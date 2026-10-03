using Wolpertinger.Edge.Runtime;

namespace Wolpertinger.Edge.Tests.Runtime;

public sealed class ProductRuntimePathsTests
{
    [Fact]
    public void LiveAndSampleWritableRootsArePhysicallyIsolated()
    {
        var root = Path.Combine(Path.GetTempPath(), "wolpertinger-runtime-paths", Guid.NewGuid().ToString("N"));

        var live = ProductRuntimePaths.ForLive(root);
        var sample = ProductRuntimePaths.ForSample(root);

        Assert.Equal(ProductRuntimeMode.Live, live.Mode);
        Assert.Equal(ProductRuntimeMode.Sample, sample.Mode);
        Assert.True(Path.IsPathFullyQualified(live.RootDirectory));
        Assert.True(Path.IsPathFullyQualified(sample.RootDirectory));
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "runtime", "live"), live.RootDirectory);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "runtime", "sample"), sample.RootDirectory);

        var liveWritable = WritablePaths(live);
        var sampleWritable = WritablePaths(sample);
        Assert.DoesNotContain(liveWritable, path => sampleWritable.Contains(path, StringComparer.OrdinalIgnoreCase));
        Assert.All(liveWritable, path => Assert.StartsWith(live.RootDirectory, path, StringComparison.OrdinalIgnoreCase));
        Assert.All(sampleWritable, path => Assert.StartsWith(sample.RootDirectory, path, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FrontierTokenStorageIsLiveOnlyAndOutsideRuntimeModeRoots()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wolpertinger-token-layout"));

        var live = ProductRuntimePaths.ForLive(root);
        var sample = ProductRuntimePaths.ForSample(root);

        Assert.Equal(Path.Combine(root, "secrets", "frontier-token.bin"), live.FrontierTokenPath);
        Assert.DoesNotContain(live.RootDirectory, live.FrontierTokenPath!, StringComparison.OrdinalIgnoreCase);
        Assert.Null(sample.FrontierTokenPath);
    }

    [Fact]
    public void LayoutMatchesFrozenProductCategories()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "wolpertinger-layout"));
        var live = ProductRuntimePaths.ForLive(root);

        Assert.Equal(Path.Combine(root, "runtime", "live", "evidence"), live.EvidenceDirectory);
        Assert.Equal(Path.Combine(root, "runtime", "live", "normalized", "observations.bin"), live.NormalizedLedgerPath);
        Assert.Equal(Path.Combine(root, "runtime", "live", "control", "authority-epochs.bin"), live.AuthorityEpochPath);
        Assert.Equal(Path.Combine(root, "runtime", "live", "projections.db"), live.ProjectionDatabasePath);
        Assert.Equal(Path.Combine(root, "runtime", "live", "secrets", "evidence-key.bin"), live.EvidenceKeyPath);
        Assert.Equal(Path.Combine(root, "runtime", "live", "logs"), live.LogsDirectory);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyRootIsRejected(string root)
    {
        Assert.Throws<ArgumentException>(() => ProductRuntimePaths.ForLive(root));
        Assert.Throws<ArgumentException>(() => ProductRuntimePaths.ForSample(root));
    }

    [Fact]
    public void UndefinedModeIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ProductRuntimePaths.Create(Path.GetTempPath(), (ProductRuntimeMode)255));
    }

    private static string[] WritablePaths(ProductRuntimePaths paths)
        =>
        [
            paths.EvidenceDirectory,
            paths.NormalizedLedgerPath,
            paths.AuthorityEpochPath,
            paths.ProjectionDatabasePath,
            paths.EvidenceKeyPath,
            paths.LogsDirectory,
        ];
}
