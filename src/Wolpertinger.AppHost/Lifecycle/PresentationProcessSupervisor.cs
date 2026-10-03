namespace Wolpertinger.AppHost.Lifecycle;

public enum PresentationSupervisorResult : byte
{
    Stopped = 0,
    Degraded = 1,
}

public sealed class PresentationProcessSupervisor
{
    private static readonly TimeSpan StabilityWindow = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan[] RestartDelays =
    [
        TimeSpan.FromMilliseconds(250),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
    ];

    private readonly IPresentationProcessLauncher _launcher;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public PresentationProcessSupervisor(
        IPresentationProcessLauncher launcher,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        _delay = delay ?? ((value, ct) => Task.Delay(value, ct));
    }
    public async Task<PresentationSupervisorResult> RunAsync(
        CancellationToken cancellationToken)
    {
        var consecutiveCrashes = 0;
        while (!cancellationToken.IsCancellationRequested)
        {
            await using var handle = _launcher.Launch();
            TimeSpan lifetime;
            try
            {
                lifetime = await handle.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return PresentationSupervisorResult.Stopped;
            }

            if (lifetime >= StabilityWindow)
                consecutiveCrashes = 0;

            consecutiveCrashes++;
            if (consecutiveCrashes >= 3)
                return PresentationSupervisorResult.Degraded;

            await _delay(
                RestartDelays[consecutiveCrashes - 1],
                cancellationToken).ConfigureAwait(false);
        }

        return PresentationSupervisorResult.Stopped;
    }
}
