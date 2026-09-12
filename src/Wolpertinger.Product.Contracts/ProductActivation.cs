namespace Wolpertinger.Product.Contracts;

public enum ProductActivationKind : byte
{
    Open = 1,
    Startup = 2,
    ProtocolCallback = 3,
    ConnectFrontier = 4,
    DisconnectFrontier = 5,
    EnterSample = 6,
    ReturnLive = 7,
    SetManualComposition = 8,
    SetSmartAuto = 9,
    SetEliteDataPath = 10,
    Exit = 11,
}

public sealed record ProductActivation(
    ProductActivationKind Kind,
    string? Payload = null);
