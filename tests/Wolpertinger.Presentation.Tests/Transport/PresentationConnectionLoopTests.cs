using System.Runtime.CompilerServices;
using Wolpertinger.Presentation.Contracts;
using Wolpertinger.Presentation.State;
using Wolpertinger.Presentation.Transport;

namespace Wolpertinger.Presentation.Tests.Transport;

public sealed class PresentationConnectionLoopTests
{
    [Fact]
    public async Task IoFailureReconnectsAndValidSnapshotResetsBackoff()
    {
        var store = new PresentationStore();
        var clients = new Queue<IPresentationSnapshotClient>(
        [
            new ThrowingClient(new IOException("offline")),
            new SequenceClient(PresentationSnapshot.Empty with { Revision = 1 }),
        ]);
        var delays = new List<TimeSpan>();
        using var stop = new CancellationTokenSource();

        Task Delay(TimeSpan delay, CancellationToken _)
        {
            delays.Add(delay);
            if (delays.Count == 2)
                stop.Cancel();
            return Task.CompletedTask;
        }

        var loop = new PresentationConnectionLoop(
            () => clients.Dequeue(),
            store,
            Delay);

        await loop.RunAsync(stop.Token);

        Assert.Equal(
            [TimeSpan.FromMilliseconds(250), TimeSpan.FromMilliseconds(250)],
            delays);
        Assert.Equal(1UL, store.State.Snapshot?.Revision);
        Assert.Equal(PresentationConnectionState.Disconnected, store.State.Connection);
    }

    [Fact]
    public async Task IncompatibleSnapshotStopsWithoutRetry()
    {
        var store = new PresentationStore();
        var invalid = PresentationSnapshot.Empty with
        {
            ProtocolVersion = PresentationProtocol.Version + 1,
            Revision = 1,
        };
        var delayCalls = 0;
        var loop = new PresentationConnectionLoop(
            () => new SequenceClient(invalid),
            store,
            (_, _) =>
            {
                delayCalls++;
                return Task.CompletedTask;
            });

        await loop.RunAsync();

        Assert.Equal(PresentationConnectionState.Incompatible, store.State.Connection);
        Assert.Equal(0, delayCalls);
    }

    private sealed class SequenceClient(params PresentationSnapshot[] snapshots)
        : IPresentationSnapshotClient
    {
        public async IAsyncEnumerable<PresentationSnapshot> ReadSnapshotsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var snapshot in snapshots)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return snapshot;
                await Task.Yield();
            }
        }
    }

    private sealed class ThrowingClient(Exception error) : IPresentationSnapshotClient
    {
        public async IAsyncEnumerable<PresentationSnapshot> ReadSnapshotsAsync(
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            throw error;
#pragma warning disable CS0162
            yield break;
#pragma warning restore CS0162
        }
    }
}
