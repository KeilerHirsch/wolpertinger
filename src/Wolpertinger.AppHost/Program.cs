using System.Diagnostics;
using Wolpertinger.AppHost.Activation;
using Wolpertinger.AppHost.Lifecycle;
using Wolpertinger.AppHost.Windows;
using Wolpertinger.Edge.Runtime;
using Wolpertinger.Presentation.Contracts;
using Wolpertinger.Product.Contracts;

namespace Wolpertinger.AppHost;

public static class Program
{
    private const string ProductId = "KeilerHirsch.WOLPERTINGER.R0";

    public static async Task<int> Main(string[] args)
    {
        var initialActivation = ParseInitialActivation(args);
        using var instance = ProductInstanceGuard.Acquire(ProductId);
        if (!instance.IsPrimary)
        {
            await instance.ForwardAsync(initialActivation).ConfigureAwait(false);
            return 0;
        }

        using var productStop = new CancellationTokenSource();
        EventHandler processExit = (_, _) => productStop.Cancel();
        AppDomain.CurrentDomain.ProcessExit += processExit;

        ProcessJob? job = null;
        ProductRuntimeHost? runtimeHost = null;
        Task<PresentationSupervisorResult>? presentationTask = null;
        try
        {
            job = new ProcessJob();
            var packageRoot = AppContext.BaseDirectory;
            var kernelExecutable = RequirePackagedFile(packageRoot, "wolpertinger_kernel.exe");
            var presentationExecutable = RequirePackagedFile(packageRoot, "Wolpertinger.Presentation.App.exe");

            var sampleFixture = RequirePackagedFile(
                packageRoot,
                Path.Combine("fixtures", "r0", "sample-flow.jsonl"));
            var userRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WOLPERTINGER");
            Directory.CreateDirectory(userRoot);

            runtimeHost = new ProductRuntimeHost(
                userRoot,
                kernelExecutable,
                sampleFixture,
                job,
                productStop.Token);
            await runtimeHost.StartAsync(ProductRuntimeMode.Live).ConfigureAwait(false);

            var launcher = new PresentationProcessLauncher(
                presentationExecutable,
                PresentationProtocol.DefaultPipeName,
                job);
            var presentationSupervisor = new PresentationProcessSupervisor(launcher);
            presentationTask = presentationSupervisor.RunAsync(productStop.Token);

            var activation = new ActivationChannel(instance.ActivationPipeName);
            await RunActivationLoopAsync(
                activation,
                productStop,
                runtimeHost,
                initialActivation).ConfigureAwait(false);
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

            if (runtimeHost is not null)
                await runtimeHost.DisposeAsync().ConfigureAwait(false);

            if (presentationTask is not null)
                await ObserveShutdownAsync(presentationTask, "presentation").ConfigureAwait(false);

            job?.Dispose();
        }
    }

    private static async Task RunActivationLoopAsync(
        ActivationChannel channel,
        CancellationTokenSource productStop,
        ProductRuntimeHost runtimeHost,
        ProductActivation initialActivation)
    {
        if (!await ProcessActivationAsync(
                initialActivation,
                productStop,
                runtimeHost).ConfigureAwait(false))
        {
            return;
        }

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

            if (!await ProcessActivationAsync(
                    activation,
                    productStop,
                    runtimeHost).ConfigureAwait(false))
            {
                return;
            }
        }
    }

    private static async Task<bool> ProcessActivationAsync(
        ProductActivation activation,
        CancellationTokenSource productStop,
        ProductRuntimeHost runtimeHost)
    {
        switch (activation.Kind)
        {
            case ProductActivationKind.Exit:
                productStop.Cancel();
                return false;
            case ProductActivationKind.EnterSample:
                await runtimeHost.SwitchModeAsync(ProductRuntimeMode.Sample)
                    .ConfigureAwait(false);
                break;
            case ProductActivationKind.ReturnLive:
                await runtimeHost.SwitchModeAsync(ProductRuntimeMode.Live)
                    .ConfigureAwait(false);
                break;
            case ProductActivationKind.ConnectFrontier:
                await runtimeHost.ConnectFrontierAsync(productStop.Token)
                    .ConfigureAwait(false);
                break;
            case ProductActivationKind.DisconnectFrontier:
                await runtimeHost.DisconnectFrontierAsync(productStop.Token)
                    .ConfigureAwait(false);
                break;
            case ProductActivationKind.ProtocolCallback:
                await runtimeHost.HandleProtocolCallbackAsync(
                    activation.Payload!,
                    productStop.Token).ConfigureAwait(false);
                break;
            case ProductActivationKind.SetManualComposition:
                runtimeHost.SetManualComposition(activation.Payload!);
                break;
            case ProductActivationKind.SetSmartAuto:
                runtimeHost.SetSmartAuto();
                break;
            case ProductActivationKind.SetEliteDataPath:
                await runtimeHost.SetEliteDataPathAsync(activation.Payload!)
                    .ConfigureAwait(false);
                break;
            case ProductActivationKind.Open:
            case ProductActivationKind.Startup:
                break;
            default:
                throw new InvalidDataException(
                    $"Unsupported product activation: {activation.Kind}.");
        }

        Trace.TraceInformation("Processed product activation {0}.", activation.Kind);
        return true;
    }

    internal static ProductActivation ParseInitialActivation(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        ProductActivation activation = args switch
        {
            [] => new(ProductActivationKind.Open),
            ["--startup"] => new(ProductActivationKind.Startup),
            ["--protocol", var payload] => new(
                ProductActivationKind.ProtocolCallback,
                payload),
            _ => throw new InvalidDataException(
                "Unsupported WOLPERTINGER startup activation."),
        };
        ProductActivationValidator.Validate(activation);
        return activation;
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
