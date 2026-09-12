using System.Security.Cryptography;
using Wolpertinger.Edge.Evidence;

namespace Wolpertinger.Edge.Telemetry;

public interface IEliteTelemetrySink
{
    Task ProcessJournalRecordAsync(
        JournalSourceRecord record,
        CancellationToken cancellationToken = default);

    Task ProcessStatusSnapshotAsync(
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default);
}

public sealed class EliteTelemetryWatcher : IAsyncDisposable
{
    private readonly string _directory;
    private readonly IEliteTelemetrySink _sink;
    private readonly JournalTailer _tailer;
    private readonly AtomicSnapshotReader _snapshotReader;
    private readonly IReadOnlyList<RecoveredRawEvidence> _recovered;
    private readonly FileSystemWatcher _watcher;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private byte[]? _lastStatusDigest;
    private StatusMetadata? _lastAcceptedStatusMetadata;
    private bool _disposed;

    public EliteTelemetryWatcher(
        string eliteDataDirectory,
        IEliteTelemetrySink sink,
        IReadOnlyList<RecoveredRawEvidence>? recovered = null,
        AtomicSnapshotReader? snapshotReader = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eliteDataDirectory);
        _directory = Path.GetFullPath(eliteDataDirectory);
        Directory.CreateDirectory(_directory);
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _tailer = new JournalTailer(_directory);
        _snapshotReader = snapshotReader ?? new AtomicSnapshotReader();
        _recovered = recovered ?? Array.Empty<RecoveredRawEvidence>();
        _watcher = new FileSystemWatcher(_directory)
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };
        _watcher.Changed += OnWake;
        _watcher.Created += OnWake;
        _watcher.Deleted += OnWake;
        _watcher.Renamed += OnWake;
    }

    public async Task DrainOnceAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await foreach (var record in _tailer
            .ReadAvailableAsync(_recovered, cancellationToken)
            .ConfigureAwait(false))
        {
            await _sink.ProcessJournalRecordAsync(record, cancellationToken).ConfigureAwait(false);
        }

        await DrainStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DrainOnceAsync(cancellationToken).ConfigureAwait(false);

            using var cycle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(TimeSpan.FromMilliseconds(500), cycle.Token);
            var wake = _wake.WaitAsync(cycle.Token);
            var completed = await Task.WhenAny(delay, wake).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
            cycle.Cancel();
        }
    }

    private async Task DrainStatusAsync(CancellationToken cancellationToken)
    {
        var path = Path.Combine(_directory, "Status.json");
        if (!File.Exists(path)) return;

        var info = new FileInfo(path);
        var metadata = new StatusMetadata(info.Length, info.LastWriteTimeUtc.Ticks);
        if (_lastAcceptedStatusMetadata == metadata) return;

        var bytes = await _snapshotReader
            .ReadCompleteJsonAsync(path, cancellationToken)
            .ConfigureAwait(false);
        if (bytes is null) return;

        var digest = SHA256.HashData(bytes);
        _lastAcceptedStatusMetadata = metadata;
        if (_lastStatusDigest is not null
            && CryptographicOperations.FixedTimeEquals(_lastStatusDigest, digest))
        {
            return;
        }

        _lastStatusDigest = digest;
        await _sink.ProcessStatusSnapshotAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    private void OnWake(object? sender, FileSystemEventArgs args)
    {
        if (_disposed) return;
        try { _wake.Release(); }
        catch (SemaphoreFullException) { }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Changed -= OnWake;
        _watcher.Created -= OnWake;
        _watcher.Deleted -= OnWake;
        _watcher.Renamed -= OnWake;
        _watcher.Dispose();
        _wake.Dispose();
        return ValueTask.CompletedTask;
    }

    private readonly record struct StatusMetadata(long Length, long LastWriteTicks);
}
