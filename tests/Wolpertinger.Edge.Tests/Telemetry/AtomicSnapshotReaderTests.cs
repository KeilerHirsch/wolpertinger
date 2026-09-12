using System.Text;
using Wolpertinger.Edge.Telemetry;

namespace Wolpertinger.Edge.Tests.Telemetry;

public sealed class AtomicSnapshotReaderTests
{
    [Fact]
    public async Task PartialJsonRetriesUntilCompleteWithoutSleeping()
    {
        var reads = new Queue<byte[]>(new[]
        {
            Encoding.UTF8.GetBytes("{\"Flags\":"),
            Encoding.UTF8.GetBytes("{\"Flags\":16,\"GuiFocus\":0}"),
        });
        var delays = 0;
        var reader = new AtomicSnapshotReader(
            (_, _) => Task.FromResult(reads.Dequeue()),
            _ => { delays++; return Task.CompletedTask; });

        var bytes = await reader.ReadCompleteJsonAsync("Status.json");

        Assert.Equal("{\"Flags\":16,\"GuiFocus\":0}", Encoding.UTF8.GetString(bytes!));
        Assert.Equal(1, delays);
        Assert.Null(reader.LastDiagnostic);
    }

    [Fact]
    public async Task IoRaceThenReplacementSucceeds()
    {
        var attempt = 0;
        var reader = new AtomicSnapshotReader(
            (_, _) =>
            {
                attempt++;
                if (attempt == 1) throw new IOException("sharing race");
                return Task.FromResult(Encoding.UTF8.GetBytes("{\"Flags\":0,\"GuiFocus\":0}"));
            },
            _ => Task.CompletedTask);

        var bytes = await reader.ReadCompleteJsonAsync("Status.json");

        Assert.NotNull(bytes);
        Assert.Equal(2, attempt);
    }

    [Fact]
    public async Task PermanentMalformedContentStopsAfterFiveAttempts()
    {
        var attempts = 0;
        var reader = new AtomicSnapshotReader(
            (_, _) => { attempts++; return Task.FromResult("{"u8.ToArray()); },
            _ => Task.CompletedTask);

        var bytes = await reader.ReadCompleteJsonAsync("Status.json");
        Assert.Null(bytes);
        Assert.Equal(5, attempts);
        Assert.NotNull(reader.LastDiagnostic);
        Assert.Equal("MalformedJson", reader.LastDiagnostic!.Code);
        Assert.Equal(5, reader.LastDiagnostic.Attempts);
    }

    [Fact]
    public async Task MissingFileIsBoundedIoFailure()
    {
        var attempts = 0;
        var reader = new AtomicSnapshotReader(
            (_, _) =>
            {
                attempts++;
                throw new FileNotFoundException("missing");
            },
            _ => Task.CompletedTask);

        var bytes = await reader.ReadCompleteJsonAsync("Status.json");

        Assert.Null(bytes);
        Assert.Equal(5, attempts);
        Assert.Equal("IoFailure", reader.LastDiagnostic!.Code);
    }

    [Fact]
    public async Task CancellationStopsImmediately()
    {
        var reads = 0;
        var reader = new AtomicSnapshotReader(
            (_, _) => { reads++; return Task.FromResult("{}"u8.ToArray()); },
            _ => Task.CompletedTask);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reader.ReadCompleteJsonAsync("Status.json", cancellation.Token));
        Assert.Equal(0, reads);
    }
}
