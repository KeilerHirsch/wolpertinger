namespace Wolpertinger.Edge.Context;

public enum GameContext : byte
{
    InactiveOrNoGame = 0,
    MainMenu = 1,
    Docked = 2,
    StationServices = 3,
    Flight = 4,
    Supercruise = 5,
    GalaxyMap = 6,
    SystemMap = 7,
    JumpPreparation = 8,
    FsdJump = 9,
    PostJump = 10,
    Unknown = 11,
}

public enum GameContextSignalOrigin : byte
{
    Lifecycle = 0,
    Journal = 1,
    StatusSnapshot = 2,
    Trusted = 3,
}

public readonly record struct GameContextTransition(
    GameContext Context,
    GameContextSignalOrigin Origin);
