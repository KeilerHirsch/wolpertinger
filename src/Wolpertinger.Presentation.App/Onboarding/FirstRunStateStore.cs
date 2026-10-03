using System.Text.Json;

namespace Wolpertinger.Presentation.App.Onboarding;

public sealed class FirstRunStateStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public FirstRunStateStore(string? path = null)
        => _path = Path.GetFullPath(path ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WOLPERTINGER",
            "first-run.json"));

    public async Task<FirstRunState> LoadAsync()
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
            var state = await JsonSerializer.DeserializeAsync<FirstRunState>(stream)
                .ConfigureAwait(false);
            return state is { SchemaVersion: FirstRunState.CurrentSchemaVersion }
                ? state
                : FirstRunState.Default;
        }
        catch (Exception error) when (
            error is IOException or UnauthorizedAccessException or JsonException)
        {
            return FirstRunState.Default;
        }
    }

    public async Task SaveAsync(FirstRunState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.SchemaVersion != FirstRunState.CurrentSchemaVersion)
            throw new InvalidDataException("Unsupported First Run state schema.");

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
                await JsonSerializer.SerializeAsync(stream, state).ConfigureAwait(false);
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
