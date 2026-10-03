using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Diagnostics;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Kernel;
using Wolpertinger.Edge.Output;
using Wolpertinger.Edge.Persistence;
using Wolpertinger.Edge.Presentation;
using Wolpertinger.Edge.Projections;

namespace Wolpertinger.Edge.Runtime;

public sealed class VerticalSliceRunner : IAsyncDisposable
{
    private readonly SegmentedEvidenceLog _evidence;
    private readonly NormalizedObservationLedger _ledger;
    private readonly KernelSupervisor _supervisor;
    private readonly ProjectionStore _projections;
    private readonly TrustedObservationDispatcher _dispatcher;

    private VerticalSliceRunner(
        SegmentedEvidenceLog evidence,
        NormalizedObservationLedger ledger,
        KernelSupervisor supervisor,
        ProjectionStore projections,
        TrustedObservationDispatcher dispatcher)
        => (_evidence, _ledger, _supervisor, _projections, _dispatcher) =
            (evidence, ledger, supervisor, projections, dispatcher);
    public IReadOnlyList<CopilotOutput> Outputs => _dispatcher.Outputs;
    public IReadOnlyList<DiagnosticEvent> Diagnostics => _dispatcher.Diagnostics;
    public FixedBytes32? FinalStateDigest => _dispatcher.FinalStateDigest;
    public KernelSupervisorDiagnostics KernelDiagnostics => _dispatcher.KernelDiagnostics;

    public static async Task<VerticalSliceRunner> OpenAsync(
        string dataDirectory,
        string kernelExecutable,
        CancellationToken cancellationToken = default,
        IPresentationPublisher? presentationPublisher = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(kernelExecutable);
        Directory.CreateDirectory(dataDirectory);

        var evidence = await SegmentedEvidenceLog.OpenAsync(
            Path.Combine(dataDirectory, "evidence"),
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var ledger = await NormalizedObservationLedger.OpenAsync(
            Path.Combine(dataDirectory, "normalized", "observations.bin"),
            cancellationToken).ConfigureAwait(false);
        var epochs = await AuthorityEpochStore.OpenAsync(
            Path.Combine(dataDirectory, "control", "authority-epochs.bin"),
            cancellationToken).ConfigureAwait(false);
        var supervisor = KernelSupervisor.Create(
            new KernelProcessOptions(kernelExecutable, TimeSpan.FromSeconds(5)),
            epochs,
            ledger);
        await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);

        var projections = await ProjectionStore.OpenAsync(
            Path.Combine(dataDirectory, "projections.db"),
            cancellationToken).ConfigureAwait(false);
        var dispatcher = new TrustedObservationDispatcher(
            ledger,
            supervisor,
            projections,
            presentationPublisher ?? NullPresentationPublisher.Instance);
        await dispatcher.RestoreIdentityFromLedgerAsync(cancellationToken).ConfigureAwait(false);

        return new VerticalSliceRunner(
            evidence,
            ledger,
            supervisor,
            projections,
            dispatcher);
    }

    public async Task ProcessJournalLineAsync(
        ReadOnlyMemory<byte> line,
        CancellationToken cancellationToken = default)
    {
        if (line.IsEmpty)
            throw new ArgumentException("Journal line is empty.", nameof(line));
        if (_supervisor.Lifecycle != KernelSupervisorLifecycle.Synchronized)
        {
            throw new InvalidOperationException(
                $"Ingest is unavailable while trusted authority is {_supervisor.Lifecycle}; reopen and replay if faulted.");
        }

        var receipt = await _evidence.AppendAsync(
            new RawEvidenceInput(
                RawEvidenceSourceKind.LocalJournal,
                line,
                DateTimeOffset.UtcNow),
            cancellationToken).ConfigureAwait(false);
        await _dispatcher.ProcessJournalEvidenceAsync(
            receipt,
            line,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _dispatcher.DisposeAsync().ConfigureAwait(false);
        await _projections.DisposeAsync().ConfigureAwait(false);
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        await _ledger.DisposeAsync().ConfigureAwait(false);
        await _evidence.DisposeAsync().ConfigureAwait(false);
    }
}
