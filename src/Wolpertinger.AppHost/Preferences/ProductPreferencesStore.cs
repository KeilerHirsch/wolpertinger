using System.Text.Json;

namespace Wolpertinger.AppHost.Preferences;

public sealed class ProductPreferencesStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ProductPreferencesStore(string? path = null)
        => _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WOLPERTINGER",
            "product.json"));

    public async Task<ProductPreferences> LoadAsync()
    {
        try
        {
            await using var stream = new FileStream(
                _path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read | FileShare.Delete,
                4096,
                FileOptions.Asynchronous);
            var value = await JsonSerializer.DeserializeAsync<ProductPreferences>(stream)
                .ConfigureAwait(false);
            if (value is not { SchemaVersion: ProductPreferences.CurrentSchemaVersion })
                return ProductPreferences.Default;
            if (value.EliteDataDirectory is not null
                && (!Path.IsPathFullyQualified(value.EliteDataDirectory)
                    || !Directory.Exists(value.EliteDataDirectory)))
            {
                return ProductPreferences.Default;
            }
            return value;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            return ProductPreferences.Default;
        }
    }

    public async Task SaveAsync(ProductPreferences value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.SchemaVersion != ProductPreferences.CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported product preferences schema.");
        if (value.EliteDataDirectory is not null
            && (!Path.IsPathFullyQualified(value.EliteDataDirectory)
                || !Directory.Exists(value.EliteDataDirectory)))
        {
            throw new InvalidDataException("Elite data directory must be an existing absolute path.");
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        var temporary = _path + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await using (var stream = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, value).ConfigureAwait(false);
                await stream.FlushAsync().ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            if (File.Exists(_path))
                File.Replace(temporary, _path, null);
            else
                File.Move(temporary, _path);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception error) when (
                error is IOException or UnauthorizedAccessException)
            {
            }
            _gate.Release();
        }
    }
}
