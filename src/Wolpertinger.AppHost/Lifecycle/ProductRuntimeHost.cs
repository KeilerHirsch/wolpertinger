using Wolpertinger.AppHost.Windows;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Kernel;
using Wolpertinger.Edge.Runtime;
using Wolpertinger.Edge.Sample;
using Wolpertinger.Edge.Telemetry;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.AppHost.Lifecycle;

internal sealed class ProductRuntimeHost : IAsyncDisposable
{
    private readonly string _userRoot;
    private readonly string _kernelExecutable;
    private readonly string _sampleFixturePath;
    private readonly IChildProcessContainment _containment;
    private readonly CancellationToken _productToken;
    private ProductEdgeRuntime? _runtime;
    private EliteTelemetryWatcher? _telemetry;
    private Task? _telemetryTask;
    private CancellationTokenSource? _modeStop;

    internal ProductRuntimeHost(
        string userRoot,
        string kernelExecutable,
        string sampleFixturePath,
        IChildProcessContainment containment,
        CancellationToken productToken)
    {
        _userRoot = Path.GetFullPath(userRoot);
        _kernelExecutable = Path.GetFullPath(kernelExecutable);
        _sampleFixturePath = Path.GetFullPath(sampleFixturePath);
        _containment = containment ?? throw new ArgumentNullException(nameof(containment));
        _productToken = productToken;
    }

    internal ProductRuntimeMode? Mode { get; private set; }

    internal async Task StartAsync(
        ProductRuntimeMode mode = ProductRuntimeMode.Live)
        => await SwitchModeAsync(mode).ConfigureAwait(false);

    internal async Task SwitchModeAsync(ProductRuntimeMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (Mode == mode && _runtime is not null)
            return;

        await StopCurrentAsync().ConfigureAwait(false);
        _productToken.ThrowIfCancellationRequested();

        var paths = ProductRuntimePaths.Create(_userRoot, mode);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_productToken);
        ProductEdgeRuntime? runtime = null;
        EliteTelemetryWatcher? telemetry = null;
        Task? telemetryTask = null;
        try
        {
            runtime = await ProductEdgeRuntime.OpenAsync(
                paths,
                _kernelExecutable,
                new DpapiCurrentUserKeyProtector(),
                PresentationProtocol.DefaultPipeName,
                stop.Token,
                processContainment: _containment).ConfigureAwait(false);

            if (mode == ProductRuntimeMode.Live)
            {
                var eliteDataPath = new WindowsEliteDataPathResolver().Resolve();
                if (eliteDataPath is not null)
                {
                    telemetry = runtime.CreateTelemetryWatcher(eliteDataPath);
                    telemetryTask = telemetry.RunAsync(stop.Token);
                }
            }
            else
            {
                if (!File.Exists(_sampleFixturePath))
                    throw new FileNotFoundException(
                        "Required R0 sample fixture is missing.",
                        _sampleFixturePath);
                await new SampleSourceAdapter(runtime)
                    .RunAsync(_sampleFixturePath, stop.Token)
                    .ConfigureAwait(false);
            }

            _modeStop = stop;
            _runtime = runtime;
            _telemetry = telemetry;
            _telemetryTask = telemetryTask;
            Mode = mode;
        }
        catch
        {
            stop.Cancel();
            if (telemetryTask is not null)
                await ObserveAsync(telemetryTask).ConfigureAwait(false);
            if (telemetry is not null)
                await telemetry.DisposeAsync().ConfigureAwait(false);
            if (runtime is not null)
                await runtime.DisposeAsync().ConfigureAwait(false);
            stop.Dispose();
            throw;
        }
    }

    private async Task StopCurrentAsync()
    {
        var stop = _modeStop;
        var telemetryTask = _telemetryTask;
        var telemetry = _telemetry;
        var runtime = _runtime;

        _modeStop = null;
        _telemetryTask = null;
        _telemetry = null;
        _runtime = null;
        Mode = null;

        stop?.Cancel();
        if (telemetryTask is not null)
            await ObserveAsync(telemetryTask).ConfigureAwait(false);
        if (telemetry is not null)
            await telemetry.DisposeAsync().ConfigureAwait(false);
        if (runtime is not null)
            await runtime.DisposeAsync().ConfigureAwait(false);
        stop?.Dispose();
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    public async ValueTask DisposeAsync()
        => await StopCurrentAsync().ConfigureAwait(false);
}
