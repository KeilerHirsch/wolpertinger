using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace Wolpertinger.Presentation.App;

public partial class App : Application
{
    private PresentationApplicationController controller = null!;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            throw new InvalidOperationException("The presentation shell requires a classic desktop lifetime.");

        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        controller = new PresentationApplicationController(desktop);
        desktop.Startup += OnStartup;
        desktop.Exit += OnDesktopExit;
        base.OnFrameworkInitializationCompleted();
    }

    private async void OnStartup(object? sender, ControlledApplicationLifetimeStartupEventArgs args)
        => await controller.StartAsync();

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs args)
    {
        controller.Dispose();
        if (TrayIcon.GetIcons(this) is { } icons)
            foreach (var icon in icons)
                icon.Dispose();
    }

    private async void OnOpen(object? sender, EventArgs args)
        => await controller.ShowHubAsync();

    private async void OnToggleOverlay(object? sender, EventArgs args)
        => await controller.ToggleOverlayAsync();

    private async void OnConnectFrontier(object? sender, EventArgs args)
        => await controller.ConnectFrontierAsync();

    private async void OnEnterSample(object? sender, EventArgs args)
        => await controller.EnterSampleAsync();

    private async void OnShowSettings(object? sender, EventArgs args)
        => await controller.ShowSettingsAsync();

    private async void OnShowDiagnostics(object? sender, EventArgs args)
        => await controller.ShowDiagnosticsAsync();

    private async void OnExit(object? sender, EventArgs args)
        => await controller.ExitAsync();
}
