using Wolpertinger.Edge.Contracts;

namespace Wolpertinger.Edge.Evidence;

public enum RawEvidenceSourceKind : ushort
{
    LocalJournal = 1,
    LocalStatus = 2,
    FrontierApi = 3,
    Community = 4,
    UserEntered = 5,
    Sample = 6,
}

public readonly record struct RawEvidenceSourceLocator(
    FixedBytes16 SourceId,
    ulong SourceOffset,
    uint SourceLength);

public sealed record RawEvidenceInput(
    RawEvidenceSourceKind SourceKind,
    ReadOnlyMemory<byte> Payload,
    DateTimeOffset ObservedUtc,
    RawEvidenceSourceLocator? SourceLocator = null);

public sealed record RawEvidenceReceipt(
    EvidenceReference Reference,
    RawEvidenceSourceKind SourceKind,
    FixedBytes32 EvidenceDigest, DateTimeOffset ObservedUtc,
    DateTimeOffset CommitUtc,
    bool IsDurable);

public sealed record RecoveredRawEvidence(
    EvidenceReference Reference,
    RawEvidenceSourceKind SourceKind,
    byte[] Payload,
    DateTimeOffset ObservedUtc,
    FixedBytes32 PreviousDigest,
    FixedBytes32 EvidenceDigest,
    RawEvidenceSourceLocator? SourceLocator = null);

public sealed class EvidenceCorruptionException : IOException
{
    public EvidenceCorruptionException(string message) : base(message) { }
}
