using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Persistence;

namespace Wolpertinger.Edge.Kernel;

public enum KernelSupervisorLifecycle : byte
{
    Cold = 0,
    Starting = 1,
    Recovering = 2,
    Synchronized = 3,
    Degraded = 4,
    Faulted = 5,
    Stopped = 6,
}

public sealed record KernelSupervisorDiagnostics(
    ulong Epoch,
    int? ActiveProcessId,
    int? ShadowProcessId,
    ObservationCursor? LastAgreedCursor,
    FixedBytes32? LastAgreedDigest,
    KernelSupervisorLifecycle Lifecycle);

public sealed class KernelSupervisor : IAsyncDisposable
{
    private IKernelProcessClient _active;
    private IKernelProcessClient _shadow;
    private readonly IAuthorityEpochStore _epochs;
    private readonly IObservationReplaySource? _replay;
    private readonly IKernelProcessClientFactory? _factory;
    private readonly bool _recoverOnStart;
    private readonly SemaphoreSlim _lane = new(1, 1);
    private ObservationCursor? _lastAgreedCursor;
    private FixedBytes32? _lastAgreedDigest;
    private bool _started;

    internal KernelSupervisor(IKernelProcessClient active, IKernelProcessClient shadow, IAuthorityEpochStore epochs)
        : this(active, shadow, epochs, null, null) { }
    internal KernelSupervisor(
        IKernelProcessClient active,
        IKernelProcessClient shadow,
        IAuthorityEpochStore epochs,
        IObservationReplaySource? replay,
        IKernelProcessClientFactory? factory,
        bool recoverOnStart = true)
    {
        _active = active;
        _shadow = shadow;
        _epochs = epochs;
        _replay = replay;
        _factory = factory;
        _recoverOnStart = recoverOnStart;
    }

    public ulong CurrentEpoch { get; private set; }
    public KernelSupervisorLifecycle Lifecycle { get; private set; } = KernelSupervisorLifecycle.Cold;
    public KernelSupervisorDiagnostics Diagnostics
        => new(CurrentEpoch, _active.ProcessId, _shadow.ProcessId, _lastAgreedCursor, _lastAgreedDigest, Lifecycle);

    public static KernelSupervisor Create(
        KernelProcessOptions options,
        AuthorityEpochStore epochs,
        IObservationReplaySource replay)
    {
        var factory = new KernelProcessClientFactory(options);
        var internalFactory = (IKernelProcessClientFactory)factory;
        return new KernelSupervisor(
            internalFactory.Create(), internalFactory.Create(), epochs, replay, internalFactory);
    }

