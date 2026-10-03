using Wolpertinger.Presentation.State;

namespace Wolpertinger.Presentation.Transport;

public sealed class PresentationConnectionLoop
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumDelay = TimeSpan.FromSeconds(2);

    private readonly Func<IPresentationSnapshotClient> _clientFactory;
    private readonly PresentationStore _store;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public PresentationConnectionLoop(
        Func<IPresentationSnapshotClient> clientFactory,
        PresentationStore store,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _delay = delay ?? Task.Delay;
    }

    public async Task RunAsync(CancellationToken cancellationToken = default)
    {
        var backoff = InitialDelay;
        while (!cancellationToken.IsCancellationRequested)
        {
            _store.MarkConnecting();
            var receivedSnapshot = false;
            try
            {
                var client = _clientFactory()
                    ?? throw new InvalidOperationException("Presentation client factory returned null.");
                await foreach (var snapshot in client.ReadSnapshotsAsync(cancellationToken)
                                   .WithCancellation(cancellationToken)
                                   .ConfigureAwait(false))
                {
                    _store.ApplySnapshot(snapshot);
                    receivedSnapshot = true;
                    backoff = InitialDelay;
                }
                _store.MarkDisconnected();
            }
            catch (InvalidDataException)
            {
                _store.MarkIncompatible();
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error) when (error is IOException or TimeoutException)
            {
                _store.MarkDisconnected();
            }

            if (cancellationToken.IsCancellationRequested)
                return;

            await _delay(backoff, cancellationToken).ConfigureAwait(false);
            if (!receivedSnapshot)
                backoff = NextDelay(backoff);
        }
    }

    private static TimeSpan NextDelay(TimeSpan current)
        => TimeSpan.FromMilliseconds(
            Math.Min(MaximumDelay.TotalMilliseconds, current.TotalMilliseconds * 2));
}
