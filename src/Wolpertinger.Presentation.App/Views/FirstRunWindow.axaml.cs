using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace Wolpertinger.Presentation.App.Views;

public partial class FirstRunWindow : Window
{
    public FirstRunWindow() => InitializeComponent();

    public event EventHandler? ConnectFrontierRequested;
    public event EventHandler? ContinueLocalRequested;
    public event EventHandler? TrySampleRequested;
    public event EventHandler<string>? EliteDataPathRequested;

    public void SetStatus(string? message)
        => this.FindControl<TextBlock>("StatusText")!.Text = message ?? string.Empty;

    private void OnConnectFrontier(object? sender, RoutedEventArgs args)
        => ConnectFrontierRequested?.Invoke(this, EventArgs.Empty);

    private void OnContinueLocal(object? sender, RoutedEventArgs args)
        => ContinueLocalRequested?.Invoke(this, EventArgs.Empty);

    private void OnTrySample(object? sender, RoutedEventArgs args)
        => TrySampleRequested?.Invoke(this, EventArgs.Empty);

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
