using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Wolpertinger.Edge.Frontier;

public sealed record FrontierOAuthSession(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset? AccessTokenExpiresUtc)
{
    public override string ToString()
        => $"FrontierOAuthSession {{ AccessToken = [REDACTED], RefreshToken = [REDACTED], AccessTokenExpiresUtc = {AccessTokenExpiresUtc:O} }}";
}

public sealed class FrontierOAuthClient
{
    private readonly FrontierOAuthOptions _options;
    private readonly HttpClient _http;
    private readonly IExternalBrowser _browser;
    private readonly object _gate = new();
    private PendingAuthorization? _pending;

    public FrontierOAuthClient(
        FrontierOAuthOptions options,
        HttpClient http,
        IExternalBrowser browser)
    {
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Validate();
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
    }

    public Task<Uri> BeginAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var verifierBytes = RandomNumberGenerator.GetBytes(32);
        var stateBytes = RandomNumberGenerator.GetBytes(32);
        var verifier = Base64Url(verifierBytes);
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var state = Base64Url(stateBytes);
        var authorization = BuildAuthorizationUri(challenge, state);

        lock (_gate)
        {
            if (_pending is not null)
            {
                CryptographicOperations.ZeroMemory(verifierBytes);
                CryptographicOperations.ZeroMemory(stateBytes);
                throw new InvalidOperationException("A Frontier authorization is already pending.");
            }
            _pending = new PendingAuthorization(verifierBytes, stateBytes);
        }

        try
        {
            _browser.Open(authorization);
            return Task.FromResult(authorization);
        }
        catch
        {
            ClearPending();
            throw;
        }
    }

    public async Task<FrontierOAuthSession> HandleCallbackAsync(
        Uri callback,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var expectedCallback = _options.ApplicationCallbackUri ?? _options.RedirectUri;
        if (!MatchesRedirect(callback, expectedCallback))
            throw new InvalidOperationException("Frontier callback redirect does not match the expected application callback URI.");

        var query = ParseQuery(callback.Query);
        if (!query.TryGetValue("state", out var state) || string.IsNullOrWhiteSpace(state))
            throw new InvalidOperationException("Frontier callback state validation failed.");

        var pending = ConsumePending(state);
        try
        {
            if (query.ContainsKey("error"))
                throw new InvalidOperationException("Frontier authorization was denied or returned an error.");
            if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
                throw new InvalidOperationException("Frontier callback is missing an authorization code.");

            return await ExchangeCodeAsync(code, pending.VerifierBytes, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pending.VerifierBytes);
            CryptographicOperations.ZeroMemory(pending.StateBytes);
        }
    }

    public Task<FrontierOAuthSession> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(refreshToken);
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["client_id"] = _options.ClientId,
        };
        return ExchangeTokenAsync(form, cancellationToken);
    }

    private async Task<FrontierOAuthSession> ExchangeCodeAsync(
        string code,
        byte[] verifierBytes,
        CancellationToken cancellationToken)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.RedirectUri.ToString(),
            ["code_verifier"] = Base64Url(verifierBytes),
        };
        return await ExchangeTokenAsync(form, cancellationToken).ConfigureAwait(false);
    }

    private async Task<FrontierOAuthSession> ExchangeTokenAsync(
        IReadOnlyDictionary<string, string> form,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _options.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is < HttpStatusCode.OK or >= HttpStatusCode.MultipleChoices)
            throw new InvalidOperationException($"Frontier token exchange failed with HTTP {(int)response.StatusCode}.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;
        var accessToken = RequiredSecret(root, "access_token");
        var refreshToken = RequiredSecret(root, "refresh_token");
        DateTimeOffset? expiresUtc = null;
        if (root.TryGetProperty("expires_in", out var expires) && expires.TryGetInt64(out var seconds) && seconds > 0)
            expiresUtc = DateTimeOffset.UtcNow.AddSeconds(seconds);
        return new FrontierOAuthSession(accessToken, refreshToken, expiresUtc);
    }

    private Uri BuildAuthorizationUri(string challenge, string state)
    {
        var query = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _options.ClientId,
            ["redirect_uri"] = _options.RedirectUri.ToString(),
            ["scope"] = _options.Scope,
            ["audience"] = _options.Audience,
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = state,
        };
        var builder = new UriBuilder(_options.AuthorizationEndpoint)
        {
            Query = string.Join("&", query.Select(pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}")),
        };
        return builder.Uri;
    }

    private PendingAuthorization ConsumePending(string state)
    {
        lock (_gate)
        {
            var pending = _pending
                ?? throw new InvalidOperationException("No Frontier authorization is pending.");
            if (!StateMatches(state, pending.StateBytes))
                throw new InvalidOperationException("Frontier callback state validation failed.");
            _pending = null;
            return pending;
        }
    }

    private void ClearPending()
    {
        PendingAuthorization? pending;
        lock (_gate)
        {
            pending = _pending;
            _pending = null;
        }
        if (pending is null) return;
        CryptographicOperations.ZeroMemory(pending.VerifierBytes);
        CryptographicOperations.ZeroMemory(pending.StateBytes);
    }

    private static bool StateMatches(string state, byte[] expected)
    {
        try
        {
            var supplied = Base64UrlDecode(state);
            return supplied.Length == expected.Length
                && CryptographicOperations.FixedTimeEquals(supplied, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool MatchesRedirect(Uri callback, Uri expected)
        => string.Equals(callback.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(callback.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            && callback.Port == expected.Port
            && string.Equals(callback.AbsolutePath, expected.AbsolutePath, StringComparison.Ordinal);

    private static string RequiredSecret(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var property)
            || property.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException("Frontier token response is missing required credential material.");
        }
        return property.GetString()!;
    }

    private static Dictionary<string, string> ParseQuery(string value)
        => value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                part => Uri.UnescapeDataString(part[0]),
                part => part.Length == 1 ? string.Empty : Uri.UnescapeDataString(part[1]),
                StringComparer.Ordinal);

    private static string Base64Url(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }

    private sealed record PendingAuthorization(byte[] VerifierBytes, byte[] StateBytes);
}
