using System.Text.Json;
using Wolpertinger.Edge.Context;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Diagnostics;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Journal;
using Wolpertinger.Edge.Kernel;
using Wolpertinger.Edge.Output;
using Wolpertinger.Edge.Persistence;
using Wolpertinger.Edge.Projections;
using Wolpertinger.Edge.Presentation;

namespace Wolpertinger.Edge.Runtime;

public sealed class TrustedObservationDispatcher : IAsyncDisposable
{
    private readonly NormalizedObservationLedger _ledger;
    private readonly KernelSupervisor _supervisor;
    private readonly ProjectionStore _projections;
    private readonly IPresentationPublisher _presentationPublisher;
    private readonly SessionIdentityTracker _identity = new();
    private readonly ContextDecisionEngine _context = new();
    private readonly CopilotOutputFormatter _formatter = new();
    private readonly List<CopilotOutput> _outputs = [];
    private readonly List<DiagnosticEvent> _diagnostics = [];
    public TrustedObservationDispatcher(
        NormalizedObservationLedger ledger,
        KernelSupervisor supervisor,
        ProjectionStore projections,
        IPresentationPublisher presentationPublisher)
    {
        _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));
        _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        _projections = projections ?? throw new ArgumentNullException(nameof(projections));
        _presentationPublisher = presentationPublisher ?? throw new ArgumentNullException(nameof(presentationPublisher));
    }

    public IReadOnlyList<CopilotOutput> Outputs => _outputs;
    public IReadOnlyList<DiagnosticEvent> Diagnostics => _diagnostics;
    public FixedBytes32? FinalStateDigest => _supervisor.Diagnostics.LastAgreedDigest;
    public KernelSupervisorDiagnostics KernelDiagnostics => _supervisor.Diagnostics;
    internal event Action? TrustedJumpApplied;

    public async Task ProcessJournalEvidenceAsync(
        RawEvidenceReceipt receipt,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.IsDurable)
            throw new InvalidOperationException("Journal dispatch requires durable raw evidence.");
        _ = JournalEvidenceProvenance.Map(receipt.SourceKind);
        if (_supervisor.Lifecycle != KernelSupervisorLifecycle.Synchronized)
            throw new InvalidOperationException(
                $"Ingest is unavailable while trusted authority is {_supervisor.Lifecycle}; reopen and replay if faulted.");

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            await RejectAsync(receipt, "InvalidJson", ex.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            var kind = JournalEventClassifier.Classify(root);
            SessionIdentityResult identity;
            try
            {
                identity = _identity.Observe(receipt, root);
            }
            catch (InvalidDataException ex)
            {
                await RejectAsync(receipt, "IdentityInvalid", ex.Message, cancellationToken).ConfigureAwait(false);
                return;
            }
            if (identity.Status == SessionIdentityStatus.IdentityConflict)
            {
                await RejectAsync(
                    receipt,
                    "IdentityConflict",
                    "Journal identity conflicts with bound session.",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            ObservationEnvelopeDraft? draft = identity.Draft;
            if (draft is null && kind == JournalEventKind.FsdJump)
            {
                if (_identity.CurrentBinding is null)
                {
                    await RejectAsync(
                        receipt,
                        "IdentityPending",
                        "FSDJump arrived before authoritative session binding.",
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                try
                {
                    draft = FsdJumpNormalizer.Normalize(receipt, root, _identity.CurrentBinding);
                }
                catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException)
                {
                    await RejectAsync(
                        receipt,
                        "NormalizationRejected",
                        ex.Message,
                        cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            var disposition = draft is null
                ? NormalizationDisposition.Ignored
                : NormalizationDisposition.Dispatchable;
            var commit = await _ledger.CommitAsync(
                receipt,
                disposition,
                draft,
                cancellationToken).ConfigureAwait(false);
            if (commit.Observation is not null)
            {
                await DispatchAsync(
                    commit.Observation,
                    receipt.Reference,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }
    public async Task ProcessCommanderVesselEvidenceAsync(
        RawEvidenceReceipt receipt,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.IsDurable)
            throw new InvalidOperationException("Commander/Vessel dispatch requires durable raw evidence.");
        if (receipt.SourceKind is not (RawEvidenceSourceKind.FrontierApi or RawEvidenceSourceKind.Sample))
            throw new InvalidDataException("Commander/Vessel dispatch requires FrontierApi or explicit Sample evidence.");
        if (_supervisor.Lifecycle != KernelSupervisorLifecycle.Synchronized)
            throw new InvalidOperationException(
                $"Ingest is unavailable while trusted authority is {_supervisor.Lifecycle}; reopen and replay if faulted.");
        if (_identity.CurrentBinding is null)
        {
            await RejectAsync(
                receipt,
                "IdentityPending",
                "Commander/Vessel profile arrived before authoritative session binding.",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            await RejectAsync(receipt, "InvalidJson", ex.Message, cancellationToken).ConfigureAwait(false);
            return;
        }

        using (document)
        {
            ObservationEnvelopeDraft draft;
            try
            {
                draft = Frontier.FrontierProfileNormalizer.Normalize(
                    receipt, document.RootElement, _identity.CurrentBinding);
            }
            catch (Exception ex) when (ex is InvalidDataException or FormatException or OverflowException)
            {
                await RejectAsync(
                    receipt,
                    "NormalizationRejected",
                    ex.Message,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var commit = await _ledger.CommitAsync(
                receipt,
                NormalizationDisposition.Dispatchable,
                draft,
                cancellationToken).ConfigureAwait(false);
            if (commit.Observation is not null)
            {
                await DispatchAsync(
                    commit.Observation,
                    receipt.Reference,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task DispatchAsync(
        ObservationEnvelope observation,
        EvidenceReference reference,
        CancellationToken cancellationToken)
    {
        var result = await _supervisor.ApplyAsync(observation, cancellationToken).ConfigureAwait(false);
        if (result.Status == KernelResponseStatus.Idempotent) return;
        if (result.Status != KernelResponseStatus.Ok)
        {
            _diagnostics.Add(new DiagnosticEvent(
                "KernelRejected", reference.RawOrdinal, result.Status.ToString()));
            return;
        }

        if (observation.Kind == ObservationKind.CommanderVessel)
        {
            var commander = CommanderVesselFactFactory.Create(observation, result);
            try
            {
                await _presentationPublisher.PublishCommanderVesselAsync(
                    commander, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _diagnostics.Add(new DiagnosticEvent(
                    "PresentationPublishFailed", reference.RawOrdinal, ex.Message));
            }
            return;
        }

        if (observation.Kind != ObservationKind.FsdJump) return;
        var fact = JumpFactFactory.Create(observation, reference, result);
        TrustedJumpApplied?.Invoke();
        var decision = _context.Decide(fact);
        if (!decision.Surface) return;

        try
        {
            await _presentationPublisher.PublishJumpAsync(
                fact, decision, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _diagnostics.Add(new DiagnosticEvent(
                "PresentationPublishFailed", reference.RawOrdinal, ex.Message));
        }
        var output = _formatter.Format(fact, decision);
        await _projections.ApplyAsync(output, cancellationToken).ConfigureAwait(false);
        _outputs.Add(output);
    }

    public async Task RestoreIdentityFromLedgerAsync(
        CancellationToken cancellationToken = default)
    {
        SessionBinding? binding = null;
        await foreach (var observation in _ledger
            .ReadObservationsAsync(null, cancellationToken)
            .ConfigureAwait(false))
        {
            if (observation.Kind == ObservationKind.SessionBound)
            {
                binding = new SessionBinding(observation.SessionId, observation.Profile);
            }
        }

        if (binding is not null)
        {
            _identity.Restore(binding);
        }
    }

    private async Task RejectAsync(
        RawEvidenceReceipt receipt,
        string code,
        string message,
        CancellationToken cancellationToken)
    {
        await _ledger.CommitAsync(
            receipt,
            NormalizationDisposition.Rejected,
            null,
            cancellationToken).ConfigureAwait(false);
        _diagnostics.Add(new DiagnosticEvent(code, receipt.Reference.RawOrdinal, message));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
