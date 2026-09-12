using System.Buffers.Binary;
using System.Text;
using Wolpertinger.Edge.Context;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Kernel;
using Wolpertinger.Edge.Persistence;
using Wolpertinger.Edge.Runtime;
using Wolpertinger.Edge.Telemetry;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Edge.Tests.Runtime;

public sealed class ProductEdgeRuntimeTests
{
    [Fact]
    public async Task JournalRecordIsEncryptedBeforeDispatchAndLocatorSurvivesRecovery()
    {
        using var temp = new TempDirectory();
        var paths = ProductRuntimePaths.ForLive(temp.Path);
        var protector = new TestProtector();
        var pipeName = $"wolpertinger.test.{Guid.NewGuid():N}";
        var locator = new RawEvidenceSourceLocator(
            FixedBytes16.FromHex("00112233445566778899AABBCCDDEEFF"),
            42,
            66);
        await using (var runtime = await ProductEdgeRuntime.OpenForTestsAsync(
                         paths,
                         protector,
                         static (_, epochs) => new KernelSupervisor(
                             new NoApplyKernelClient(),
                             new NoApplyKernelClient(),
                             epochs),
                         pipeName))
        {
            var payload = "{\"timestamp\":\"2026-09-11T12:00:00Z\",\"event\":\"Fileheader\"}"u8.ToArray();
            locator = locator with { SourceLength = checked((uint)payload.Length + 1) };
            await runtime.ProcessJournalRecordAsync(new JournalSourceRecord(payload, locator));
            Assert.Empty(runtime.Outputs);
        }

        var segment = Directory.GetFiles(paths.EvidenceDirectory, "evidence-*.wlev").Single();
        var bytes = await File.ReadAllBytesAsync(segment);
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(4, 2)));
        Assert.True(bytes.AsSpan().IndexOf("Fileheader"u8) < 0);

        var keyStore = new EvidenceKeyStore(paths.EvidenceKeyPath, protector);
        var recovered = await EvidenceLogRecovery.RecoverAsync(paths.EvidenceDirectory, keyStore);
        var record = Assert.Single(recovered);
        Assert.Equal(RawEvidenceSourceKind.LocalJournal, record.SourceKind);
        Assert.Equal(locator, record.SourceLocator);
    }

    [Fact]
    public async Task StatusIsDurableBeforeContextTransition()
    {
        using var temp = new TempDirectory();
        var paths = ProductRuntimePaths.ForLive(temp.Path);
        var protector = new TestProtector();
        var transitions = new List<GameContextTransition>();
        await using (var runtime = await ProductEdgeRuntime.OpenForTestsAsync(
                         paths, protector,
                         static (_, epochs) => new KernelSupervisor(
                             new NoApplyKernelClient(), new NoApplyKernelClient(), epochs),
                         $"wolpertinger.status.{Guid.NewGuid():N}"))
        {
            runtime.GameContextChanged += transitions.Add;
            await runtime.ProcessStatusSnapshotAsync(
                "{\"Flags\":16,\"GuiFocus\":0}"u8.ToArray());
            Assert.Equal(GameContext.Supercruise, runtime.CurrentGameContext);
            Assert.Equal(new GameContextTransition(
                GameContext.Supercruise, GameContextSignalOrigin.StatusSnapshot),
                Assert.Single(transitions));
        }

        var store = new EvidenceKeyStore(paths.EvidenceKeyPath, protector);
        var recovered = await EvidenceLogRecovery.RecoverAsync(paths.EvidenceDirectory, store);
        Assert.Equal(RawEvidenceSourceKind.LocalStatus, Assert.Single(recovered).SourceKind);
    }

    [Fact]
    public async Task RuntimeHealthAndSmartContextShareOnePresentationSnapshot()
    {
        using var temp = new TempDirectory();
        var paths = ProductRuntimePaths.ForLive(temp.Path);
        var runtime = await ProductEdgeRuntime.OpenForTestsAsync(
            paths, new TestProtector(),
            static (_, epochs) => new KernelSupervisor(
                new NoApplyKernelClient(), new NoApplyKernelClient(), epochs),
            $"wolpertinger.presentation-v2.{Guid.NewGuid():N}");

        Assert.Equal(ProductRuntimeHealth.Ready, runtime.CurrentPresentation.RuntimeHealth.Health);
        var payload = "{\"timestamp\":\"2026-09-12T10:00:00Z\",\"event\":\"Fileheader\"}"u8.ToArray();
        var locator = new RawEvidenceSourceLocator(
            FixedBytes16.FromHex("00112233445566778899AABBCCDDEEFF"), 0, checked((uint)payload.Length + 1));
        await runtime.ProcessJournalRecordAsync(new JournalSourceRecord(payload, locator));
        Assert.Equal(PresentationGameContext.MainMenu, runtime.CurrentPresentation.Context);
        Assert.Equal(PresentationComposition.Quiet, runtime.CurrentPresentation.Intent.Composition);

        var status = "{\"Flags\":16,\"GuiFocus\":0}"u8.ToArray();
        await runtime.ProcessStatusSnapshotAsync(status);
        Assert.Equal(PresentationGameContext.Supercruise, runtime.CurrentPresentation.Context);
        Assert.Equal(PresentationComposition.Quiet, runtime.CurrentPresentation.Intent.Composition);
        await runtime.ProcessStatusSnapshotAsync(status);
        Assert.Equal(PresentationComposition.Flight, runtime.CurrentPresentation.Intent.Composition);
        Assert.Equal(PresentationSelectionMode.Auto, runtime.CurrentPresentation.Intent.SelectionMode);

        await runtime.DisposeAsync();
        Assert.Equal(ProductRuntimeHealth.Stopped, runtime.CurrentPresentation.RuntimeHealth.Health);
        Assert.Equal(PresentationGameContext.Supercruise, runtime.CurrentPresentation.Context);
    }

    private sealed class TestProtector : IEvidenceKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
            => Encoding.UTF8.GetBytes(Convert.ToBase64String(plaintext));

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
            => Convert.FromBase64String(Encoding.UTF8.GetString(protectedBytes));
    }
    private sealed class NoApplyKernelClient : IKernelProcessClient
    {
        private bool _started;
        private ulong _epoch;
        private KernelRole _role;

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
            => throw new InvalidOperationException(
                $"Unexpected kernel apply for {_role} epoch {_epoch}.");

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

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "wolpertinger-product-runtime-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
