using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Wolpertinger.Presentation.App.Activation;
using Wolpertinger.Presentation.App.Onboarding;
using Wolpertinger.Presentation.App.Preferences;
using Wolpertinger.Presentation.App.Views;
using Wolpertinger.Presentation.Preferences;
using Wolpertinger.Presentation.State;
using Wolpertinger.Presentation.Transport;
using Wolpertinger.Presentation.ViewModels;
using Wolpertinger.Product.Contracts;

namespace Wolpertinger.Presentation.App;

/// <summary>
/// Owns the UI lifecycle. Product commands cross the bounded activation contract;
/// presentation state comes only from the validated snapshot store.
/// </summary>
public sealed class PresentationApplicationController : IDisposable
{
    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly PresentationStore store;
    private readonly PresentationPreferencesStore preferencesStore;
    private readonly FirstRunStateStore firstRunStore;
    private readonly IProductActivationClient activationClient;
    private readonly CancellationTokenSource connectionStop = new();

    private PresentationPreferences preferences = PresentationPreferences.Default;
    private FirstRunState firstRunState = FirstRunState.Default;
    private OverlayWindow? overlay;
    private FullscreenHubWindow? hub;
    private DiagnosticsWindow? diagnostics;
    private FirstRunWindow? firstRun;
    private SettingsWindow? settings;
    private Task? connectionTask;
    private Task? startup;
    private Task pendingSave = Task.CompletedTask;
    private bool exiting;
    private bool disposed;
    private string? preferenceDiagnostic;

    public PresentationApplicationController(
        IClassicDesktopStyleApplicationLifetime desktop,
        PresentationStore? store = null,
        PresentationPreferencesStore? preferencesStore = null,
        FirstRunStateStore? firstRunStore = null,
        IProductActivationClient? activationClient = null)
    {
        this.desktop = desktop;
        this.store = store ?? new PresentationStore();
        this.preferencesStore = preferencesStore ?? new PresentationPreferencesStore();
        this.firstRunStore = firstRunStore ?? new FirstRunStateStore();
        this.activationClient = activationClient ?? new ProductActivationClient();
        this.store.StateChanged += OnStateChanged;
    }

    public Task StartAsync() => startup ??= InitializeAsync();

    private async Task InitializeAsync()
    {
        preferences = await preferencesStore.LoadAsync().ConfigureAwait(true);
        firstRunState = await firstRunStore.LoadAsync().ConfigureAwait(true);
        if (disposed)
            return;

        overlay = new OverlayWindow(preferences);
        hub = new FullscreenHubWindow(preferences);
        diagnostics = new DiagnosticsWindow();
        firstRun = new FirstRunWindow();
        settings = new SettingsWindow();

        foreach (var window in new Window[] { overlay, hub, diagnostics, firstRun, settings })
            window.Closing += OnSurfaceClosing;

        firstRun.ConnectFrontierRequested += OnFirstRunConnectFrontier;
        firstRun.ContinueLocalRequested += OnFirstRunContinueLocal;
        firstRun.TrySampleRequested += OnFirstRunTrySample;
        firstRun.EliteDataPathRequested += OnFirstRunEliteDataPath;

        settings.SmartAutoRequested += OnSettingsSmartAuto;
        settings.ManualCompositionRequested += OnSettingsManualComposition;
        settings.ConnectFrontierRequested += OnSettingsConnectFrontier;
        settings.DisconnectFrontierRequested += OnSettingsDisconnectFrontier;
        settings.ReturnLiveRequested += OnSettingsReturnLive;
        settings.EnterSampleRequested += OnSettingsEnterSample;
        settings.EliteDataPathRequested += OnSettingsEliteDataPath;

        connectionTask = new PresentationConnectionLoop(
            () => new PresentationPipeClient(),
            store).RunAsync(connectionStop.Token);

        RefreshSurfaces();
        if (!firstRunState.Completed)
        {
            firstRun.Show();
            firstRun.Activate();
            return;
        }

        if (preferences.OverlayVisible)
            overlay.Show();
        if (preferences.HubVisible)
            hub.Show();
        RefreshSurfaceDiagnostics();
    }
    public async Task ToggleOverlayAsync()
    {
        await StartAsync();
        if (exiting || disposed)
            return;
        if (overlay!.IsVisible)
            overlay.Hide();
        else if (overlay.Attachment.LastDiagnostic is null || overlay.Attachment.TryRecover())
            overlay.Show();
        preferences = preferences with { OverlayVisible = overlay.IsVisible };
        RefreshSurfaceDiagnostics();
        await QueuePreferenceSave();
    }

