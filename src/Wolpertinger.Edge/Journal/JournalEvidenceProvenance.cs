using Wolpertinger.Edge.Contracts;
using Wolpertinger.Edge.Evidence;

namespace Wolpertinger.Edge.Journal;

internal static class JournalEvidenceProvenance
{
    internal static SourceProvenance Map(RawEvidenceSourceKind sourceKind)
        => sourceKind switch
        {
            RawEvidenceSourceKind.LocalJournal => SourceProvenance.LocalJournal,
            RawEvidenceSourceKind.Sample => SourceProvenance.Sample,
            _ => throw new InvalidDataException(
                "Journal normalization requires LocalJournal or explicit Sample evidence."),
        };

    internal static int ProtocolVersion(RawEvidenceSourceKind sourceKind)
        => sourceKind switch
        {
            RawEvidenceSourceKind.LocalJournal => KernelProtocol.Stage1Version,
            RawEvidenceSourceKind.Sample => KernelProtocol.R0Version,
            _ => throw new InvalidDataException(
                "Journal normalization requires LocalJournal or explicit Sample evidence."),
        };
}
