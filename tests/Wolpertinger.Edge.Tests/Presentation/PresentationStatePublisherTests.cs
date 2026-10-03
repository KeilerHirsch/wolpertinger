using System.Text;
using Wolpertinger.Edge.Context;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Facts;
using Wolpertinger.Edge.Presentation;
using Wolpertinger.Edge.Runtime;
using Wolpertinger.Edge.Tests.TestSupport;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Edge.Tests.Presentation;

public sealed class PresentationStatePublisherTests
{
    private static readonly ContextDecision Decision = new(true, "JumpCompleted", OutputChannel.Display);

    [Fact]
    public async Task StartsEmptyAndReplacesCompleteSnapshotOncePerJump()
    {
        IPresentationPublisher publisher = new PresentationStatePublisher();
        Assert.Equal(PresentationSnapshot.Empty, publisher.Current);
        var first = TestFacts.Jump();
        await publisher.PublishJumpAsync(first, Decision);
        var retained = publisher.Current;
        Assert.Equal(JumpPresentationProjector.Project(first, Decision, 1), retained);
        var second = first with { StarSystem = "Second", FuelLevel = new Decimal64(1, 0) };
        await publisher.PublishJumpAsync(second, Decision);
        Assert.Equal(JumpPresentationProjector.Project(second, Decision, 2), publisher.Current);
        Assert.Equal(first.StarSystem, retained.Jump!.StarSystem);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var updates = publisher.ReadUpdatesAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync());
        Assert.Same(publisher.Current, updates.Current);
    }

    [Fact]
    public async Task ContextHealthAndAccountUpdatesAreAtomicAndMonotonic()
    {
        var publisher = new PresentationStatePublisher();
        var intent = new PresentationIntent(
            PresentationComposition.Flight, false, "ContextFlight", PresentationSelectionMode.Auto);

        await publisher.PublishContextAsync(PresentationGameContext.Supercruise, intent);
        var context = publisher.Current;
        Assert.Equal(1UL, context.Revision);
        Assert.Equal(PresentationGameContext.Supercruise, context.Context);
        Assert.Equal(intent, context.Intent);
        Assert.Equal(ProductRuntimeHealth.Starting, context.RuntimeHealth.Health);

        await publisher.PublishRuntimeHealthAsync(new(ProductRuntimeHealth.Ready, "Ready"));
        Assert.Equal(2UL, publisher.Current.Revision);
        Assert.Equal(intent, publisher.Current.Intent);

        await publisher.PublishFrontierAccountAsync(new(
            FrontierAccountState.Connected, PresentationFreshness.Current, 123, "Connected"));
        Assert.Equal(3UL, publisher.Current.Revision);
        Assert.Equal(PresentationGameContext.Supercruise, publisher.Current.Context);
        Assert.Equal(ProductRuntimeHealth.Ready, publisher.Current.RuntimeHealth.Health);
        Assert.Equal(FrontierAccountState.Connected, publisher.Current.FrontierAccount.State);
        PresentationSnapshotValidator.Validate(publisher.Current);
    }

    [Fact]
    public async Task BurstNeverWaitsForReaderAndOnlyLatestUpdateRemainsPending()
    {
        var publisher = new PresentationStatePublisher();
        for (var i = 1; i <= 1000; i++)
        {
            var publish = publisher.PublishJumpAsync(TestFacts.Jump() with { StarSystem = $"System {i}" }, Decision);
            Assert.True(publish.IsCompletedSuccessfully);
            await publish;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var updates = publisher.ReadUpdatesAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync());
        Assert.Equal(1000UL, updates.Current.Revision);
        Assert.Equal("System 1000", updates.Current.Jump!.StarSystem);
        var pending = updates.MoveNextAsync().AsTask();
        Assert.False(pending.IsCompleted);
        await publisher.PublishJumpAsync(TestFacts.Jump(), Decision);
        Assert.True(await pending.WaitAsync(timeout.Token));
        Assert.Equal(1001UL, updates.Current.Revision);
    }

    [Fact]
    public async Task ConcurrentWritersKeepCurrentAndPendingRevisionConsistent()
    {
        var publisher = new PresentationStatePublisher();
        await Task.WhenAll(Enumerable.Range(0, 100).Select(i => Task.Run(async () =>
            await publisher.PublishJumpAsync(TestFacts.Jump() with { StarSystem = $"System {i}" }, Decision))));
        Assert.Equal(100UL, publisher.Current.Revision);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var updates = publisher.ReadUpdatesAsync(timeout.Token).GetAsyncEnumerator();
        Assert.True(await updates.MoveNextAsync());
        Assert.Same(publisher.Current, updates.Current);
    }

    [Fact]
    public async Task FailedProjectionDoesNotAdvanceOrReplaceSnapshot()
    {
        var publisher = new PresentationStatePublisher();
        await publisher.PublishJumpAsync(TestFacts.Jump(), Decision);
        var before = publisher.Current;
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await publisher.PublishJumpAsync(TestFacts.Jump() with { FuelFreshness = (FreshnessState)255 }, Decision));
        Assert.Same(before, publisher.Current);
        await publisher.PublishJumpAsync(TestFacts.Jump(), Decision);
        Assert.Equal(2UL, publisher.Current.Revision);
    }

    [Fact]
    public async Task CancellationStopsWaitingReader()
    {
        var publisher = new PresentationStatePublisher();
        using var cancellation = new CancellationTokenSource();
        await using var updates = publisher.ReadUpdatesAsync(cancellation.Token).GetAsyncEnumerator();
        var pending = updates.MoveNextAsync().AsTask();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task NullPublisherDoesNothingAndHasNoUpdates()
    {
        IPresentationPublisher publisher = NullPresentationPublisher.Instance;
        await publisher.PublishJumpAsync(TestFacts.Jump(), Decision);
        Assert.Same(PresentationSnapshot.Empty, publisher.Current);
        await using var updates = publisher.ReadUpdatesAsync().GetAsyncEnumerator();
        Assert.False(await updates.MoveNextAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunnerPublishesTrustedJumpAndContainsPresentationFailure(bool fail)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "WOLPERTINGER.slnx")))
            directory = directory.Parent;
        var root = directory?.FullName ?? throw new DirectoryNotFoundException("Repository root not found.");
        var executable = Path.Combine(root, "kernel", "bin",
            OperatingSystem.IsWindows() ? "wolpertinger_kernel_main.exe" : "wolpertinger_kernel_main");
        Assert.True(File.Exists(executable), $"Build the Ada kernel first: {executable}");
        var data = Path.Combine(root, "tests", "Wolpertinger.Edge.Tests", "TestResults", $"presentation-{Guid.NewGuid():N}");
        var publisher = new RecordingPublisher(fail);
        await using var runner = await VerticalSliceRunner.OpenAsync(data, executable, presentationPublisher: publisher);
        var lines = File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "journal", "live-v4-fsdjump-session.jsonl"));
        await runner.ProcessJournalLineAsync(Encoding.UTF8.GetBytes(lines[4]));
        Assert.Empty(publisher.Calls);
        Assert.Empty(runner.Outputs);
        foreach (var line in lines.Take(4))
            await runner.ProcessJournalLineAsync(Encoding.UTF8.GetBytes(line));
        Assert.Empty(publisher.Calls);
        await runner.ProcessJournalLineAsync(Encoding.UTF8.GetBytes(lines[4]));
        var call = Assert.Single(publisher.Calls);
        Assert.True(call.Decision.Surface);
        Assert.Equal("JumpCompleted", call.Decision.ReasonCode);
        Assert.Equal(runner.FinalStateDigest, call.Fact.StateDigest);
        Assert.Single(runner.Outputs);
        var failures = runner.Diagnostics.Where(d => d.Code == "PresentationPublishFailed").ToArray();
        if (fail)
        {
            var diagnostic = Assert.Single(failures);
            Assert.Equal(call.Fact.EvidenceReference.RawOrdinal, diagnostic.RawOrdinal);
            Assert.Equal("Presentation unavailable", diagnostic.Message);
        }
        else
            Assert.Empty(failures);
    }

    private sealed class RecordingPublisher(bool fail) : IPresentationPublisher
    {
        public List<(JumpFact Fact, ContextDecision Decision)> Calls { get; } = [];
        public PresentationSnapshot Current => PresentationSnapshot.Empty;
        public ValueTask PublishJumpAsync(JumpFact fact, ContextDecision decision, CancellationToken cancellationToken = default)
        {
            Calls.Add((fact, decision));
            return fail ? ValueTask.FromException(new InvalidOperationException("Presentation unavailable")) : ValueTask.CompletedTask;
        }
        public ValueTask PublishCommanderVesselAsync(
            CommanderVesselFact fact,
            CancellationToken cancellationToken = default)
            => fail
                ? ValueTask.FromException(new InvalidOperationException("Presentation unavailable"))
                : ValueTask.CompletedTask;
        public IAsyncEnumerable<PresentationSnapshot> ReadUpdatesAsync(CancellationToken cancellationToken = default)
            => NullPresentationPublisher.Instance.ReadUpdatesAsync(cancellationToken);
    }
}
