using System.Runtime.InteropServices;

namespace Wolpertinger.AppHost.Windows;

public sealed class WindowsEliteDataPathResolver
{
    private static readonly Guid SavedGamesFolderId =
        new("4C5C32FF-BB9D-43B0-B5B4-2D72E54EAA4");

    private readonly Func<string> _savedGamesBasePath;

    public WindowsEliteDataPathResolver()
        : this(ResolveSavedGamesBasePath)
    {
    }

    public WindowsEliteDataPathResolver(Func<string> savedGamesBasePath)
        => _savedGamesBasePath = savedGamesBasePath
            ?? throw new ArgumentNullException(nameof(savedGamesBasePath));

    public string? Resolve()
    {
        var basePath = _savedGamesBasePath();
        ArgumentException.ThrowIfNullOrWhiteSpace(basePath);
        var candidate = Path.GetFullPath(Path.Combine(
            basePath,
            "Frontier Developments",
            "Elite Dangerous"));
        return Directory.Exists(candidate) ? candidate : null;
    }
    private static string ResolveSavedGamesBasePath()
    {
        var folderId = SavedGamesFolderId;
        var hr = SHGetKnownFolderPath(
            ref folderId,
            0,
            IntPtr.Zero,
            out var pathPointer);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);

        try
        {
            return Marshal.PtrToStringUni(pathPointer)
                ?? throw new InvalidOperationException("Saved Games path was null.");
        }
        finally
        {
            Marshal.FreeCoTaskMem(pathPointer);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        ref Guid rfid,
        uint dwFlags,
        IntPtr hToken,
        out IntPtr ppszPath);
}
