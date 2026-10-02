using System.Text;
using Wolpertinger.Edge.Context;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Persistence;
using Wolpertinger.Edge.Runtime;
using Wolpertinger.Edge.Sample;
using Wolpertinger.Presentation.Contracts;
using static Wolpertinger.Integration.Tests.FsdJumpVerticalSliceTests;

namespace Wolpertinger.Integration.Tests;

public sealed class SampleModePipelineTests
{
    [Fact]
    public async Task SampleFixtureUsesRealEvidenceKernelAndPresentationPipeline()
    {
        var root = RepoRoot();
        var data = TempData();
        var paths = ProductRuntimePaths.ForSample(data);
        var protector = new TestProtector();
        var transitions = new List<GameContext>();
        var pipeName = $"wolpertinger.sample.{Guid.NewGuid():N}";

        Assert.Equal(ProductRuntimeMode.Sample, paths.Mode);
        Assert.Null(paths.FrontierTokenPath);

        await using (var runtime = await ProductEdgeRuntime.OpenAsync(
                         paths,
                         Kernel(root),
                         protector,
                         pipeName))
        {
            runtime.GameContextChanged += transition => transitions.Add(transition.Context);
            var adapter = new SampleSourceAdapter(runtime);
            await adapter.RunAsync(Path.Combine(root, "fixtures", "r0", "sample-flow.jsonl"));

            AssertSubsequence(
                transitions,
                GameContext.Flight,
                GameContext.JumpPreparation,
                GameContext.FsdJump,
                GameContext.PostJump);

            Assert.NotNull(runtime.FinalStateDigest);
            Assert.Equal(3UL, runtime.KernelDiagnostics.LastAgreedCursor?.EvidenceSequence);
            Assert.Single(runtime.Outputs);

            var snapshot = runtime.CurrentPresentation;
            var commander = Assert.IsType<CommanderVesselPresentation>(snapshot.CommanderVessel);
            Assert.Equal("CMDR SAMPLE", commander.CommanderName);
            Assert.Equal("Sidewinder", commander.ShipModel);
            Assert.Equal(PresentationProvenance.Sample, commander.Provenance);
            Assert.Equal(PresentationFreshness.Current, commander.Freshness);

            var jump = Assert.IsType<JumpPresentation>(snapshot.Jump);
            Assert.Equal("Sample Destination", jump.StarSystem);
            Assert.Equal(PresentationProvenance.Sample, jump.LocationProvenance);
            Assert.Equal(PresentationProvenance.Sample, jump.FuelProvenance);
        }

        var keyStore = new EvidenceKeyStore(paths.EvidenceKeyPath, protector);
        var recovered = await EvidenceLogRecovery.RecoverAsync(
            paths.EvidenceDirectory,
            keyStore);
        Assert.Equal(8, recovered.Count);
        Assert.All(recovered, record =>
            Assert.Equal(RawEvidenceSourceKind.Sample, record.SourceKind));

        await using var ledger = await NormalizedObservationLedger.OpenAsync(
            paths.NormalizedLedgerPath);
        var observations = new List<ObservationEnvelope>();
        await foreach (var observation in ledger.ReadObservationsAsync(null))
            observations.Add(observation);
        Assert.Equal(
            [ObservationKind.SessionBound, ObservationKind.CommanderVessel, ObservationKind.FsdJump],
            observations.Select(observation => observation.Kind));
        Assert.All(observations, observation =>
        {
            Assert.Equal(SourceProvenance.Sample, observation.Provenance);
            Assert.Equal(KernelProtocol.R0Version, observation.ProtocolVersion);
        });

        Assert.False(File.Exists(Path.Combine(data, "secrets", "frontier-token.bin")));
    }

    private static void AssertSubsequence(
        IReadOnlyList<GameContext> observed,
        params GameContext[] expected)
    {
        var index = 0;
        foreach (var context in observed)
        {
            if (index < expected.Length && context == expected[index])
                index++;
        }
        Assert.Equal(expected.Length, index);
    }

    private sealed class TestProtector : IEvidenceKeyProtector
    {
        public byte[] Protect(ReadOnlySpan<byte> plaintext)
            => Encoding.UTF8.GetBytes(Convert.ToBase64String(plaintext));

        public byte[] Unprotect(ReadOnlySpan<byte> protectedBytes)
            => Convert.FromBase64String(Encoding.UTF8.GetString(protectedBytes));
    }
}
