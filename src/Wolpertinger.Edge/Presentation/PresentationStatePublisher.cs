using System.Threading.Channels;
using Wolpertinger.Edge.Context;
using Wolpertinger.Edge.Facts;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Edge.Presentation;

public sealed class PresentationStatePublisher : IPresentationPublisher
{
    private readonly object _gate = new();
    private readonly Channel<PresentationSnapshot> _updates = Channel.CreateBounded<PresentationSnapshot>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private PresentationSnapshot _current = PresentationSnapshot.Empty;

    public PresentationSnapshot Current => Volatile.Read(ref _current);

    public ValueTask PublishJumpAsync(
        JumpFact fact,
        ContextDecision decision,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish(current => JumpPresentationProjector.Project(
            fact, decision, current, checked(current.Revision + 1)));
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishCommanderVesselAsync(
        CommanderVesselFact fact,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(fact);
        Publish(current => current with
        {
            Revision = checked(current.Revision + 1),
            CommanderVessel = CommanderVesselPresentationProjector.Project(fact),
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishContextAsync(
        PresentationGameContext context,
        PresentationIntent intent,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(intent);
        Publish(current => current with
        {
            Revision = checked(current.Revision + 1),
            Context = context,
            Intent = intent,
        });
        return ValueTask.CompletedTask;
    }
    public ValueTask PublishRuntimeHealthAsync(
        RuntimeHealthPresentation health,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(health);
        Publish(current => current with
        {
            Revision = checked(current.Revision + 1),
            RuntimeHealth = health,
        });
        return ValueTask.CompletedTask;
    }

    public ValueTask PublishFrontierAccountAsync(
        FrontierAccountPresentation account,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(account);
        Publish(current => current with
        {
            Revision = checked(current.Revision + 1),
            FrontierAccount = account,
        });
        return ValueTask.CompletedTask;
    }

    private void Publish(Func<PresentationSnapshot, PresentationSnapshot> update)
    {
        lock (_gate)
        {
            var snapshot = update(_current);
            PresentationSnapshotValidator.Validate(snapshot);
            Volatile.Write(ref _current, snapshot);
            _updates.Writer.TryWrite(snapshot);
        }
    }

    public IAsyncEnumerable<PresentationSnapshot> ReadUpdatesAsync(
        CancellationToken cancellationToken = default)
        => _updates.Reader.ReadAllAsync(cancellationToken);
}
