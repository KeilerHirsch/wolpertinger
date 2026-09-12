using Wolpertinger.AppHost.Windows;

namespace Wolpertinger.AppHost.Tests;

public sealed class WindowsEliteDataPathResolverTests
{
    [Fact]
    public void ExistingDefaultDirectoryIsResolvedFromSavedGamesBase()
    {
        using var temp = new TempDirectory();
        var elite = Path.Combine(temp.Path, "Frontier Developments", "Elite Dangerous");
        Directory.CreateDirectory(elite);
        var resolver = new WindowsEliteDataPathResolver(() => temp.Path);

        var resolved = resolver.Resolve();

        Assert.Equal(Path.GetFullPath(elite), resolved);
    }

    [Fact]
    public void MissingDefaultDirectoryReturnsNull()
    {
        using var temp = new TempDirectory();
        var resolver = new WindowsEliteDataPathResolver(() => temp.Path);

        Assert.Null(resolver.Resolve());
    }
    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "wolpertinger-apphost-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