    internal static KernelSupervisor CreateForExactReplay(
        KernelProcessOptions options,
        AuthorityEpochStore epochs,
        IObservationReplaySource replay)
    {
        var factory = new KernelProcessClientFactory(options);
        var internalFactory = (IKernelProcessClientFactory)factory;
        return new KernelSupervisor(
            internalFactory.Create(), internalFactory.Create(), epochs, replay, internalFactory, recoverOnStart: false);
    }
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_started) throw new InvalidOperationException("Kernel supervisor is already started.");
        if (Lifecycle == KernelSupervisorLifecycle.Stopped)
            throw new InvalidOperationException("Kernel supervisor is stopped.");

        Lifecycle = KernelSupervisorLifecycle.Starting;
        try
        {
            CurrentEpoch = await _epochs.NextAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(
                _active.StartAsync(cancellationToken),
                _shadow.StartAsync(cancellationToken)).ConfigureAwait(false);
            await Task.WhenAll(
                _active.SetRoleAsync(CurrentEpoch, KernelRole.Active, cancellationToken),
                _shadow.SetRoleAsync(CurrentEpoch, KernelRole.Shadow, cancellationToken)).ConfigureAwait(false);

            if (_recoverOnStart && _replay is not null)
            {
                Lifecycle = KernelSupervisorLifecycle.Recovering;
                await foreach (var observation in _replay.ReadObservationsAsync(null, cancellationToken).ConfigureAwait(false))
                {
                    var result = await ApplyToBothAsync(observation, cancellationToken).ConfigureAwait(false);
                    if (!IsCommittedStateResult(result.Status))
                        throw new InvalidDataException($"Startup replay failed closed with kernel status {result.Status}.");
                }
            }

            _started = true;
            Lifecycle = KernelSupervisorLifecycle.Synchronized;
        }
        catch
        {
            Lifecycle = KernelSupervisorLifecycle.Faulted;
            throw;
        }
    }

    public async Task<KernelApplyResult> ApplyAsync(
        ObservationEnvelope observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        if (!_started) throw new InvalidOperationException("Kernel supervisor is not started.");
        if (Lifecycle == KernelSupervisorLifecycle.Faulted)
            throw new InvalidOperationException("Kernel supervisor is faulted; reopen and replay before further ingest.");

        await _lane.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Lifecycle != KernelSupervisorLifecycle.Synchronized)
                throw new InvalidOperationException($"Kernel supervisor is not synchronized ({Lifecycle}).");

            if (!_active.IsHealthy)
            {
                Lifecycle = KernelSupervisorLifecycle.Recovering;
                await PromoteShadowAndRejoinAsync(cancellationToken).ConfigureAwait(false);
                Lifecycle = KernelSupervisorLifecycle.Synchronized;
            }

            if (!_shadow.IsHealthy)
            {
                Lifecycle = KernelSupervisorLifecycle.Degraded;
                var degradedResult = await ApplyWithActiveThenRejoinShadowAsync(observation, cancellationToken).ConfigureAwait(false);
                Lifecycle = KernelSupervisorLifecycle.Synchronized;
                return degradedResult;
            }

            var result = await ApplyToBothAsync(observation, cancellationToken).ConfigureAwait(false);
            Lifecycle = KernelSupervisorLifecycle.Synchronized;
            return result;
        }
        catch
        {
            Lifecycle = KernelSupervisorLifecycle.Faulted;
            throw;
        }
        finally { _lane.Release(); }
    }
    private async Task<KernelApplyResult> ApplyToBothAsync(
        ObservationEnvelope observation,
        CancellationToken cancellationToken)
    {
        var activeTask = _active.ApplyAsync(observation, cancellationToken);
        var shadowTask = _shadow.ApplyAsync(observation, cancellationToken);
        await Task.WhenAll(activeTask, shadowTask).ConfigureAwait(false);
        var active = await activeTask.ConfigureAwait(false);
        var shadow = await shadowTask.ConfigureAwait(false);
        ValidateFencing(active, shadow);
        ValidateAgreement(active, shadow);
        EnsureCommittedStateResult(active);
        RecordAgreement(active);
        return active;
    }

    private async Task<KernelApplyResult> ApplyWithActiveThenRejoinShadowAsync(
        ObservationEnvelope observation,
        CancellationToken cancellationToken)
    {
        EnsureRecoveryConfigured();
        var active = await _active.ApplyAsync(observation, cancellationToken).ConfigureAwait(false);
        ValidateActiveFencing(active);
        EnsureCommittedStateResult(active);
        var targetCursor = active.Cursor;
        var targetDigest = IsCommittedStateResult(active.Status) ? active.StateDigest : _lastAgreedDigest;
        var shadow = await ReplaceShadowAsync(targetCursor, targetDigest, cancellationToken).ConfigureAwait(false);
        if (shadow is not null)
            ValidateAgreement(active, shadow);
        if (IsCommittedStateResult(active.Status)) RecordAgreement(active);
        return active;
    }
    private async Task PromoteShadowAndRejoinAsync(CancellationToken cancellationToken)
    {
        EnsureRecoveryConfigured();
        if (!_shadow.IsHealthy)
            throw new InvalidOperationException("Both trusted kernels are unavailable; fail closed.");
        if (_lastAgreedCursor is null || _lastAgreedDigest is null)
            throw new InvalidOperationException("Active failure before an agreed state cannot be promoted safely.");

        var oldActive = _active;
        var promoted = _shadow;
        var newEpoch = await _epochs.NextAsync(cancellationToken).ConfigureAwait(false);
        await promoted.SetRoleAsync(newEpoch, KernelRole.Active, cancellationToken).ConfigureAwait(false);
        CurrentEpoch = newEpoch;

        var replacement = _factory!.Create();
        await replacement.StartAsync(cancellationToken).ConfigureAwait(false);
        await replacement.SetRoleAsync(CurrentEpoch, KernelRole.Shadow, cancellationToken).ConfigureAwait(false);
        await ReplayToAsync(replacement, _lastAgreedCursor.Value, _lastAgreedDigest.Value, cancellationToken)
            .ConfigureAwait(false);

        _active = promoted;
        _shadow = replacement;
        await oldActive.DisposeAsync().ConfigureAwait(false);
    }

    private async Task<KernelApplyResult?> ReplaceShadowAsync(
        ObservationCursor? targetCursor,
        FixedBytes32? targetDigest,
        CancellationToken cancellationToken)
    {
        EnsureRecoveryConfigured();
        var oldShadow = _shadow;
        var replacement = _factory!.Create();
        await replacement.StartAsync(cancellationToken).ConfigureAwait(false);
        await replacement.SetRoleAsync(CurrentEpoch, KernelRole.Shadow, cancellationToken).ConfigureAwait(false);
        KernelApplyResult? replayResult = null;
        if (targetCursor is not null && targetDigest is not null)
            replayResult = await ReplayToAsync(replacement, targetCursor.Value, targetDigest.Value, cancellationToken).ConfigureAwait(false);
        _shadow = replacement;
        await oldShadow.DisposeAsync().ConfigureAwait(false);
        return replayResult;
    }
    private async Task<KernelApplyResult> ReplayToAsync(
        IKernelProcessClient client,
        ObservationCursor targetCursor,
        FixedBytes32 targetDigest,
        CancellationToken cancellationToken)
    {
        KernelApplyResult? last = null;
        await foreach (var observation in _replay!.ReadObservationsAsync(null, cancellationToken).ConfigureAwait(false))
        {
            if (Compare(observation.Cursor, targetCursor) > 0) break;
            var result = await client.ApplyAsync(observation, cancellationToken).ConfigureAwait(false);
            if (result.Epoch != CurrentEpoch || result.Role != KernelRole.Shadow)
                throw new InvalidDataException("Rejoining Shadow returned stale epoch or wrong role.");
            if (result.Status is not (KernelResponseStatus.Ok or KernelResponseStatus.Idempotent))
                throw new InvalidDataException($"Replay failed closed with kernel status {result.Status}.");
            last = result;
            if (result.Cursor == targetCursor) break;
        }

        if (last is null || last.Cursor != targetCursor || last.StateDigest != targetDigest)
            throw new KernelDivergenceException("Rejoining Shadow did not reach the agreed cursor/state digest.");
        return last;
    }

    private void EnsureRecoveryConfigured()
    {
        if (_replay is null || _factory is null)
            throw new InvalidOperationException("Kernel recovery requires replay source and process factory.");
    }

    private static bool IsCommittedStateResult(KernelResponseStatus status)
        => status is KernelResponseStatus.Ok or KernelResponseStatus.Idempotent;

    private static void EnsureCommittedStateResult(KernelApplyResult result)
    {
        if (!IsCommittedStateResult(result.Status))
            throw new InvalidDataException($"Trusted kernels agreed on non-committed status {result.Status}; fail closed.");
    }

    private void RecordAgreement(KernelApplyResult result)
    {
        if (!IsCommittedStateResult(result.Status) || result.Cursor is null) return;
        _lastAgreedCursor = result.Cursor;
        _lastAgreedDigest = result.StateDigest;
    }
    private void ValidateFencing(KernelApplyResult active, KernelApplyResult shadow)
    {
        ValidateActiveFencing(active);
        if (shadow.Epoch != CurrentEpoch)
            throw new InvalidDataException("Shadow kernel response epoch does not match supervisor epoch.");
        if (shadow.Role != KernelRole.Shadow)
            throw new InvalidDataException("Shadow kernel response role does not self-identify as SHADOW.");
    }

    private void ValidateActiveFencing(KernelApplyResult active)
    {
        if (active.Epoch != CurrentEpoch)
            throw new InvalidDataException("Active kernel response epoch does not match supervisor epoch.");
        if (active.Role != KernelRole.Active)
            throw new InvalidDataException("Active kernel response role does not self-identify as ACTIVE.");
    }

    private static void ValidateAgreement(KernelApplyResult active, KernelApplyResult shadow)
    {
        if (active.Cursor != shadow.Cursor)
            throw new KernelDivergenceException("Active/Shadow cursor divergence.");
        if (active.StateDigest != shadow.StateDigest)
            throw new KernelDivergenceException("Active/Shadow state-digest divergence.");
        if (active.Status != shadow.Status)
            throw new KernelDivergenceException("Active/Shadow status divergence.");
        if (!Equals(active.JumpFact, shadow.JumpFact))
            throw new KernelDivergenceException("Active/Shadow JumpFact divergence.");
        if (!Equals(active.CommanderVesselFact, shadow.CommanderVesselFact))
            throw new KernelDivergenceException("Active/Shadow Commander/Vessel fact divergence.");
    }

    private static int Compare(ObservationCursor left, ObservationCursor right)
        => left.EvidenceSequence != right.EvidenceSequence
            ? left.EvidenceSequence.CompareTo(right.EvidenceSequence)
            : left.MessageOrdinal.CompareTo(right.MessageOrdinal);

    public async ValueTask DisposeAsync()
    {
        await _active.DisposeAsync().ConfigureAwait(false);
        if (!ReferenceEquals(_active, _shadow)) await _shadow.DisposeAsync().ConfigureAwait(false);
        await _epochs.DisposeAsync().ConfigureAwait(false);
        _lane.Dispose();
    }
}
