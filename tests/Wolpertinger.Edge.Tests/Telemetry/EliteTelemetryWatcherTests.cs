using System.Text;
using Wolpertinger.Edge.Runtime;
using Wolpertinger.Edge.Telemetry;

namespace Wolpertinger.Edge.Tests.Telemetry;

public sealed class EliteTelemetryWatcherTests
{
    [Fact]
    public async Task DuplicateDrainsDoNotDuplicateJournalRecords()
    {
        using var temp = new TempDirectory();
        var journal = Path.Combine(temp.Path, "Journal.2026-09-11T120000.01.log");
        await File.WriteAllTextAsync(journal, "one\n", new UTF8Encoding(false));
        var sink = new FakeSink();
        await using var watcher = new EliteTelemetryWatcher(temp.Path, sink);

        await watcher.DrainOnceAsync();
        await watcher.DrainOnceAsync();

        var record = Assert.Single(sink.JournalRecords);
        Assert.Equal("one", Encoding.UTF8.GetString(record.Payload));
    }

    [Fact]
    public async Task LaterDrainRecoversAppendEvenWithoutWatcherWake()
    {
        using var temp = new TempDirectory();
        var journal = Path.Combine(temp.Path, "Journal.2026-09-11T120100.01.log");
        await File.WriteAllTextAsync(journal, "one\n", new UTF8Encoding(false));
        var sink = new FakeSink();
        await using var watcher = new EliteTelemetryWatcher(temp.Path, sink);
        await watcher.DrainOnceAsync();

        await File.AppendAllTextAsync(journal, "two\n", new UTF8Encoding(false));
        await watcher.DrainOnceAsync();

        Assert.Equal(new[] { "one", "two" },
            sink.JournalRecords.Select(r => Encoding.UTF8.GetString(r.Payload)).ToArray());
    }

    [Fact]
    public async Task IdenticalStatusSnapshotIsPublishedOnlyOnce()
    {
        using var temp = new TempDirectory();
        await File.WriteAllTextAsync(Path.Combine(temp.Path, "Status.json"),
            "{\"Flags\":16,\"GuiFocus\":0}", new UTF8Encoding(false));
        var sink = new FakeSink();
        await using var watcher = new EliteTelemetryWatcher(temp.Path, sink);

        await watcher.DrainOnceAsync();
        await watcher.DrainOnceAsync();

        Assert.Single(sink.StatusSnapshots);
    }
    [Fact]
    public async Task PartialStatusIsSuppressedUntilComplete()
    {
        using var temp = new TempDirectory();
        var status = Path.Combine(temp.Path, "Status.json");
        await File.WriteAllTextAsync(status, "{\"Flags\":", new UTF8Encoding(false));
        var sink = new FakeSink();
        await using var watcher = new EliteTelemetryWatcher(temp.Path, sink);

        await watcher.DrainOnceAsync();
        Assert.Empty(sink.StatusSnapshots);

        await File.WriteAllTextAsync(status, "{\"Flags\":1,\"GuiFocus\":0}", new UTF8Encoding(false));
        await watcher.DrainOnceAsync();

        Assert.Single(sink.StatusSnapshots);
    }

    private sealed class FakeSink : IEliteTelemetrySink
    {
        public List<JournalSourceRecord> JournalRecords { get; } = [];
        public List<byte[]> StatusSnapshots { get; } = [];

        public Task ProcessJournalRecordAsync(JournalSourceRecord record, CancellationToken cancellationToken = default)
        {
            JournalRecords.Add(record);
            return Task.CompletedTask;
        }
        public Task ProcessStatusSnapshotAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        {
            StatusSnapshots.Add(payload.ToArray());
            return Task.CompletedTask;
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "wolpertinger-watcher-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
