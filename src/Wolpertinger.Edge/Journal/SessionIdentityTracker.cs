using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Evidence;

namespace Wolpertinger.Edge.Journal;

public enum SessionIdentityStatus
{
    IdentityPending = 0,
    SessionBound,
    IdentityConflict,
}

public sealed record SessionBinding(FixedBytes16 SessionId, ProfileKey Profile);

public sealed record ObservationEnvelopeDraft(
    FixedBytes32 EvidenceDigest,
    FixedBytes16 SessionId,
    ProfileKey Profile,
    ObservationKind Kind,
    long? SourceTimeUnixMs,
    long ObservedUnixMs,
    long CommitUnixMs,
    ushort MessageCount,
    ObservationPayload Payload,
    SourceProvenance Provenance,
    int ProtocolVersion = KernelProtocol.Stage1Version)
{
    public ObservationEnvelope ToEnvelope(ulong evidenceSequence, uint messageOrdinal = 0)
        => new(
            new ObservationCursor(evidenceSequence, messageOrdinal),
            EvidenceDigest,
            SessionId,
            Profile,
            Kind,
            SourceTimeUnixMs,
            ObservedUnixMs,
            CommitUnixMs,
            MessageCount,
            Payload,
            Provenance,
            ProtocolVersion);
}

public sealed record SessionIdentityResult(
    SessionIdentityStatus Status,
    GalaxyRealm Realm,
    FixedBytes16? SessionId,
    ProfileKey? Profile,
    ObservationEnvelopeDraft? Draft);

public sealed class SessionIdentityTracker
{
    private FixedBytes16? _pendingSessionId;
    private GalaxyRealm _pendingRealm = GalaxyRealm.Unknown;
    private ulong _nextSaveEpoch;
    private int? _expectedContinuationPart;

    public SessionBinding? CurrentBinding { get; private set; }

