using System.Text;
using Wolpertinger.Edge.Telemetry;

namespace Wolpertinger.Edge.Tests.Telemetry;

public sealed class JournalTailerSharedWriterTests
{
    [Fact]
    public async Task ReadsNewLinesWhileWriterHandleRemainsOpen()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "Journal.2026-10-04T000001.01.log");
        await using var writer = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            FileOptions.Asynchronous);

        await writer.WriteAsync("first\n"u8.ToArray());
        await writer.FlushAsync();

        var tailer = new JournalTailer(temp.Path);
        var first = Assert.Single(await CollectAsync(tailer.ReadAvailableAsync([])));
        Assert.Equal("first", Encoding.UTF8.GetString(first.Payload));

        await writer.WriteAsync("second\n"u8.ToArray());
        await writer.FlushAsync();

        var second = Assert.Single(await CollectAsync(tailer.ReadAvailableAsync([])));
        Assert.Equal("second", Encoding.UTF8.GetString(second.Payload));
        Assert.Equal((ulong)"first\n"u8.Length, second.Locator.SourceOffset);
    }

    private static async Task<List<JournalSourceRecord>> CollectAsync(
        IAsyncEnumerable<JournalSourceRecord> source)
    {
        var records = new List<JournalSourceRecord>();
        await foreach (var record in source)
            records.Add(record);
        return records;
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "wolpertinger-journal-shared-writer-tests",
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
