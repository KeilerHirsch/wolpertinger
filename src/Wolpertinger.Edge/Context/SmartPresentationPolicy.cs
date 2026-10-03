using Wolpertinger.Presentation.Contracts;

namespace Wolpertinger.Edge.Context;

public sealed class SmartPresentationPolicy
{
    private GameContext _stableContext = GameContext.InactiveOrNoGame;
    private GameContext? _snapshotCandidate;
    private int _snapshotCandidateCount;

    public PresentationIntent Decide(
        GameContextTransition transition,
        PresentationComposition? manualPin = null)
    {
        var effective = Stabilize(transition);
        if (manualPin is { } pinned)
        {
            if (!Enum.IsDefined(pinned))
                throw new ArgumentOutOfRangeException(nameof(manualPin));
            return new PresentationIntent(
                pinned,
                OverlayEmphasized(effective),
                "ManualPin",
                PresentationSelectionMode.Manual);
        }

        return new PresentationIntent(
            Map(effective),
            OverlayEmphasized(effective),
            $"Context{effective}",
            PresentationSelectionMode.Auto);
    }

    private GameContext Stabilize(GameContextTransition transition)
    {
        if (transition.Origin != GameContextSignalOrigin.StatusSnapshot)
        {
            _stableContext = transition.Context;
            _snapshotCandidate = null;
            _snapshotCandidateCount = 0;
            return _stableContext;
        }
        if (transition.Context == _stableContext)
        {
            _snapshotCandidate = null;
            _snapshotCandidateCount = 0;
            return _stableContext;
        }

        if (_snapshotCandidate == transition.Context)
        {
            _snapshotCandidateCount++;
        }
        else
        {
            _snapshotCandidate = transition.Context;
            _snapshotCandidateCount = 1;
        }

        if (_snapshotCandidateCount >= 2)
        {
            _stableContext = transition.Context;
            _snapshotCandidate = null;
            _snapshotCandidateCount = 0;
        }

        return _stableContext;
    }

    private static PresentationComposition Map(GameContext context) => context switch
    {
        GameContext.InactiveOrNoGame or GameContext.MainMenu => PresentationComposition.Quiet,
        GameContext.Docked or GameContext.StationServices => PresentationComposition.Station,
        GameContext.Flight or GameContext.Supercruise => PresentationComposition.Flight,
        GameContext.GalaxyMap => PresentationComposition.GalaxyMap,
        GameContext.SystemMap => PresentationComposition.SystemMap,
        GameContext.JumpPreparation => PresentationComposition.JumpPreparation,
        GameContext.FsdJump => PresentationComposition.JumpTransit,
        GameContext.PostJump => PresentationComposition.PostJump,
        GameContext.Unknown => PresentationComposition.Unknown,
        _ => throw new InvalidDataException($"Undefined game context: {context}."),
    };

    private static bool OverlayEmphasized(GameContext context)
        => context is GameContext.JumpPreparation or GameContext.FsdJump or GameContext.PostJump;
}
