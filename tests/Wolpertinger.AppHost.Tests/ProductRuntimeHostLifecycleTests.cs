using Wolpertinger.AppHost.Lifecycle;

namespace Wolpertinger.AppHost.Tests;

public sealed class ProductRuntimeHostLifecycleTests
{
    [Fact]
    public async Task TelemetryFaultIsPublishedAndStillPropagates()
    {
        string? reason = null;
        var fault = Task.FromException(new IOException("journal read failed"));

        var thrown = await Assert.ThrowsAsync<IOException>(() =>
            ProductRuntimeHost.MonitorTelemetryAsync(
                fault,
                (value, _) =>
                {
                    reason = value;
                    return ValueTask.CompletedTask;
                },
                CancellationToken.None));

        Assert.Equal("journal read failed", thrown.Message);
        Assert.Equal("TelemetryFault:IOException", reason);
    }

    [Fact]
    public async Task CleanupContinuesAfterTelemetryTaskFault()
    {
        using var stop = new CancellationTokenSource();
        var token = stop.Token;
        var telemetry = new AsyncProbe();
        var runtime = new AsyncProbe();
        var http = new DisposableProbe();
        var taskFailure = new InvalidOperationException("telemetry fault");

        var failure = await ProductRuntimeHost.CleanupResourcesAsync(
            stop,
            Task.FromException(taskFailure),
            telemetry,
            runtime,
            http);

        Assert.Same(taskFailure, failure);
        Assert.True(token.IsCancellationRequested);
        Assert.True(telemetry.Disposed);
        Assert.True(runtime.Disposed);
        Assert.True(http.Disposed);
    }

    private sealed class AsyncProbe : IAsyncDisposable
    {
        public bool Disposed { get; private set; }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class DisposableProbe : IDisposable
    {
        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
