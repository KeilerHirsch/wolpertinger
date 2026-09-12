using System.Diagnostics;
using Wolpertinger.AppHost.Activation;
using Wolpertinger.AppHost.Lifecycle;
using Wolpertinger.AppHost.Windows;
using Wolpertinger.Edge.Evidence;
using Wolpertinger.Edge.Runtime;
using Wolpertinger.Edge.Telemetry;
using Wolpertinger.Presentation.Contracts;
using Wolpertinger.Product.Contracts;

namespace Wolpertinger.AppHost;

public static class Program
{
    private const string ProductId = "KeilerHirsch.WOLPERTINGER.R0";

    public static async Task<int> Main(string[] args)
    {
        using var instance = ProductInstanceGuard.Acquire(ProductId);
        if (!instance.IsPrimary)
        {
            await instance.ForwardAsync(new ProductActivation(ProductActivationKind.Open))
                .ConfigureAwait(false);
            return 0;
        }

        using var productStop = new CancellationTokenSource();
        EventHandler processExit = (_, _) => productStop.Cancel();
        AppDomain.CurrentDomain.ProcessExit += processExit;

        ProcessJob? job = null;
        ProductEdgeRuntime? runtime = null;
        EliteTelemetryWatcher? telemetry = null;
        Task? telemetryTask = null;
        Task<PresentationSupervisorResult>? presentationTask = null;
        try
        {
            job = new ProcessJob();
            var packageRoot = AppContext.BaseDirectory;
            var kernelExecutable = RequirePackagedFile(packageRoot, "wolpertinger_kernel.exe");
            var presentationExecutable = RequirePackagedFile(packageRoot, "Wolpertinger.Presentation.App.exe");

            var userRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WOLPERTINGER");
            Directory.CreateDirectory(userRoot);
            var paths = ProductRuntimePaths.ForLive(userRoot);

            runtime = await ProductEdgeRuntime.OpenAsync(
                paths,
                kernelExecutable,
                new DpapiCurrentUserKeyProtector(),
                PresentationProtocol.DefaultPipeName,
                productStop.Token,
                processContainment: job).ConfigureAwait(false);

            var eliteDataPath = new WindowsEliteDataPathResolver().Resolve();
            if (eliteDataPath is not null)
            {
                telemetry = runtime.CreateTelemetryWatcher(eliteDataPath);
                telemetryTask = telemetry.RunAsync(productStop.Token);
            }

            var launcher = new PresentationProcessLauncher(
                presentationExecutable,
                PresentationProtocol.DefaultPipeName,
                job);
            var presentationSupervisor = new PresentationProcessSupervisor(launcher);
            presentationTask = presentationSupervisor.RunAsync(productStop.Token);

            var activation = new ActivationChannel(instance.ActivationPipeName);
            await RunActivationLoopAsync(activation, productStop).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException) when (productStop.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception error)
        {
            Trace.TraceError("WOLPERTINGER AppHost failed closed: {0}", error);
            return 1;
        }
        finally
        {
            productStop.Cancel();
            AppDomain.CurrentDomain.ProcessExit -= processExit;

            if (telemetryTask is not null)
                await ObserveShutdownAsync(telemetryTask, "telemetry").ConfigureAwait(false);
            if (telemetry is not null)
                await telemetry.DisposeAsync().ConfigureAwait(false);

            if (presentationTask is not null)
                await ObserveShutdownAsync(presentationTask, "presentation").ConfigureAwait(false);

            if (runtime is not null)
                await runtime.DisposeAsync().ConfigureAwait(false);

            job?.Dispose();
        }
    }

    private static async Task RunActivationLoopAsync(
        ActivationChannel channel,
        CancellationTokenSource productStop)
    {
        while (!productStop.IsCancellationRequested)
        {
            ProductActivation activation;
            try
            {
                activation = await channel.ReceiveOneAsync(productStop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (productStop.IsCancellationRequested)
            {
                return;
            }
            catch (InvalidDataException error)
            {
                Trace.TraceWarning("Rejected malformed activation: {0}", error.Message);
                continue;
            }
            catch (IOException error)
            {
                Trace.TraceWarning("Rejected activation I/O failure: {0}", error.Message);
                continue;
            }

            if (activation.Kind == ProductActivationKind.Exit)
            {
                productStop.Cancel();
                return;
            }

            Trace.TraceInformation("Received product activation {0}.", activation.Kind);
        }
    }

    private static string RequirePackagedFile(string root, string fileName)
    {
        var path = Path.GetFullPath(Path.Combine(root, fileName));
        if (!File.Exists(path))
            throw new FileNotFoundException($"Required packaged component is missing: {fileName}.", path);
        return path;
    }

    private static async Task ObserveShutdownAsync(Task task, string component)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception error)
        {
            Trace.TraceWarning("{0} shutdown observed an error: {1}", component, error.Message);
        }
    }
}
