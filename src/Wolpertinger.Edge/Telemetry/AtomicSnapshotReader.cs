using System.Text.Json;

namespace Wolpertinger.Edge.Telemetry;

public sealed record SnapshotReadDiagnostic(
    string Code,
    int Attempts,
    string Message);

public sealed class AtomicSnapshotReader
{
    private const int MaxAttempts = 5;
    private readonly Func<string, CancellationToken, Task<byte[]>> _read;
    private readonly Func<CancellationToken, Task> _delay;

    public AtomicSnapshotReader()
        : this(
            static (path, ct) => File.ReadAllBytesAsync(path, ct),
            static ct => Task.Delay(TimeSpan.FromMilliseconds(25), ct))
    {
    }

    internal AtomicSnapshotReader(
        Func<string, CancellationToken, Task<byte[]>> read,
        Func<CancellationToken, Task> delay)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        _delay = delay ?? throw new ArgumentNullException(nameof(delay));
    }

    public SnapshotReadDiagnostic? LastDiagnostic { get; private set; }

    public async Task<byte[]?> ReadCompleteJsonAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        LastDiagnostic = null;
        string lastCode = "IoFailure";
        string lastMessage = "Snapshot read failed.";

        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var bytes = await _read(path, cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(bytes);
                LastDiagnostic = null;
                return bytes;
            }
            catch (JsonException ex)
            {
                lastCode = "MalformedJson";
                lastMessage = ex.Message;
            }
            catch (IOException ex)
            {
                lastCode = "IoFailure";
                lastMessage = ex.Message;
            }

            if (attempt < MaxAttempts)
            {
                await _delay(cancellationToken).ConfigureAwait(false);
            }
        }

        LastDiagnostic = new SnapshotReadDiagnostic(lastCode, MaxAttempts, lastMessage);
        return null;
    }
}
