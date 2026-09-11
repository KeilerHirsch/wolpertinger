using Wolpertinger.Edge.Runtime;

namespace Wolpertinger.Edge.Tests.Runtime;

public sealed class ProductRuntimePathsTests
{
    [Fact]
    public void LiveAndSampleRootsAreAbsoluteAndFullyIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), "wolpertinger-runtime-paths", Guid.NewGuid().ToString("N"));

        var live = ProductRuntimePaths.ForLive(root);
        var sample = ProductRuntimePaths.ForSample(root);

        Assert.True(Path.IsPathFullyQualified(live.RootDirectory));
        Assert.True(Path.IsPathFullyQualified(sample.RootDirectory));
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "live"), live.RootDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "sample"), sample.RootDirectory);

        Assert.NotEqual(live.EvidenceDirectory, sample.EvidenceDirectory);
        Assert.NotEqual(live.NormalizedLedgerPath, sample.NormalizedLedgerPath);
        Assert.NotEqual(live.AuthorityEpochPath, sample.AuthorityEpochPath);
        Assert.NotEqual(live.ProjectionDatabasePath, sample.ProjectionDatabasePath);
        Assert.NotEqual(live.EvidenceKeyPath, sample.EvidenceKeyPath);
        Assert.NotEqual(live.FrontierTokenPath, sample.FrontierTokenPath);
    }
    [Fact]
    public void LayoutMatchesFrozenProductCategories()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wolpertinger-layout"));
        var live = ProductRuntimePaths.ForLive(root);

        Assert.Equal(Path.Combine(root, "live", "evidence"), live.EvidenceDirectory);
        Assert.Equal(Path.Combine(root, "live", "normalized", "observations.bin"), live.NormalizedLedgerPath);
        Assert.Equal(Path.Combine(root, "live", "control", "authority-epochs.bin"), live.AuthorityEpochPath);
        Assert.Equal(Path.Combine(root, "live", "projections.db"), live.ProjectionDatabasePath);
        Assert.Equal(Path.Combine(root, "live", "secrets", "evidence-key.bin"), live.EvidenceKeyPath);
        Assert.Equal(Path.Combine(root, "live", "secrets", "frontier-token.bin"), live.FrontierTokenPath);
        Assert.Equal(Path.Combine(root, "live", "logs"), live.LogsDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyRootIsRejected(string root)
    {
        Assert.Throws<ArgumentException>(() => ProductRuntimePaths.ForLive(root));
        Assert.Throws<ArgumentException>(() => ProductRuntimePaths.ForSample(root));
    }
}
