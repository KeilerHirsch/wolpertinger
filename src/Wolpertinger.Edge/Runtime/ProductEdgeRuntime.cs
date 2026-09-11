using Wolpertinger.Edge.Diagnostics;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Kernel;
using Wolpertinger.Edge.Output;
using Wolpertinger.Edge.Persistence;
using Wolpertinger.Edge.Presentation;
using Wolpertinger.Edge.Projections;
using Wolpertinger.Edge.Telemetry;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Edge.Runtime;

public sealed class ProductEdgeRuntime : IAsyncDisposable
{
    private readonly EncryptedSegmentedEvidenceLog _evidence;
    private readonly NormalizedObservationLedger _ledger;
    private readonly KernelSupervisor _supervisor;
    private readonly ProjectionStore _projections;
    private readonly TrustedObservationDispatcher _dispatcher;
    private readonly CancellationTokenSource _presentationCancellation;
    private readonly Task _presentationTask;
    private readonly SemaphoreSlim _ingestGate = new(1, 1);
    private bool _disposed;

    private ProductEdgeRuntime(
        EncryptedSegmentedEvidenceLog evidence,
        NormalizedObservationLedger ledger,
        KernelSupervisor supervisor,
        ProjectionStore projections,
        TrustedObservationDispatcher dispatcher,
        CancellationTokenSource presentationCancellation,
        Task presentationTask)
    {
        _evidence = evidence;
        _ledger = ledger;
        _supervisor = supervisor;
        _projections = projections;
        _dispatcher = dispatcher;
        _presentationCancellation = presentationCancellation;
        _presentationTask = presentationTask;
    }

    public IReadOnlyList<CopilotOutput> Outputs => _dispatcher.Outputs;
    public IReadOnlyList<DiagnosticEvent> Diagnostics => _dispatcher.Diagnostics;
    public KernelSupervisorDiagnostics KernelDiagnostics => _dispatcher.KernelDiagnostics;
    public Wolpertinger.Edge.Contracts.FixedBytes32? FinalStateDigest => _dispatcher.FinalStateDigest;

    public static Task<ProductEdgeRuntime> OpenAsync(
        ProductRuntimePaths paths,
        string kernelExecutable,
        IEvidenceKeyProtector keyProtector,
        string presentationPipeName = PresentationProtocol.DefaultPipeName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kernelExecutable);
        return OpenCoreAsync(
            paths,
            keyProtector,
            (ledger, epochs) => KernelSupervisor.Create(
                new KernelProcessOptions(kernelExecutable, TimeSpan.FromSeconds(5)),
                (AuthorityEpochStore)epochs,
                ledger),
            presentationPipeName,
            cancellationToken);
    }

    internal static Task<ProductEdgeRuntime> OpenForTestsAsync(
        ProductRuntimePaths paths,
        IEvidenceKeyProtector keyProtector,
        Func<NormalizedObservationLedger, IAuthorityEpochStore, KernelSupervisor> supervisorFactory,
        string presentationPipeName,
        CancellationToken cancellationToken = default)
        => OpenCoreAsync(
            paths,
            keyProtector,
            supervisorFactory,
            presentationPipeName,
            cancellationToken);

    private static async Task<ProductEdgeRuntime> OpenCoreAsync(
        ProductRuntimePaths paths,
        IEvidenceKeyProtector keyProtector,
        Func<NormalizedObservationLedger, IAuthorityEpochStore, KernelSupervisor> supervisorFactory,
        string presentationPipeName,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(keyProtector);
        ArgumentNullException.ThrowIfNull(supervisorFactory);
        ArgumentException.ThrowIfNullOrWhiteSpace(presentationPipeName);

        var keyStore = new EvidenceKeyStore(paths.EvidenceKeyPath, keyProtector);
        var evidence = await EncryptedSegmentedEvidenceLog.OpenAsync(
            paths.EvidenceDirectory, keyStore, cancellationToken: cancellationToken).ConfigureAwait(false);
        var ledger = await NormalizedObservationLedger.OpenAsync(
            paths.NormalizedLedgerPath, cancellationToken).ConfigureAwait(false);
        var epochs = await AuthorityEpochStore.OpenAsync(
            paths.AuthorityEpochPath, cancellationToken).ConfigureAwait(false);
        var supervisor = supervisorFactory(ledger, epochs);
        ProjectionStore? projections = null;
        CancellationTokenSource? presentationCancellation = null;
        try
        {
            await supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
            projections = await ProjectionStore.OpenAsync(
                paths.ProjectionDatabasePath, cancellationToken).ConfigureAwait(false);
            var publisher = new PresentationStatePublisher();
            var dispatcher = new TrustedObservationDispatcher(
                ledger, supervisor, projections, publisher);
            await dispatcher.RestoreIdentityFromLedgerAsync(cancellationToken).ConfigureAwait(false);

            presentationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var pipeServer = new PresentationPipeServer(publisher, presentationPipeName);
            var presentationTask = pipeServer.RunAsync(presentationCancellation.Token);
            return new ProductEdgeRuntime(
                evidence,
                ledger,
                supervisor,
                projections,
                dispatcher,
                presentationCancellation,
                presentationTask);
        }
        catch
        {
            if (presentationCancellation is not null)
            {
                presentationCancellation.Cancel();
                presentationCancellation.Dispose();
            }
            if (projections is not null)
            {
                await projections.DisposeAsync().ConfigureAwait(false);
            }
            await supervisor.DisposeAsync().ConfigureAwait(false);
            await ledger.DisposeAsync().ConfigureAwait(false);
            await evidence.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task ProcessJournalRecordAsync(
        JournalSourceRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Payload.Length == 0)
            throw new ArgumentException("Journal record payload is empty.", nameof(record));

        await _ingestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_supervisor.Lifecycle != KernelSupervisorLifecycle.Synchronized)
            {
                throw new InvalidOperationException(
                    $"Ingest is unavailable while trusted authority is {_supervisor.Lifecycle}; reopen and replay if faulted.");
            }

            var receipt = await _evidence.AppendAsync(
                new RawEvidenceInput(
                    RawEvidenceSourceKind.LocalJournal,
                    record.Payload,
                    DateTimeOffset.UtcNow,
                    record.Locator),
                cancellationToken).ConfigureAwait(false);
            await _dispatcher.ProcessJournalEvidenceAsync(
                receipt, record.Payload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ingestGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        await _ingestGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
        }
        finally
        {
            _ingestGate.Release();
        }

        _presentationCancellation.Cancel();
        try
        {
            await _presentationTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_presentationCancellation.IsCancellationRequested)
        {
        }

        await _dispatcher.DisposeAsync().ConfigureAwait(false);
        await _projections.DisposeAsync().ConfigureAwait(false);
        await _supervisor.DisposeAsync().ConfigureAwait(false);
        await _ledger.DisposeAsync().ConfigureAwait(false);
        await _evidence.DisposeAsync().ConfigureAwait(false);
        _presentationCancellation.Dispose();
        _ingestGate.Dispose();
    }
}