    public async Task ShowHubAsync()
    {
        await StartAsync();
        if (exiting || disposed)
            return;
        if (hub!.Attachment.LastDiagnostic is null || hub.Attachment.TryRecover())
            hub.Show();
        if (hub.IsVisible)
            hub.Activate();
        preferences = preferences with { HubVisible = hub.IsVisible };
        RefreshSurfaceDiagnostics();
        await QueuePreferenceSave();
    }

    public async Task ShowSettingsAsync()
    {
        await StartAsync();
        if (exiting || disposed)
            return;
        settings!.Show();
        settings.Activate();
    }

    public async Task ShowDiagnosticsAsync()
    {
        await StartAsync();
        if (exiting || disposed)
            return;
        RefreshSurfaceDiagnostics();
        diagnostics!.Show();
        diagnostics.Activate();
    }

    public async Task ConnectFrontierAsync()
        => await SendTrayActivationAsync(ProductActivationKind.ConnectFrontier);

    public async Task EnterSampleAsync()
        => await SendTrayActivationAsync(ProductActivationKind.EnterSample);

    public async Task ExitAsync()
    {
        await StartAsync();
        if (exiting || disposed)
            return;
        exiting = true;
        await QueuePreferenceSave();
        try
        {
            await SendActivationAsync(new ProductActivation(ProductActivationKind.Exit));
        }
        catch (Exception error) when (error is IOException or TimeoutException)
        {
            Trace.TraceWarning("Product exit activation failed: {0}", error.Message);
        }
        Dispose();
        desktop.Shutdown();
    }

    private async Task SendTrayActivationAsync(ProductActivationKind kind)
    {
        await StartAsync();
        if (exiting || disposed)
            return;
        try
        {
            await SendActivationAsync(new ProductActivation(kind));
        }
        catch (Exception error) when (error is IOException or TimeoutException)
        {
            Trace.TraceWarning("Product activation {0} failed: {1}", kind, error.Message);
        }
    }

    private async Task SendActivationAsync(ProductActivation activation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await activationClient.SendAsync(activation, timeout.Token).ConfigureAwait(false);
    }

    private async Task CompleteFirstRunAsync(ProductActivation activation)
    {
        try
        {
            firstRun!.SetStatus("Starting…");
            await SendActivationAsync(activation);
            var completed = new FirstRunState(
                FirstRunState.CurrentSchemaVersion,
                Completed: true);
            await firstRunStore.SaveAsync(completed);
            firstRunState = completed;
            firstRun.Hide();
            firstRun.SetStatus(null);
            await ShowHubAsync();
        }
        catch (Exception error) when (
            error is IOException
                or UnauthorizedAccessException
                or TimeoutException
                or InvalidDataException
                or InvalidOperationException)
        {
            firstRun!.SetStatus($"Could not start: {error.Message}");
        }
    }

    private async void OnFirstRunConnectFrontier(object? sender, EventArgs args)
        => await CompleteFirstRunAsync(
            new ProductActivation(ProductActivationKind.ConnectFrontier));

    private async void OnFirstRunContinueLocal(object? sender, EventArgs args)
        => await CompleteFirstRunAsync(
            new ProductActivation(ProductActivationKind.ReturnLive));

    private async void OnFirstRunTrySample(object? sender, EventArgs args)
        => await CompleteFirstRunAsync(
            new ProductActivation(ProductActivationKind.EnterSample));

    private async void OnFirstRunEliteDataPath(object? sender, string path)
    {
        try
        {
            firstRun!.SetStatus("Applying Elite data path…");
            await SendActivationAsync(new ProductActivation(
                ProductActivationKind.SetEliteDataPath,
                path));
            firstRun.SetStatus("Elite data path saved. Choose how to start.");
        }
        catch (Exception error) when (
            error is IOException or TimeoutException or InvalidOperationException)
        {
            firstRun!.SetStatus($"Could not apply path: {error.Message}");
        }
    }

    private async Task SendSettingsActivationAsync(ProductActivation activation)
    {
        try
        {
            settings!.SetStatus("Applying…");
            await SendActivationAsync(activation);
            settings.SetStatus("Applied.");
        }
        catch (Exception error) when (
            error is IOException or TimeoutException or InvalidOperationException)
        {
            settings!.SetStatus($"Could not apply: {error.Message}");
        }
    }

    private async void OnSettingsSmartAuto(object? sender, EventArgs args)
        => await SendSettingsActivationAsync(
            new ProductActivation(ProductActivationKind.SetSmartAuto));

