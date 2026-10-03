using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Presentation.App.Views;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        this.FindControl<ComboBox>("CompositionChoice")!.ItemsSource =
            Enum.GetNames<PresentationComposition>();
        this.FindControl<ComboBox>("CompositionChoice")!.SelectedItem =
            PresentationComposition.Flight.ToString();
    }

    public event EventHandler? SmartAutoRequested;
    public event EventHandler<string>? ManualCompositionRequested;
    public event EventHandler? ConnectFrontierRequested;
    public event EventHandler? DisconnectFrontierRequested;
    public event EventHandler? ReturnLiveRequested;
    public event EventHandler? EnterSampleRequested;
    public event EventHandler<string>? EliteDataPathRequested;

    public void SetStatus(string? message)
        => this.FindControl<TextBlock>("StatusText")!.Text = message ?? string.Empty;

    private void OnSmartAuto(object? sender, RoutedEventArgs args)
        => SmartAutoRequested?.Invoke(this, EventArgs.Empty);

    private void OnManualComposition(object? sender, RoutedEventArgs args)
    {
        if (this.FindControl<ComboBox>("CompositionChoice")!.SelectedItem is string value)
            ManualCompositionRequested?.Invoke(this, value);
    }

    private void OnConnectFrontier(object? sender, RoutedEventArgs args)
        => ConnectFrontierRequested?.Invoke(this, EventArgs.Empty);

    private void OnDisconnectFrontier(object? sender, RoutedEventArgs args)
        => DisconnectFrontierRequested?.Invoke(this, EventArgs.Empty);

    private void OnReturnLive(object? sender, RoutedEventArgs args)
        => ReturnLiveRequested?.Invoke(this, EventArgs.Empty);

    private void OnEnterSample(object? sender, RoutedEventArgs args)
        => EnterSampleRequested?.Invoke(this, EventArgs.Empty);

    private async void OnLocateEliteData(object? sender, RoutedEventArgs args)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = "Locate Elite Dangerous data",
                AllowMultiple = false,
            });
        var path = folders.FirstOrDefault()?.TryGetLocalPath();
        if (!string.IsNullOrWhiteSpace(path))
            EliteDataPathRequested?.Invoke(this, path);
    }
}
