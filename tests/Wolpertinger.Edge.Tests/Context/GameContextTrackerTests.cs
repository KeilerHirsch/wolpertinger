using System.Text;
using Wolpertinger.Edge.Context;

namespace Wolpertinger.Edge.Tests.Context;

public sealed class GameContextTrackerTests
{
    [Fact]
    public void R0EnumValuesAreFrozen()
    {
        Assert.Equal(0, (byte)GameContext.InactiveOrNoGame);
        Assert.Equal(1, (byte)GameContext.MainMenu);
        Assert.Equal(2, (byte)GameContext.Docked);
        Assert.Equal(3, (byte)GameContext.StationServices);
        Assert.Equal(4, (byte)GameContext.Flight);
        Assert.Equal(5, (byte)GameContext.Supercruise);
        Assert.Equal(6, (byte)GameContext.GalaxyMap);
        Assert.Equal(7, (byte)GameContext.SystemMap);
        Assert.Equal(8, (byte)GameContext.JumpPreparation);
        Assert.Equal(9, (byte)GameContext.FsdJump);
        Assert.Equal(10, (byte)GameContext.PostJump);
        Assert.Equal(11, (byte)GameContext.Unknown);
        Assert.Equal(3, (byte)GameContextSignalOrigin.Trusted);
    }

    [Fact]
    public void JournalAndTrustedSignalsFollowFrozenSequence()
    {
        var tracker = new GameContextTracker();
        AssertTransition(tracker.ApplyJournal(Json("{\"event\":\"Fileheader\"}")), GameContext.MainMenu, GameContextSignalOrigin.Journal);
        AssertTransition(tracker.ApplyJournal(Json("{\"event\":\"Docked\"}")), GameContext.Docked, GameContextSignalOrigin.Journal);
        AssertTransition(tracker.ApplyJournal(Json("{\"event\":\"Undocked\"}")), GameContext.Flight, GameContextSignalOrigin.Journal);
        AssertTransition(tracker.ApplyJournal(Json("{\"event\":\"FSDTarget\"}")), GameContext.JumpPreparation, GameContextSignalOrigin.Journal);
        AssertTransition(tracker.ApplyJournal(Json("{\"event\":\"StartJump\",\"JumpType\":\"Hyperspace\"}")), GameContext.FsdJump, GameContextSignalOrigin.Journal);
        AssertTransition(tracker.ApplyTrustedFsdJump(), GameContext.PostJump, GameContextSignalOrigin.Trusted);
        AssertTransition(tracker.ApplyStatus(Json("{\"Flags\":16,\"GuiFocus\":0}")), GameContext.Supercruise, GameContextSignalOrigin.StatusSnapshot);
        AssertTransition(tracker.ApplyJournal(Json("{\"event\":\"Shutdown\"}")), GameContext.InactiveOrNoGame, GameContextSignalOrigin.Journal);
    }

    [Fact]
    public void UiFocusTemporarilyOverridesBaseContext()
    {
        var tracker = new GameContextTracker();
        _ = tracker.ApplyJournal(Json("{\"event\":\"Undocked\"}"));
        AssertTransition(tracker.ApplyStatus(Json("{\"Flags\":0,\"GuiFocus\":6}")), GameContext.GalaxyMap, GameContextSignalOrigin.StatusSnapshot);
        AssertTransition(tracker.ApplyStatus(Json("{\"Flags\":0,\"GuiFocus\":7}")), GameContext.SystemMap, GameContextSignalOrigin.StatusSnapshot);
        AssertTransition(tracker.ApplyStatus(Json("{\"Flags\":0,\"GuiFocus\":5}")), GameContext.StationServices, GameContextSignalOrigin.StatusSnapshot);
        AssertTransition(tracker.ApplyStatus(Json("{\"Flags\":0,\"GuiFocus\":0}")), GameContext.Flight, GameContextSignalOrigin.StatusSnapshot);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Flags\":\"bad\",\"GuiFocus\":0}")]
    [InlineData("{\"Flags\":17,\"GuiFocus\":0}")]
    public void MalformedOrContradictoryStatusFailsToUnknown(string json)
    {
        var tracker = new GameContextTracker();
        _ = tracker.ApplyJournal(Json("{\"event\":\"Undocked\"}"));
        AssertTransition(tracker.ApplyStatus(Json(json)), GameContext.Unknown, GameContextSignalOrigin.StatusSnapshot);
    }

    [Fact]
    public void ReplayOfSameSignalsIsDeterministic()
    {
        static GameContext[] Replay()
        {
            var tracker = new GameContextTracker();
            return new[]
            {
                tracker.ApplyJournal(Json("{\"event\":\"Fileheader\"}")).Context,
                tracker.ApplyJournal(Json("{\"event\":\"Undocked\"}")).Context,
                tracker.ApplyJournal(Json("{\"event\":\"FSDTarget\"}")).Context,
                tracker.ApplyJournal(Json("{\"event\":\"StartJump\",\"JumpType\":\"Hyperspace\"}")).Context,
                tracker.ApplyTrustedFsdJump().Context,
                tracker.ApplyStatus(Json("{\"Flags\":16,\"GuiFocus\":0}")).Context,
            };
        }

        Assert.Equal(Replay(), Replay());
    }

    private static ReadOnlyMemory<byte> Json(string json) => Encoding.UTF8.GetBytes(json);

    private static void AssertTransition(
        GameContextTransition transition,
        GameContext expectedContext,
        GameContextSignalOrigin expectedOrigin)
    {
        Assert.Equal(expectedContext, transition.Context);
        Assert.Equal(expectedOrigin, transition.Origin);
    }
}
