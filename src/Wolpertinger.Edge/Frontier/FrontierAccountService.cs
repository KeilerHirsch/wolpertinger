using System.Net;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Edge.Frontier;

public sealed record FrontierAccountStatus(
    FrontierAccountState State,
    PresentationFreshness Freshness,
    long? LastSuccessUnixMs,
    string ReasonCode);

public interface IFrontierAccountStateSource
{
    FrontierAccountStatus Current { get; }
    event Action<FrontierAccountStatus>? StatusChanged;
}

public sealed class FrontierAccountService : IFrontierAccountStateSource
{
    public static readonly TimeSpan MinimumAutomaticRefreshInterval = TimeSpan.FromSeconds(60);
    public static readonly IReadOnlyList<TimeSpan> RetryDelays =
    [
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60),
        TimeSpan.FromSeconds(120),
    ];

    private readonly FrontierOAuthClient _oauth;
    private readonly FrontierTokenStore _tokenStore;
    private readonly FrontierCapiClient _capi;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FrontierOAuthSession? _session;
    private DateTimeOffset? _lastProfileAttemptUtc;
    private int _consecutiveRetryableFailures;
    private FrontierAccountStatus _current = new(
        FrontierAccountState.Disconnected,
        PresentationFreshness.Unknown,
        null,
        "Disconnected");

    public FrontierAccountService(
        FrontierOAuthClient oauth,
        FrontierTokenStore tokenStore,
        FrontierCapiClient capi,
        Func<DateTimeOffset>? clock = null)
    {
        _oauth = oauth ?? throw new ArgumentNullException(nameof(oauth));
        _tokenStore = tokenStore ?? throw new ArgumentNullException(nameof(tokenStore));
        _capi = capi ?? throw new ArgumentNullException(nameof(capi));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public FrontierAccountStatus Current => Volatile.Read(ref _current);
    public event Action<FrontierAccountStatus>? StatusChanged;

    public async Task<Uri> ConnectAsync(CancellationToken cancellationToken = default)
    {
        Publish(new FrontierAccountStatus(
            FrontierAccountState.Connecting,
            Current.Freshness,
            Current.LastSuccessUnixMs,
            "Connecting"));
        return await _oauth.BeginAuthorizationAsync(cancellationToken).ConfigureAwait(false);
    }
    public async Task HandleCallbackAsync(
        Uri callback,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var session = await _oauth.HandleCallbackAsync(callback, cancellationToken)
                .ConfigureAwait(false);
            await PersistSessionAsync(session, cancellationToken).ConfigureAwait(false);
            _session = session;
            _consecutiveRetryableFailures = 0;
            Publish(new FrontierAccountStatus(
                FrontierAccountState.Connected,
                PresentationFreshness.Unknown,
                Current.LastSuccessUnixMs,
                "Connected"));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _session = null;
            _lastProfileAttemptUtc = null;
            _consecutiveRetryableFailures = 0;
            await _tokenStore.DeleteAsync(cancellationToken).ConfigureAwait(false);
            Publish(new FrontierAccountStatus(
                FrontierAccountState.Disconnected,
                PresentationFreshness.Unknown,
                null,
                "Disconnected"));
        }
        finally { _gate.Release(); }
    }
    public async Task<FrontierProfileSnapshot?> RefreshProfileAsync(
        bool automatic = false,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _clock();
            var automaticDelay = _consecutiveRetryableFailures == 0
                ? MinimumAutomaticRefreshInterval
                : RetryDelays[Math.Min(_consecutiveRetryableFailures - 1, RetryDelays.Count - 1)];
            if (automatic
                && _lastProfileAttemptUtc is DateTimeOffset last
                && now - last < automaticDelay)
            {
                return null;
            }
            _lastProfileAttemptUtc = now;

            var session = await EnsureSessionAsync(cancellationToken).ConfigureAwait(false);
            if (session is null)
                return null;

            try
            {
                return await FetchAndPublishSuccessAsync(session, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (FrontierCapiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
            {
                return await HandleUnauthorizedAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (FrontierCapiException)
            {
                PublishUnavailable("CapiUnavailable");
                return null;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                PublishUnavailable("CapiTimeout");
                return null;
            }
            catch (HttpRequestException)
            {
                PublishUnavailable("NetworkUnavailable");
                return null;
            }
        }
        finally { _gate.Release(); }
    }
    private async Task<FrontierOAuthSession?> EnsureSessionAsync(
        CancellationToken cancellationToken)
    {
        if (_session is not null)
            return _session;

        var stored = await _tokenStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            Publish(new FrontierAccountStatus(
                FrontierAccountState.Disconnected,
                PresentationFreshness.Unknown,
                Current.LastSuccessUnixMs,
                "NoCredential"));
            return null;
        }

        try
        {
            var refreshed = await _oauth.RefreshAsync(stored.RefreshToken, cancellationToken)
                .ConfigureAwait(false);
            await PersistSessionAsync(refreshed, cancellationToken).ConfigureAwait(false);
            _session = refreshed;
            return refreshed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            PublishReauthenticationRequired();
            return null;
        }
    }

    private async Task<FrontierProfileSnapshot?> HandleUnauthorizedAsync(
        CancellationToken cancellationToken)
    {
        var stored = await _tokenStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (stored is null)
        {
            PublishReauthenticationRequired();
            return null;
        }
        FrontierOAuthSession refreshed;
        try
        {
            refreshed = await _oauth.RefreshAsync(stored.RefreshToken, cancellationToken)
                .ConfigureAwait(false);
            await PersistSessionAsync(refreshed, cancellationToken).ConfigureAwait(false);
            _session = refreshed;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            PublishReauthenticationRequired();
            return null;
        }

        try
        {
            return await FetchAndPublishSuccessAsync(refreshed, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (FrontierCapiException ex) when (ex.StatusCode == HttpStatusCode.Unauthorized)
        {
            PublishReauthenticationRequired();
            return null;
        }
        catch (FrontierCapiException)
        {
            PublishUnavailable("CapiUnavailable");
            return null;
        }
    }
    private async Task<FrontierProfileSnapshot> FetchAndPublishSuccessAsync(
        FrontierOAuthSession session,
        CancellationToken cancellationToken)
    {
        var snapshot = await _capi.GetProfileAsync(session.AccessToken, cancellationToken)
            .ConfigureAwait(false);
        _consecutiveRetryableFailures = 0;
        Publish(new FrontierAccountStatus(
            FrontierAccountState.Connected,
            PresentationFreshness.Current,
            snapshot.ResponseObservedUtc.ToUnixTimeMilliseconds(),
            "ProfileCurrent"));
        return snapshot;
    }

    private Task PersistSessionAsync(
        FrontierOAuthSession session,
        CancellationToken cancellationToken)
        => _tokenStore.SaveAsync(
            new FrontierTokenSet(session.RefreshToken, session.AccessTokenExpiresUtc),
            cancellationToken);

    private void PublishUnavailable(string reasonCode)
    {
        if (_consecutiveRetryableFailures < int.MaxValue)
            _consecutiveRetryableFailures++;
        var current = Current;
        Publish(new FrontierAccountStatus(
            FrontierAccountState.Unavailable,
            current.LastSuccessUnixMs is null
                ? PresentationFreshness.Unknown
                : PresentationFreshness.Stale,
            current.LastSuccessUnixMs,
            reasonCode));
    }

    private void PublishReauthenticationRequired()
    {
        var current = Current;
        _session = null;
        _consecutiveRetryableFailures = 0;
        Publish(new FrontierAccountStatus(
            FrontierAccountState.ReauthenticationRequired,
            current.LastSuccessUnixMs is null
                ? PresentationFreshness.Unknown
                : PresentationFreshness.Stale,
            current.LastSuccessUnixMs,
            "ReauthenticationRequired"));
    }

    private void Publish(FrontierAccountStatus status)
    {
        Volatile.Write(ref _current, status);
        var handlers = StatusChanged;
        if (handlers is null)
            return;
        foreach (Action<FrontierAccountStatus> handler in handlers.GetInvocationList())
        {
            try { handler(status); }
            catch { /* Account observers cannot break the integration state machine. */ }
        }
    }
}
