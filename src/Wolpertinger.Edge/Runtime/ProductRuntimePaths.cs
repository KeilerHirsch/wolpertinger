namespace Wolpertinger.Edge.Runtime;

public sealed record ProductRuntimePaths(
    string RootDirectory,
    string EvidenceDirectory,
    string NormalizedLedgerPath,
    string AuthorityEpochPath,
    string ProjectionDatabasePath,
    string EvidenceKeyPath,
    string FrontierTokenPath,
    string LogsDirectory)
{
    public static ProductRuntimePaths ForLive(string root)
        => Create(root, "live");

    public static ProductRuntimePaths ForSample(string root)
        => Create(root, "sample");

    private static ProductRuntimePaths Create(string root, string mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        var baseRoot = Path.Combine(Path.GetFullPath(root), mode);
        return new ProductRuntimePaths(
            baseRoot,
            Path.Combine(baseRoot, "evidence"),
            Path.Combine(baseRoot, "normalized", "observations.bin"),
            Path.Combine(baseRoot, "control", "authority-epochs.bin"),
            Path.Combine(baseRoot, "projections.db"),
            Path.Combine(baseRoot, "secrets", "evidence-key.bin"),
            Path.Combine(baseRoot, "secrets", "frontier-token.bin"),
            Path.Combine(baseRoot, "logs"));
    }
}
