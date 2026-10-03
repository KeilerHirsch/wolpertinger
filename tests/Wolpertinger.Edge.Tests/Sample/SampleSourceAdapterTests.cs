using System.Text;
using Wolpertinger.Edge.Sample;

namespace Wolpertinger.Edge.Tests.Sample;

public sealed class SampleSourceAdapterTests
{
    [Fact]
    public async Task FixtureRoutesRecordsSequentiallyToExplicitSampleEntrypoints()
    {
        using var temp = new TempFile(
            """
            {"source":"journal","payload":{"event":"Fileheader"}}
            {"source":"status","payload":{"Flags":0,"GuiFocus":0}}
            {"source":"commanderVessel","payload":{"commander":{"name":"CMDR SAMPLE"},"ship":{"name":"Sidewinder"}}}
            """);
        var target = new RecordingTarget();
        var adapter = new SampleSourceAdapter(target);

        await adapter.RunAsync(temp.Path);

        Assert.Equal(["journal", "status", "commanderVessel"], target.Kinds);
        Assert.Contains("\"Fileheader\"", Encoding.UTF8.GetString(target.Payloads[0]));
        Assert.Contains("\"Flags\":0", Encoding.UTF8.GetString(target.Payloads[1]));
        Assert.Contains("\"CMDR SAMPLE\"", Encoding.UTF8.GetString(target.Payloads[2]));
        Assert.Equal(1, target.MaximumConcurrentCalls);
    }

    [Fact]
    public async Task UnknownSourceFailsClosedBeforeCallingTarget()
    {
        using var temp = new TempFile(
            """{"source":"network","payload":{"value":1}}""");
        var target = new RecordingTarget();
        var adapter = new SampleSourceAdapter(target);

        var error = await Assert.ThrowsAsync<InvalidDataException>(
            () => adapter.RunAsync(temp.Path));

        Assert.Contains("Unsupported sample source", error.Message);
        Assert.Empty(target.Kinds);
    }

    private sealed class RecordingTarget : ISampleIngestTarget
    {
        private int _active;
        public int MaximumConcurrentCalls { get; private set; }
        public List<string> Kinds { get; } = [];
        public List<byte[]> Payloads { get; } = [];

        public Task ProcessSampleJournalAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
            => RecordAsync("journal", payload, cancellationToken);

        public Task ProcessSampleStatusAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
            => RecordAsync("status", payload, cancellationToken);

        public Task ProcessSampleCommanderVesselAsync(
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken = default)
            => RecordAsync("commanderVessel", payload, cancellationToken);

        private async Task RecordAsync(
            string kind,
            ReadOnlyMemory<byte> payload,
            CancellationToken cancellationToken)
        {
            var active = Interlocked.Increment(ref _active);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, active);
            try
            {
                await Task.Yield();
                cancellationToken.ThrowIfCancellationRequested();
                Kinds.Add(kind);
                Payloads.Add(payload.ToArray());
            }
            finally
            {
                Interlocked.Decrement(ref _active);
            }
        }
    }

    private sealed class TempFile : IDisposable
    {
        public TempFile(string content)
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"wolpertinger-sample-{Guid.NewGuid():N}.jsonl");
            File.WriteAllText(Path, content + Environment.NewLine);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (File.Exists(Path))
                File.Delete(Path);
        }
    }
}
