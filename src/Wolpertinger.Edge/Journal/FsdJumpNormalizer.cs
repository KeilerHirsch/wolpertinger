using System.Text;
using System.Text.Json;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Evidence;

namespace Wolpertinger.Edge.Journal;

public static class FsdJumpNormalizer
{
    public static ObservationEnvelopeDraft Normalize(
        RawEvidenceReceipt receipt,
        JsonElement root,
        SessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        ArgumentNullException.ThrowIfNull(binding);
        if (!receipt.IsDurable)
        {
            throw new InvalidOperationException("FSDJump normalization requires durable raw evidence.");
        }
        var provenance = JournalEvidenceProvenance.Map(receipt.SourceKind);

        if (JournalEventClassifier.Classify(root) != JournalEventKind.FsdJump)
        {
            throw new InvalidDataException("Journal event is not FSDJump.");
        }

        var starSystem = RequireString(root, "StarSystem");
        var starSystemBytes = Encoding.UTF8.GetByteCount(starSystem);
        if (starSystemBytes is < 1 or > 128)
        {
            throw new InvalidDataException("StarSystem UTF-8 length must be in 1..128.");
        }
        var systemAddress = RequireUnsigned64(root, "SystemAddress");
        var positionElement = RequireProperty(root, "StarPos");
        if (positionElement.ValueKind != JsonValueKind.Array || positionElement.GetArrayLength() != 3)
        {
            throw new InvalidDataException("StarPos must contain exactly three numeric elements.");
        }

        var positionValues = positionElement.EnumerateArray().ToArray();
        var position = new GalacticPosition(
            ParseNumber(positionValues[0], "StarPos[0]"),
            ParseNumber(positionValues[1], "StarPos[1]"),
            ParseNumber(positionValues[2], "StarPos[2]"));

        var payload = new FsdJumpPayload(
            starSystem,
            systemAddress,
            position,
            ParseNumber(RequireProperty(root, "JumpDist"), "JumpDist"),
            ParseNumber(RequireProperty(root, "FuelUsed"), "FuelUsed"),
            ParseNumber(RequireProperty(root, "FuelLevel"), "FuelLevel"));

        return new ObservationEnvelopeDraft(
            receipt.EvidenceDigest,
            binding.SessionId,
            binding.Profile,
            ObservationKind.FsdJump,
            SessionIdentityTracker.ParseOptionalTimestamp(root),
            receipt.ObservedUtc.ToUnixTimeMilliseconds(),
            receipt.CommitUtc.ToUnixTimeMilliseconds(),
            MessageCount: 1,
            payload,
            provenance,
            JournalEvidenceProvenance.ProtocolVersion(receipt.SourceKind));
    }

    private static Decimal64 ParseNumber(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Number)
        {
            throw new InvalidDataException($"{name} must be a JSON number.");
        }

        return Decimal64.Parse(element.GetRawText());
    }

    private static string RequireString(JsonElement root, string propertyName)
    {
        var value = RequireProperty(root, propertyName);
        if (value.ValueKind != JsonValueKind.String || value.GetString() is not { } text)
        {
            throw new InvalidDataException($"{propertyName} must be a string.");
        }

        return text;
    }

    private static ulong RequireUnsigned64(JsonElement root, string propertyName)
    {
        var value = RequireProperty(root, propertyName);
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetUInt64(out var result))
        {
            throw new InvalidDataException($"{propertyName} must be an unsigned 64-bit integer.");
        }

        return result;
    }

    private static JsonElement RequireProperty(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidDataException($"Missing required FSDJump field: {propertyName}.");
        }

        return value;
    }
}
