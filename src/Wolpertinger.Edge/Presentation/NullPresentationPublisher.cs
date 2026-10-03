using System.Runtime.CompilerServices;
using Wolpertinger.Edge.Context;
using Wolpertinger.Edge.Facts;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Edge.Presentation;

public sealed class NullPresentationPublisher : IPresentationPublisher
{
    public static NullPresentationPublisher Instance { get; } = new();
    private NullPresentationPublisher() { }

    public PresentationSnapshot Current => PresentationSnapshot.Empty;

    public ValueTask PublishJumpAsync(JumpFact fact, ContextDecision decision, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public ValueTask PublishCommanderVesselAsync(CommanderVesselFact fact, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    public async IAsyncEnumerable<PresentationSnapshot> ReadUpdatesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
