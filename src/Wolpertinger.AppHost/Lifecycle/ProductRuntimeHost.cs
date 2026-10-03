using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Wolpertinger.AppHost.Activation;
using Wolpertinger.AppHost.Preferences;
using Wolpertinger.AppHost.Windows;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Frontier;
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
    private readonly ProductPreferencesStore _preferencesStore;
    private ProductEdgeRuntime? _runtime;
    private EliteTelemetryWatcher? _telemetry;
    private Task? _telemetryTask;
    private CancellationTokenSource? _modeStop;
    private HttpClient? _frontierHttp;
    private FrontierAccountService? _frontierAccount;

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
        _preferencesStore = new ProductPreferencesStore(
            Path.Combine(_userRoot, "product.json"));
    }

    internal ProductRuntimeMode? Mode { get; private set; }

    internal Task StartAsync(ProductRuntimeMode mode = ProductRuntimeMode.Live)
        => SwitchModeAsync(mode);
    internal Task SwitchModeAsync(ProductRuntimeMode mode)
        => StartModeAsync(mode, forceRestart: false);

    internal async Task<Uri> ConnectFrontierAsync(
        CancellationToken cancellationToken = default)
        => await RequireFrontierAccount()
            .ConnectAsync(cancellationToken)
            .ConfigureAwait(false);

    internal async Task HandleProtocolCallbackAsync(
        string callbackPayload,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(callbackPayload);
        var callback = new Uri(callbackPayload, UriKind.Absolute);
        var account = RequireFrontierAccount();
        await account.HandleCallbackAsync(callback, cancellationToken)
            .ConfigureAwait(false);

        var snapshot = await account.RefreshProfileAsync(
            automatic: false,
            cancellationToken).ConfigureAwait(false);
        if (snapshot is not null)
        {
            await RequireRuntime().ProcessFrontierProfileSnapshotAsync(
                snapshot,
                cancellationToken).ConfigureAwait(false);
        }
    }

    internal Task DisconnectFrontierAsync(
        CancellationToken cancellationToken = default)
        => RequireFrontierAccount().DisconnectAsync(cancellationToken);

    internal void SetManualComposition(string value)
    {
        if (!Enum.TryParse<PresentationComposition>(
                value,
                ignoreCase: false,
                out var composition)
            || !Enum.IsDefined(composition))
        {
            throw new InvalidDataException("Manual composition is invalid.");
        }
        RequireRuntime().SetManualComposition(composition);
    }

    internal void SetSmartAuto()
        => RequireRuntime().SetSmartAuto();

    internal async Task SetEliteDataPathAsync(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath))
            throw new InvalidDataException("Elite data directory does not exist.");

        await _preferencesStore.SaveAsync(new ProductPreferences(
            ProductPreferences.CurrentSchemaVersion,
            fullPath)).ConfigureAwait(false);

        if (Mode == ProductRuntimeMode.Live)
            await StartModeAsync(ProductRuntimeMode.Live, forceRestart: true)
                .ConfigureAwait(false);
    }
    private async Task StartModeAsync(
        ProductRuntimeMode mode,
        bool forceRestart)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
        if (!forceRestart && Mode == mode && _runtime is not null)
            return;

        await StopCurrentAsync().ConfigureAwait(false);
        _productToken.ThrowIfCancellationRequested();

        var paths = ProductRuntimePaths.Create(_userRoot, mode);
        var stop = CancellationTokenSource.CreateLinkedTokenSource(_productToken);
        ProductEdgeRuntime? runtime = null;
        EliteTelemetryWatcher? telemetry = null;
        Task? telemetryTask = null;
        HttpClient? frontierHttp = null;
        FrontierAccountService? frontierAccount = null;
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
                var preferences = await _preferencesStore.LoadAsync()
                    .ConfigureAwait(false);
                var eliteDataPath = preferences.EliteDataDirectory
                    ?? new WindowsEliteDataPathResolver().Resolve();
                if (eliteDataPath is not null)
                {
                    telemetry = runtime.CreateTelemetryWatcher(eliteDataPath);
                    telemetryTask = MonitorTelemetryAsync(
                        telemetry.RunAsync(stop.Token),
                        (reason, token) => runtime.ReportRuntimeFaultAsync(reason, token),
                        stop.Token);
                }

                var optionsResult = FrontierOAuthOptionsFile.Read(
                    Path.Combine(_userRoot, FrontierOAuthOptionsFile.FileName));
                if (optionsResult is
                    { Status: FrontierOAuthOptionsFileStatus.Valid, Options: not null })
                {
                    frontierHttp = FrontierOAuthHttpClientFactory.CreateClient();
                    var oauth = new FrontierOAuthClient(
                        optionsResult.Options,
                        frontierHttp,
                        new SystemExternalBrowser());
                    var tokenStore = new FrontierTokenStore(
                        paths.FrontierTokenPath
                            ?? throw new InvalidOperationException(
                                "Live runtime has no Frontier token path."),
                        new DpapiCurrentUserKeyProtector());
                    var capi = runtime.CreateFrontierCapiClient(
                        frontierHttp,
                        optionsResult.Options.CapiProfileEndpoint);
                    frontierAccount = new FrontierAccountService(
                        oauth,
                        tokenStore,
                        capi);
                    runtime.AttachFrontierAccountService(frontierAccount);
                }
                else
                {
                    Trace.TraceInformation(
                        "Frontier integration unavailable: OAuth configuration is {0}.",
                        optionsResult.Status);
                }
            }
            else
            {
                if (!File.Exists(_sampleFixturePath))
                {
                    throw new FileNotFoundException(
                        "Required R0 sample fixture is missing.",
                        _sampleFixturePath);
                }
                await new SampleSourceAdapter(runtime)
                    .RunAsync(_sampleFixturePath, stop.Token)
                    .ConfigureAwait(false);
            }

            _modeStop = stop;
            _runtime = runtime;
            _telemetry = telemetry;
            _telemetryTask = telemetryTask;
            _frontierHttp = frontierHttp;
            _frontierAccount = frontierAccount;
            Mode = mode;
        }
        catch
        {
            _ = await CleanupResourcesAsync(
                stop,
                telemetryTask,
                telemetry,
                runtime,
                frontierHttp).ConfigureAwait(false);
            throw;
        }
    }
    private ProductEdgeRuntime RequireRuntime()
        => _runtime
            ?? throw new InvalidOperationException(
                "Product runtime is not available.");

    private FrontierAccountService RequireFrontierAccount()
    {
        if (Mode != ProductRuntimeMode.Live)
            throw new InvalidOperationException(
                "Frontier account integration is unavailable in Sample mode.");
        return _frontierAccount
            ?? throw new InvalidOperationException(
                "Frontier OAuth public configuration is unavailable or invalid.");
    }

    private async Task StopCurrentAsync()
    {
        var stop = _modeStop;
        var telemetryTask = _telemetryTask;
        var telemetry = _telemetry;
        var runtime = _runtime;
        var frontierHttp = _frontierHttp;

        _modeStop = null;
        _telemetryTask = null;
        _telemetry = null;
        _runtime = null;
        _frontierHttp = null;
        _frontierAccount = null;
        Mode = null;

        var failure = await CleanupResourcesAsync(
            stop,
            telemetryTask,
            telemetry,
            runtime,
            frontierHttp).ConfigureAwait(false);
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static async Task MonitorTelemetryAsync(
        Task telemetryTask,
        Func<string, CancellationToken, ValueTask> reportFault,
        CancellationToken stopToken)
    {
        ArgumentNullException.ThrowIfNull(telemetryTask);
        ArgumentNullException.ThrowIfNull(reportFault);
        try
        {
            await telemetryTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (stopToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            try
            {
                await reportFault(
                    $"TelemetryFault:{ex.GetType().Name}",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception reportFailure)
            {
                Trace.TraceError(
                    "Failed to publish telemetry runtime fault: {0}",
                    reportFailure.Message);
            }
            throw;
        }
    }

    internal static async Task<Exception?> CleanupResourcesAsync(
        CancellationTokenSource? stop,
        Task? telemetryTask,
        IAsyncDisposable? telemetry,
        IAsyncDisposable? runtime,
        IDisposable? frontierHttp)
    {
        stop?.Cancel();
        Exception? firstFailure = null;

        if (telemetryTask is not null)
        {
            try
            {
                await telemetryTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stop?.IsCancellationRequested is true)
            {
            }
            catch (Exception ex)
            {
                firstFailure ??= ex;
            }
        }

        if (telemetry is not null)
        {
            try { await telemetry.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { firstFailure ??= ex; }
        }

        if (runtime is not null)
        {
            try { await runtime.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { firstFailure ??= ex; }
        }

        try { frontierHttp?.Dispose(); }
        catch (Exception ex) { firstFailure ??= ex; }

        try { stop?.Dispose(); }
        catch (Exception ex) { firstFailure ??= ex; }

        return firstFailure;
    }

    public async ValueTask DisposeAsync()
        => await StopCurrentAsync().ConfigureAwait(false);
}
