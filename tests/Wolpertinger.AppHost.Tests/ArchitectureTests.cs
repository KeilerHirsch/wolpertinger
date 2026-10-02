using System.Diagnostics;
using System.Reflection;
using Wolpertinger.AppHost.Lifecycle;
using Wolpertinger.Edge.Kernel;
using Wolpertinger.Edge.Runtime;

namespace Wolpertinger.AppHost.Tests;

public sealed class ArchitectureTests
{
    [Fact]
    public void AppHostDoesNotReferenceEngineeringCli()
    {
        var root = FindRepoRoot();
        var project = File.ReadAllText(Path.Combine(
            root, "src", "Wolpertinger.AppHost", "Wolpertinger.AppHost.csproj"));

        Assert.DoesNotContain("Wolpertinger.Host", project, StringComparison.Ordinal);
        Assert.Contains("Wolpertinger.Edge", project, StringComparison.Ordinal);
        Assert.Contains("Wolpertinger.Product.Contracts", project, StringComparison.Ordinal);
    }

    [Fact]
    public void PresentationStartInfoUsesOnlyBoundedNonSecretPipeArguments()
    {
        var method = typeof(PresentationProcessLauncher).GetMethod(
            "BuildStartInfo",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var info = Assert.IsType<ProcessStartInfo>(method!.Invoke(
            null,
            [@"C:\Program Files\WOLPERTINGER\Wolpertinger.Presentation.App.exe", "wolpertinger.presentation.v2"]));

        Assert.Equal(new[] { "--pipe", "wolpertinger.presentation.v2" }, info.ArgumentList);
        Assert.All(info.ArgumentList, argument =>
        {
            var lower = argument.ToLowerInvariant();
            Assert.DoesNotContain("token", lower);
            Assert.DoesNotContain("secret", lower);
            Assert.DoesNotContain("password", lower);
            Assert.DoesNotContain("access_key", lower);
            Assert.DoesNotContain("refresh_key", lower);
        });
    }

    [Fact]
    public void ProductRuntimeOpenAcceptsOptionalChildContainment()
    {
        var open = typeof(ProductEdgeRuntime)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(method => method.Name == nameof(ProductEdgeRuntime.OpenAsync));
        var containment = Assert.Single(open.GetParameters(), parameter =>
            parameter.ParameterType == typeof(IChildProcessContainment));

        Assert.True(containment.HasDefaultValue);
        Assert.Null(containment.DefaultValue);
    }

    [Fact]
    public void R0SampleFixtureShipsBesideAppHost()
    {
        var assemblyDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location)
            ?? throw new DirectoryNotFoundException("AppHost assembly directory unavailable.");
        var fixture = Path.Combine(
            assemblyDirectory,
            "fixtures",
            "r0",
            "sample-flow.jsonl");

        Assert.True(File.Exists(fixture), $"Missing packaged sample fixture: {fixture}");
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WOLPERTINGER.slnx")))
                return directory.FullName;
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate WOLPERTINGER repository root.");
    }
}
