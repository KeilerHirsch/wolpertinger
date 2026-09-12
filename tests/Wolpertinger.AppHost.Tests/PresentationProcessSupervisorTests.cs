using Wolpertinger.AppHost.Lifecycle;

namespace Wolpertinger.AppHost.Tests;

public sealed class PresentationProcessSupervisorTests
{
    [Fact]
    public async Task ConsecutiveCrashesUseFrozenBackoffThenDegrade()
    {
        var launcher = new FakeLauncher([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        var delays = new List<TimeSpan>();
        var supervisor = new PresentationProcessSupervisor(
            launcher,
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        var result = await supervisor.RunAsync(CancellationToken.None);

        Assert.Equal(PresentationSupervisorResult.Degraded, result);
        Assert.Equal(3, launcher.LaunchCount);
        Assert.Equal(new[] { TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(1) }, delays);
    }

    [Fact]
    public async Task StableThirtySecondsResetsCrashCounter()
    {
        var launcher = new FakeLauncher([
            TimeSpan.Zero,
            TimeSpan.FromSeconds(31),
            TimeSpan.Zero,
            TimeSpan.Zero,
        ]);
        var delays = new List<TimeSpan>();
        var supervisor = new PresentationProcessSupervisor(
            launcher,
            (delay, _) => { delays.Add(delay); return Task.CompletedTask; });

        var result = await supervisor.RunAsync(CancellationToken.None);

        Assert.Equal(PresentationSupervisorResult.Degraded, result);
        Assert.Equal(4, launcher.LaunchCount);
        Assert.Equal(new[]
        {
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromSeconds(1),
        }, delays);
    }

    [Fact]
    public async Task ProductCancellationNeverRestartsPresentation()
    {
        using var cancellation = new CancellationTokenSource();
        var launcher = new FakeLauncher([TimeSpan.MaxValue], cancellation);
        var supervisor = new PresentationProcessSupervisor(
            launcher,
            static (_, ct) => Task.Delay(Timeout.InfiniteTimeSpan, ct));

        var result = await supervisor.RunAsync(cancellation.Token);

        Assert.Equal(PresentationSupervisorResult.Stopped, result);
        Assert.Equal(1, launcher.LaunchCount);
    }

    private sealed class FakeLauncher(
        IReadOnlyList<TimeSpan> lifetimes,
        CancellationTokenSource? cancellation = null) : IPresentationProcessLauncher
    {
        private int _index;
        public int LaunchCount { get; private set; }

        public IPresentationProcessHandle Launch()
        {
            LaunchCount++;
            var lifetime = lifetimes[Math.Min(_index++, lifetimes.Count - 1)];
            return new FakeHandle(lifetime, cancellation);
        }
    }
    private sealed class FakeHandle(
        TimeSpan lifetime,
        CancellationTokenSource? cancellation) : IPresentationProcessHandle
    {
        public Task<TimeSpan> WaitForExitAsync(CancellationToken cancellationToken)
        {
            if (lifetime == TimeSpan.MaxValue)
            {
                cancellation?.Cancel();
                return Task.FromCanceled<TimeSpan>(cancellationToken);
            }
            return Task.FromResult(lifetime);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
