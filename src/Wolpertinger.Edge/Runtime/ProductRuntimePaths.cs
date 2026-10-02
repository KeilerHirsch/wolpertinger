namespace Wolpertinger.Edge.Runtime;

public sealed record ProductRuntimePaths(
    ProductRuntimeMode Mode,
    string RootDirectory,
    string EvidenceDirectory,
    string NormalizedLedgerPath,
    string AuthorityEpochPath,
    string ProjectionDatabasePath,
    string EvidenceKeyPath,
    string? FrontierTokenPath,
    string LogsDirectory)
{
    public static ProductRuntimePaths ForLive(string root)
        => Create(root, ProductRuntimeMode.Live);

    public static ProductRuntimePaths ForSample(string root)
        => Create(root, ProductRuntimeMode.Sample);

    public static ProductRuntimePaths Create(string root, ProductRuntimeMode mode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));

        var productRoot = Path.GetFullPath(root);
        var runtimeRoot = Path.Combine(
            productRoot,
            "runtime",
            mode == ProductRuntimeMode.Live ? "live" : "sample");

        return new ProductRuntimePaths(
            mode,
            runtimeRoot,
            Path.Combine(runtimeRoot, "evidence"),
            Path.Combine(runtimeRoot, "normalized", "observations.bin"),
            Path.Combine(runtimeRoot, "control", "authority-epochs.bin"),
            Path.Combine(runtimeRoot, "projections.db"),
            Path.Combine(runtimeRoot, "secrets", "evidence-key.bin"),
            mode == ProductRuntimeMode.Live
                ? Path.Combine(productRoot, "secrets", "frontier-token.bin")
                : null,
            Path.Combine(runtimeRoot, "logs"));
    }
}