    private async void OnSettingsManualComposition(object? sender, string value)
        => await SendSettingsActivationAsync(
            new ProductActivation(ProductActivationKind.SetManualComposition, value));

    private async void OnSettingsConnectFrontier(object? sender, EventArgs args)
        => await SendSettingsActivationAsync(
            new ProductActivation(ProductActivationKind.ConnectFrontier));

    private async void OnSettingsDisconnectFrontier(object? sender, EventArgs args)
        => await SendSettingsActivationAsync(
            new ProductActivation(ProductActivationKind.DisconnectFrontier));

    private async void OnSettingsReturnLive(object? sender, EventArgs args)
        => await SendSettingsActivationAsync(
            new ProductActivation(ProductActivationKind.ReturnLive));

    private async void OnSettingsEnterSample(object? sender, EventArgs args)
        => await SendSettingsActivationAsync(
            new ProductActivation(ProductActivationKind.EnterSample));

    private async void OnSettingsEliteDataPath(object? sender, string path)
        => await SendSettingsActivationAsync(
            new ProductActivation(ProductActivationKind.SetEliteDataPath, path));

    private async void OnSurfaceClosing(object? sender, WindowClosingEventArgs args)
    {
        if (exiting || disposed)
            return;

        args.Cancel = true;
        ((Window)sender!).Hide();
        if (ReferenceEquals(sender, overlay))
            preferences = preferences with { OverlayVisible = false };
        else if (ReferenceEquals(sender, hub))
            preferences = preferences with { HubVisible = false };
        else
            return;

        await QueuePreferenceSave();
    }

    private Task QueuePreferenceSave()
        => pendingSave = SavePreferencesAsync(pendingSave, preferences);

    private async Task SavePreferencesAsync(
        Task previous,
        PresentationPreferences value)
    {
        await previous;
        try
        {
            await preferencesStore.SaveAsync(value);
            preferenceDiagnostic = null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            preferenceDiagnostic = "Presentation preferences could not be saved.";
            Trace.TraceWarning("{0} {1}", preferenceDiagnostic, error.Message);
        }
        RefreshSurfaceDiagnostics();
    }
    private void OnStateChanged(object? sender, EventArgs args)
    {
        if (Dispatcher.UIThread.CheckAccess())
            RefreshSurfaces();
        else
            Dispatcher.UIThread.Post(RefreshSurfaces);
    }

    private void RefreshSurfaces()
    {
        if (disposed || overlay is null || hub is null || diagnostics is null)
            return;

        var state = store.State;
        var smart = SmartHubViewModel.Create(state);
        overlay.DataContext = smart;
        hub.DataContext = smart;

        var hasSnapshot = state.Snapshot is not null;
        foreach (var window in new Window[] { overlay, hub })
        {
            window.FindControl<StackPanel>("Body")!.IsVisible = hasSnapshot;
            var emptyState = window.FindControl<TextBlock>("EmptyState")!;
            emptyState.IsVisible = !hasSnapshot;
            emptyState.Text = $"{state.Connection} — waiting for validated state";
        }

        var hasJump = state.Snapshot?.Jump is not null;
        diagnostics.DataContext = hasJump
            ? PresentationViewModelFactory.CreateDiagnostics(state)
            : null;
        diagnostics.FindControl<StackPanel>("Body")!.IsVisible = hasJump;
        var diagnosticsEmpty = diagnostics.FindControl<TextBlock>("EmptyState")!;
        diagnosticsEmpty.IsVisible = !hasJump;
        diagnosticsEmpty.Text = $"{state.Connection} — no validated jump available";
        RefreshSurfaceDiagnostics();
    }

    private void RefreshSurfaceDiagnostics()
    {
        if (disposed || diagnostics is null)
            return;
        diagnostics.FindControl<TextBlock>("SurfaceStatus")!.Text =
            string.Join(
                Environment.NewLine,
                new[]
                {
                    $"Connection: {store.State.Connection}",
                    preferenceDiagnostic,
                    overlay?.Attachment.LastDiagnostic,
                    hub?.Attachment.LastDiagnostic,
                }.Where(message => !string.IsNullOrWhiteSpace(message)));
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        connectionStop.Cancel();
        store.StateChanged -= OnStateChanged;
        overlay?.Attachment.Dispose();
        hub?.Attachment.Dispose();

        foreach (var window in new Window?[]
                 {
                     overlay, hub, diagnostics, firstRun, settings,
                 })
        {
            if (window is null)
                continue;
            window.Closing -= OnSurfaceClosing;
            window.Close();
        }

        connectionStop.Dispose();
        _ = connectionTask;
    }
}
