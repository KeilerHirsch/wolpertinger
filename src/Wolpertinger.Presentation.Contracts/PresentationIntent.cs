namespace Wolpertinger.Presentation.Contracts;

public enum PresentationGameContext : byte
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

public enum PresentationComposition : byte
{
    Quiet = 0,
    Flight = 1,
    JumpPreparation = 2,
    JumpTransit = 3,
    PostJump = 4,
    GalaxyMap = 5,
    SystemMap = 6,
    Station = 7,
    Unknown = 8,
}

public enum PresentationSelectionMode : byte
{
    Auto = 0,
    Manual = 1,
}

public enum PresentationTriState : byte
{
    Unknown = 0,
    False = 1,
    True = 2,
}

public enum ProductRuntimeHealth : byte
{
    Starting = 0,
    Ready = 1,
    Degraded = 2,
    Faulted = 3,
    Stopped = 4,
}

public enum FrontierAccountState : byte
{
    Disconnected = 0,
    Connecting = 1,
    Connected = 2,
    ReauthenticationRequired = 3,
    Unavailable = 4,
}

public sealed record PresentationIntent(
    PresentationComposition Composition,
    bool OverlayEmphasized,
    string ReasonCode,
    PresentationSelectionMode SelectionMode);

public sealed record RuntimeHealthPresentation(
    ProductRuntimeHealth Health,
    string ReasonCode);

public sealed record FrontierAccountPresentation(
    FrontierAccountState State,
    PresentationFreshness Freshness,
    long? LastSuccessUnixMs,
    string ReasonCode);
