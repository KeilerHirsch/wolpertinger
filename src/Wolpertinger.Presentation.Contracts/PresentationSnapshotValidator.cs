namespace Wolpertinger.Presentation.Contracts;

public static class PresentationSnapshotValidator
{
    public static void Validate(PresentationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.ProtocolVersion != PresentationProtocol.Version)
            throw new InvalidDataException("Unsupported presentation protocol version.");
        RequireEnum(snapshot.Context, nameof(snapshot.Context));
        ValidateIntent(snapshot.Intent);
        ValidateRuntimeHealth(snapshot.RuntimeHealth);
        ValidateFrontierAccount(snapshot.FrontierAccount);

        if (snapshot.Jump is not null)
            ValidateJump(snapshot.Revision, snapshot.Jump);
        if (snapshot.CommanderVessel is not null)
            ValidateCommanderVessel(snapshot.Revision, snapshot.CommanderVessel);
    }

    private static void ValidateIntent(PresentationIntent? intent)
    {
        if (intent is null) throw new InvalidDataException("Presentation intent is required.");
        RequireEnum(intent.Composition, nameof(intent.Composition));
        RequireEnum(intent.SelectionMode, nameof(intent.SelectionMode));
        RequireText(intent.ReasonCode, nameof(intent.ReasonCode));
        if (intent.SelectionMode == PresentationSelectionMode.Manual && intent.ReasonCode != "ManualPin")
            throw new InvalidDataException("Manual presentation intent requires reason ManualPin.");
        if (intent.SelectionMode == PresentationSelectionMode.Auto && intent.ReasonCode == "ManualPin")
            throw new InvalidDataException("Automatic presentation intent cannot use reason ManualPin.");
    }

    private static void ValidateRuntimeHealth(RuntimeHealthPresentation? health)
    {
        if (health is null) throw new InvalidDataException("Runtime health is required.");
        RequireEnum(health.Health, nameof(health.Health));
        RequireText(health.ReasonCode, nameof(health.ReasonCode));
    }

    private static void ValidateFrontierAccount(FrontierAccountPresentation? account)
    {
        if (account is null) throw new InvalidDataException("Frontier account state is required.");
        RequireEnum(account.State, nameof(account.State));
        RequireEnum(account.Freshness, nameof(account.Freshness));
        RequireText(account.ReasonCode, nameof(account.ReasonCode));
        if (account.LastSuccessUnixMs is < 0)
            throw new InvalidDataException("Frontier account last-success time must be nonnegative.");
    }

    private static void ValidateJump(ulong revision, JumpPresentation jump)
    {
        if (revision == 0)
            throw new InvalidDataException("A jump snapshot requires a nonzero revision.");
        if (jump.Profile is null)
            throw new InvalidDataException("A jump requires a profile.");
        ValidateProfile(jump.Profile);
        RequireText(jump.StarSystem, nameof(jump.StarSystem));
        RequireText(jump.ReasonCode, nameof(jump.ReasonCode));
        RequireDigest(jump.EvidenceDigestHex, nameof(jump.EvidenceDigestHex));
        RequireDigest(jump.StateDigestHex, nameof(jump.StateDigestHex));
        if (jump.EvidenceReference.ByteOffset < 0 || jump.EvidenceReference.FrameLength < 0)
            throw new InvalidDataException("Evidence offset and frame length must be nonnegative.");
        RequireText(jump.PositionX, nameof(jump.PositionX));
        RequireText(jump.PositionY, nameof(jump.PositionY));
        RequireText(jump.PositionZ, nameof(jump.PositionZ));
        RequireText(jump.JumpDistance, nameof(jump.JumpDistance));
        RequireText(jump.FuelUsed, nameof(jump.FuelUsed));
        RequireText(jump.FuelLevel, nameof(jump.FuelLevel));
        RequireEnum(jump.LocationProvenance, nameof(jump.LocationProvenance));
        RequireEnum(jump.FuelProvenance, nameof(jump.FuelProvenance));
        RequireEnum(jump.LocationFreshness, nameof(jump.LocationFreshness));
        RequireEnum(jump.FuelFreshness, nameof(jump.FuelFreshness));
    }

    private static void ValidateCommanderVessel(
        ulong revision,
        CommanderVesselPresentation commander)
    {
        if (revision == 0)
            throw new InvalidDataException("A commander/vessel snapshot requires a nonzero revision.");
        if (commander.Profile is null)
            throw new InvalidDataException("Commander/vessel presentation requires a profile.");
        ValidateProfile(commander.Profile);
        RequireDigest(commander.StateDigestHex, nameof(commander.StateDigestHex));
        RequireText(commander.CommanderName, nameof(commander.CommanderName));
        RequireText(commander.ShipModel, nameof(commander.ShipModel));
        RequireOptionalText(commander.ShipName, nameof(commander.ShipName));
        RequireOptionalText(commander.SystemName, nameof(commander.SystemName));
        RequireOptionalText(commander.StationName, nameof(commander.StationName));
        RequireEnum(commander.Docked, nameof(commander.Docked));
        RequireEnum(commander.Alive, nameof(commander.Alive));
        RequireEnum(commander.Provenance, nameof(commander.Provenance));
        RequireEnum(commander.Freshness, nameof(commander.Freshness));
        if (commander.ObservedUnixMs < 0)
            throw new InvalidDataException("Commander/vessel observation time must be nonnegative.");
    }

    private static void ValidateProfile(PresentationProfile profile)
    {
        RequireText(profile.Fid, nameof(PresentationProfile.Fid));
        RequireEnum(profile.Realm, nameof(PresentationProfile.Realm));
    }

    private static void RequireEnum<T>(T value, string field) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
            throw new InvalidDataException($"{field} contains an undefined enum value.");
    }

    private static void RequireOptionalText(string? value, string field)
    {
        if (value is not null && string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{field} must be null or nonblank.");
    }

    private static void RequireText(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{field} must not be blank.");
    }

    private static void RequireDigest(string? value, string field)
    {
        if (value is null || value.Length != 64 || !value.All(char.IsAsciiHexDigit))
            throw new InvalidDataException($"{field} must contain exactly 64 hexadecimal characters.");
    }
}