    public void Restore(SessionBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);
        CurrentBinding = binding;
        _pendingSessionId = binding.SessionId;
        _pendingRealm = binding.Profile.Realm;
        _nextSaveEpoch = NextSaveEpoch(binding.Profile.SaveEpoch);
        _expectedContinuationPart = null;
    }
    public SessionIdentityResult Observe(RawEvidenceReceipt receipt, JsonElement root)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        if (!receipt.IsDurable)
        {
            throw new InvalidOperationException("Journal normalization requires durable raw evidence.");
        }
        _ = JournalEvidenceProvenance.Map(receipt.SourceKind);

        return JournalEventClassifier.Classify(root) switch
        {
            JournalEventKind.FileHeader => ObserveFileHeader(receipt, root),
            JournalEventKind.Commander or JournalEventKind.LoadGame => ObserveIdentityEvent(receipt, root),
            JournalEventKind.Continued => ObserveContinued(root),
            _ => Snapshot(),
        };
    }

    private SessionIdentityResult ObserveFileHeader(RawEvidenceReceipt receipt, JsonElement root)
    {
        var version = GetOptionalString(root, "gameversion");
        var build = GetOptionalString(root, "build");
        var realm = GalaxyRealmResolver.Resolve(version, build);

        if (CurrentBinding is not null
            && _expectedContinuationPart is int expectedPart
            && TryGetPositivePart(root, "part", out var actualPart)
            && actualPart == expectedPart
            && realm == CurrentBinding.Profile.Realm)
        {
            _expectedContinuationPart = null;
            return Snapshot();
        }

        _expectedContinuationPart = null;
        _pendingRealm = realm;
        _pendingSessionId = DeriveSessionId(receipt.EvidenceDigest);
        CurrentBinding = null;

        return new SessionIdentityResult(
            SessionIdentityStatus.IdentityPending,
            _pendingRealm,
            _pendingSessionId,
            null,
            null);
    }

    private SessionIdentityResult ObserveContinued(JsonElement root)
    {
        _expectedContinuationPart = CurrentBinding is not null
            && TryGetPositivePart(root, "Part", out var part)
                ? part
                : null;
        return Snapshot();
    }
    private SessionIdentityResult ObserveIdentityEvent(RawEvidenceReceipt receipt, JsonElement root)
    {
        var fid = GetOptionalString(root, "FID");
        if (string.IsNullOrWhiteSpace(fid) || Encoding.UTF8.GetByteCount(fid) > 64)
        {
            return ConflictOrPending();
        }

        if (CurrentBinding is not null)
        {
            return string.Equals(CurrentBinding.Profile.Fid, fid, StringComparison.Ordinal)
                ? Snapshot()
                : new SessionIdentityResult(
                    SessionIdentityStatus.IdentityConflict,
                    CurrentBinding.Profile.Realm,
                    CurrentBinding.SessionId,
                    CurrentBinding.Profile,
                    null);
        }

        if (_pendingSessionId is null || _pendingRealm == GalaxyRealm.Unknown)
        {
            return Snapshot();
        }

        var profile = new ProfileKey(fid, _pendingRealm, _nextSaveEpoch);
        CurrentBinding = new SessionBinding(_pendingSessionId.Value, profile);
        _nextSaveEpoch = NextSaveEpoch(profile.SaveEpoch);
        var draft = CreateSessionBoundDraft(receipt, root, CurrentBinding);
        return new SessionIdentityResult(
            SessionIdentityStatus.SessionBound,
            profile.Realm,
            CurrentBinding.SessionId,
            profile,
            draft);
    }

    private SessionIdentityResult Snapshot()
    {
        if (CurrentBinding is not null)
        {
            return new SessionIdentityResult(
                SessionIdentityStatus.SessionBound,
                CurrentBinding.Profile.Realm,
                CurrentBinding.SessionId,
                CurrentBinding.Profile,
                null);
        }

        return new SessionIdentityResult(
            SessionIdentityStatus.IdentityPending,
            _pendingRealm,
            _pendingSessionId,
            null,
            null);
    }

    private SessionIdentityResult ConflictOrPending()
        => CurrentBinding is null
            ? Snapshot()
            : new SessionIdentityResult(
                SessionIdentityStatus.IdentityConflict,
                CurrentBinding.Profile.Realm,
                CurrentBinding.SessionId,
                CurrentBinding.Profile,
                null);

    private static ObservationEnvelopeDraft CreateSessionBoundDraft(
        RawEvidenceReceipt receipt,
        JsonElement root,
        SessionBinding binding)
        => new(
            receipt.EvidenceDigest,
            binding.SessionId,
            binding.Profile,
            ObservationKind.SessionBound,
            ParseOptionalTimestamp(root),
            receipt.ObservedUtc.ToUnixTimeMilliseconds(),
            receipt.CommitUtc.ToUnixTimeMilliseconds(),
            MessageCount: 1,
            SessionBoundPayload.Instance,
            JournalEvidenceProvenance.Map(receipt.SourceKind),
            JournalEvidenceProvenance.ProtocolVersion(receipt.SourceKind));

    private static ulong NextSaveEpoch(ulong current)
    {
        if (current == ulong.MaxValue)
            throw new InvalidDataException("Session save epoch is exhausted.");
        return current + 1;
    }

    private static FixedBytes16 DeriveSessionId(FixedBytes32 evidenceDigest)
    {
        var prefix = Encoding.UTF8.GetBytes("session-v1");
        var material = new byte[prefix.Length + 32];
        prefix.CopyTo(material, 0);
        evidenceDigest.ToArray().CopyTo(material, prefix.Length);
        var digest = SHA256.HashData(material);
        return FixedBytes16.FromBytes(digest.AsSpan(0, 16));
    }

    internal static long? ParseOptionalTimestamp(JsonElement root)
    {
        var text = GetOptionalString(root, "timestamp");
        if (text is null)
        {
            return null;
        }

        if (!DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var timestamp))
        {
            throw new InvalidDataException("Journal timestamp is not a valid UTC timestamp.");
        }

        return timestamp.ToUnixTimeMilliseconds();
    }

    private static bool TryGetPositivePart(JsonElement root, string propertyName, out int part)
    {
        part = 0;
        return root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out part)
            && part > 0;
    }

    private static string? GetOptionalString(JsonElement root, string propertyName)
        => root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
