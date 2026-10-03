using Wolpertinger.Edge.Context;
using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Edge.Tests.Context;

public sealed class SmartPresentationPolicyTests
{
    public static IEnumerable<object[]> AutoMappings()
    {
        yield return [GameContext.InactiveOrNoGame, PresentationComposition.Quiet];
        yield return [GameContext.MainMenu, PresentationComposition.Quiet];
        yield return [GameContext.Docked, PresentationComposition.Station];
        yield return [GameContext.StationServices, PresentationComposition.Station];
        yield return [GameContext.Flight, PresentationComposition.Flight];
        yield return [GameContext.Supercruise, PresentationComposition.Flight];
        yield return [GameContext.GalaxyMap, PresentationComposition.GalaxyMap];
        yield return [GameContext.SystemMap, PresentationComposition.SystemMap];
        yield return [GameContext.JumpPreparation, PresentationComposition.JumpPreparation];
        yield return [GameContext.FsdJump, PresentationComposition.JumpTransit];
        yield return [GameContext.PostJump, PresentationComposition.PostJump];
        yield return [GameContext.Unknown, PresentationComposition.Unknown];
    }

    [Theory]
    [MemberData(nameof(AutoMappings))]
    public void JournalOrTrustedMappingsAreImmediate(
        GameContext context,
        PresentationComposition expected)
    {
        var policy = new SmartPresentationPolicy();
        var origin = context == GameContext.PostJump
            ? GameContextSignalOrigin.Trusted
            : GameContextSignalOrigin.Journal;
        var intent = policy.Decide(new GameContextTransition(context, origin));
        Assert.Equal(expected, intent.Composition);
        Assert.Equal(PresentationSelectionMode.Auto, intent.SelectionMode);
    }

    [Fact]
    public void ManualPinOverridesOnlyCompositionAndReturningAutoResumesContext()
    {
        var policy = new SmartPresentationPolicy();
        var transition = new GameContextTransition(GameContext.Supercruise, GameContextSignalOrigin.Journal);

        var manual = policy.Decide(transition, PresentationComposition.GalaxyMap);
        Assert.Equal(PresentationComposition.GalaxyMap, manual.Composition);
        Assert.Equal(PresentationSelectionMode.Manual, manual.SelectionMode);
        Assert.Equal("ManualPin", manual.ReasonCode);

        var automatic = policy.Decide(transition);
        Assert.Equal(PresentationComposition.Flight, automatic.Composition);
        Assert.Equal(PresentationSelectionMode.Auto, automatic.SelectionMode);
    }

    [Fact]
    public void SnapshotOnlyChangeRequiresTwoIdenticalConsecutiveSignals()
    {
        var policy = new SmartPresentationPolicy();
        var flight = policy.Decide(new(GameContext.Flight, GameContextSignalOrigin.Journal));
        Assert.Equal(PresentationComposition.Flight, flight.Composition);

        var first = policy.Decide(new(GameContext.GalaxyMap, GameContextSignalOrigin.StatusSnapshot));
        var second = policy.Decide(new(GameContext.GalaxyMap, GameContextSignalOrigin.StatusSnapshot));

        Assert.Equal(PresentationComposition.Flight, first.Composition);
        Assert.Equal(PresentationComposition.GalaxyMap, second.Composition);
    }

    [Fact]
    public void JournalAndTrustedSignalsBypassSnapshotStabilization()
    {
        var policy = new SmartPresentationPolicy();
        _ = policy.Decide(new(GameContext.Flight, GameContextSignalOrigin.Journal));
        _ = policy.Decide(new(GameContext.GalaxyMap, GameContextSignalOrigin.StatusSnapshot));

        var jump = policy.Decide(new(GameContext.FsdJump, GameContextSignalOrigin.Journal));
        var post = policy.Decide(new(GameContext.PostJump, GameContextSignalOrigin.Trusted));

        Assert.Equal(PresentationComposition.JumpTransit, jump.Composition);
        Assert.Equal(PresentationComposition.PostJump, post.Composition);
    }

    [Fact]
    public void IdenticalSequencesProduceIdenticalIntentSequences()
    {
        static PresentationIntent[] Replay()
        {
            var policy = new SmartPresentationPolicy();
            return
            [
                policy.Decide(new(GameContext.Flight, GameContextSignalOrigin.Journal)),
                policy.Decide(new(GameContext.GalaxyMap, GameContextSignalOrigin.StatusSnapshot)),
                policy.Decide(new(GameContext.GalaxyMap, GameContextSignalOrigin.StatusSnapshot)),
                policy.Decide(new(GameContext.Unknown, GameContextSignalOrigin.StatusSnapshot)),
                policy.Decide(new(GameContext.Unknown, GameContextSignalOrigin.StatusSnapshot)),
            ];
        }

        Assert.Equal(Replay(), Replay());
    }
}
