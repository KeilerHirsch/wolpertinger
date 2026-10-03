using System.Security.Cryptography;
using System.Text;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Kernel;
using Wolpertinger.Edge.Persistence;
using Wolpertinger.Edge.Presentation;
using Wolpertinger.Edge.Projections;
using Wolpertinger.Edge.Runtime;

namespace Wolpertinger.Edge.Tests.Runtime;

public sealed class TrustedObservationDispatcherTests
{
    [Fact]
    public async Task FixturePreservesFrozenDispositionsOutputAndPresentation()
    {
        await using var harness = await Harness.OpenAsync();
        var evidenceDir = Path.Combine(harness.Root, "evidence-v1");
        await using var evidence = await SegmentedEvidenceLog.OpenAsync(evidenceDir, 4096);
        foreach (var line in FixtureLines())
        {
            var payload = Encoding.UTF8.GetBytes(line);
            var receipt = await evidence.AppendAsync(new RawEvidenceInput(
                RawEvidenceSourceKind.LocalJournal, payload, DateTimeOffset.UtcNow));
            await harness.Dispatcher.ProcessJournalEvidenceAsync(receipt, payload);
        }

        Assert.Equal(new[]
            {
                NormalizationDisposition.Ignored,
                NormalizationDisposition.Dispatchable,
                NormalizationDisposition.Ignored,
                NormalizationDisposition.Ignored,
                NormalizationDisposition.Dispatchable,
            },
            harness.Ledger.Entries.Select(entry => entry.Disposition).ToArray());
        var output = Assert.Single(harness.Dispatcher.Outputs);
        Assert.Equal("Jump complete: W. Grantler NX-42 - 55.359 ly, fuel 27.123 t.", output.Text);
        Assert.Equal(2UL, output.Cursor.EvidenceSequence);
        Assert.Empty(harness.Dispatcher.Diagnostics);
        Assert.NotNull(harness.Dispatcher.FinalStateDigest);
        Assert.Equal(harness.Dispatcher.FinalStateDigest, output.StateDigest);
        var presentation = Assert.IsType<JumpPresentation>(harness.Publisher.Current.Jump);
        Assert.Equal(2UL, presentation.Cursor.EvidenceSequence);
        Assert.Equal("W. Grantler NX-42", presentation.StarSystem);
    }

    [Fact]
    public async Task NonDurableReceiptIsRejectedBeforeLedgerMutation()
    {
        await using var harness = await Harness.OpenAsync();
        var receipt = new RawEvidenceReceipt(
            new EvidenceReference(0, 0, 0, 1),
            RawEvidenceSourceKind.LocalJournal,
            FixedBytes32.FromBytes(SHA256.HashData("raw"u8)),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            IsDurable: false);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Dispatcher.ProcessJournalEvidenceAsync(receipt, "{}"u8.ToArray()));
        Assert.Empty(harness.Ledger.Entries);
        Assert.Empty(harness.Dispatcher.Outputs);
    }

    private static IEnumerable<string> FixtureLines()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null
               && !File.Exists(Path.Combine(directory.FullName, "WOLPERTINGER.slnx")))
        {
            directory = directory.Parent;
        }

        var root = directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root not found.");
        return File.ReadLines(Path.Combine(
            root, "fixtures", "journal", "live-v4-fsdjump-session.jsonl"));
    }

    private sealed class Harness : IAsyncDisposable
    {
        private Harness(
            string root,
            NormalizedObservationLedger ledger,
            KernelSupervisor supervisor,
            ProjectionStore projections,
            PresentationStatePublisher publisher,
            TrustedObservationDispatcher dispatcher)
        {
            Root = root;
            Ledger = ledger;
            Supervisor = supervisor;
            Projections = projections;
            Publisher = publisher;
            Dispatcher = dispatcher;
        }

        public string Root { get; }
        public NormalizedObservationLedger Ledger { get; }
        public KernelSupervisor Supervisor { get; }
        public ProjectionStore Projections { get; }
        public PresentationStatePublisher Publisher { get; }
        public TrustedObservationDispatcher Dispatcher { get; }

        public static async Task<Harness> OpenAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "wolpertinger-dispatcher-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var ledger = await NormalizedObservationLedger.OpenAsync(
                Path.Combine(root, "normalized.bin"));
            var epochs = new FakeEpochStore();
            var supervisor = new KernelSupervisor(
                new FakeKernelClient(),
                new FakeKernelClient(),
                epochs);
            await supervisor.StartAsync();
            var projections = await ProjectionStore.OpenAsync(
                Path.Combine(root, "projections.db"));
            var publisher = new PresentationStatePublisher();
            var dispatcher = new TrustedObservationDispatcher(
                ledger,
                supervisor,
                projections,
                publisher);
            return new Harness(root, ledger, supervisor, projections, publisher, dispatcher);
        }

        public async ValueTask DisposeAsync()
        {
            await Dispatcher.DisposeAsync();
            await Projections.DisposeAsync();
            await Supervisor.DisposeAsync();
            await Ledger.DisposeAsync();
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FakeEpochStore : IAuthorityEpochStore
    {
        private ulong _epoch;

        public Task<ulong> NextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(++_epoch);

        public ValueTask DisposeAsync()
            => ValueTask.CompletedTask;
    }

    private sealed class FakeKernelClient : IKernelProcessClient
    {
        private ulong _epoch;
        private KernelRole _role;
        private bool _started;

        public bool IsHealthy => _started;
        public int? ProcessId => null;

        public Task StartAsync(CancellationToken cancellationToken = default)
        {
            _started = true;
            return Task.CompletedTask;
        }

        public Task SetRoleAsync(
            ulong epoch,
            KernelRole role,
            CancellationToken cancellationToken = default)
        {
            _epoch = epoch;
            _role = role;
            return Task.CompletedTask;
        }

        public Task<KernelApplyResult> ApplyAsync(
            ObservationEnvelope observation,
            CancellationToken cancellationToken = default)
        {
            var digest = FixedBytes32.FromBytes(
                SHA256.HashData(CborContractCodec.Encode(observation)));
            KernelJumpFact? jumpFact = null;
            if (observation.Payload is FsdJumpPayload jump)
            {
                jumpFact = new KernelJumpFact(
                    observation.Cursor,
                    jump.SystemAddress,
                    jump.StarSystem,
                    jump.Position,
                    jump.JumpDistance,
                    jump.FuelUsed,
                    jump.FuelLevel,
                    SourceProvenance.LocalJournal,
                    FreshnessState.Current,
                    SourceProvenance.LocalJournal,
                    FreshnessState.Current);
            }

            return Task.FromResult(new KernelApplyResult(
                KernelResponseStatus.Ok,
                _epoch,
                _role,
                observation.Cursor,
                digest,
                jumpFact));
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            _started = false;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _started = false;
            return ValueTask.CompletedTask;
        }
    }
}
