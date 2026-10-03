using System.Text;

namespace Wolpertinger.Product.Contracts;

public static class ProductActivationValidator
{
    public const int MaximumPayloadUtf8Bytes = 4_096;

    private static readonly HashSet<string> ManualCompositions =
        new(StringComparer.Ordinal)
        {
            "Quiet",
            "Flight",
            "JumpPreparation",
            "JumpTransit",
            "PostJump",
            "GalaxyMap",
            "SystemMap",
            "Station",
            "Unknown",
        };

    public static void Validate(ProductActivation activation)
    {
        ArgumentNullException.ThrowIfNull(activation);
        if (!Enum.IsDefined(activation.Kind))
            throw new InvalidDataException("Activation kind is undefined.");

        var allowsPayload = activation.Kind is
            ProductActivationKind.ProtocolCallback or
            ProductActivationKind.SetManualComposition or
            ProductActivationKind.SetEliteDataPath;

        if (!allowsPayload && activation.Payload is not null)
            throw new InvalidDataException("Activation kind does not accept a payload.");

        if (!allowsPayload)
            return;

        if (string.IsNullOrWhiteSpace(activation.Payload))
            throw new InvalidDataException("Activation payload is required.");
        if (activation.Payload.Contains('\0'))
            throw new InvalidDataException("Activation payload contains NUL.");
        if (Encoding.UTF8.GetByteCount(activation.Payload) > MaximumPayloadUtf8Bytes)
            throw new InvalidDataException("Activation payload exceeds the bounded contract.");

        switch (activation.Kind)
        {
            case ProductActivationKind.ProtocolCallback:
                if (!Uri.TryCreate(activation.Payload, UriKind.Absolute, out var callback)
                    || !string.Equals(callback.Scheme, "wolpertinger", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(callback.Host, "frontier", StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(callback.AbsolutePath, "/oauth", StringComparison.Ordinal)
                    || callback.Fragment.Length != 0)
                {
                    throw new InvalidDataException(
                        "Protocol callback does not match the registered WOLPERTINGER callback.");
                }
                break;

            case ProductActivationKind.SetManualComposition:
                if (!ManualCompositions.Contains(activation.Payload))
                    throw new InvalidDataException("Manual composition value is not allowed.");
                break;

            case ProductActivationKind.SetEliteDataPath:
                if (!Path.IsPathFullyQualified(activation.Payload)
                    || !Directory.Exists(activation.Payload))
                {
                    throw new InvalidDataException(
                        "Elite data path must be one existing absolute directory.");
                }
                break;
        }
    }
}
