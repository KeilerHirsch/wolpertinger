using Wolpertinger.AppHost.Preferences;

namespace Wolpertinger.AppHost.Tests;

public sealed class ProductPreferencesStoreTests
{
    [Fact]
    public async Task EliteDirectoryRoundTripsAtomically()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"wol-elite-{Guid.NewGuid():N}");
        var file = Path.Combine(Path.GetTempPath(), $"wol-product-{Guid.NewGuid():N}.json");
        Directory.CreateDirectory(directory);
        try
        {
            var store = new ProductPreferencesStore(file);
            await store.SaveAsync(new ProductPreferences(
                ProductPreferences.CurrentSchemaVersion,
                directory));

            var loaded = await store.LoadAsync();
            Assert.Equal(directory, loaded.EliteDataDirectory);
            Assert.False(File.Exists(file + ".tmp"));
        }
        finally
        {
            if (File.Exists(file)) File.Delete(file);
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task MissingOrStaleEliteDirectoryFallsBackToDefault()
    {
        var file = Path.Combine(Path.GetTempPath(), $"wol-product-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(
                file,
                """{"SchemaVersion":1,"EliteDataDirectory":"Z:\\definitely-missing-wolpertinger"}""");
            var loaded = await new ProductPreferencesStore(file).LoadAsync();
            Assert.Null(loaded.EliteDataDirectory);
        }
        finally
        {
            File.Delete(file);
        }
    }
}
