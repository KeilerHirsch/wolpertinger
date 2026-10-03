using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Xml.Linq;
using Wolpertinger.Product.Contracts;

namespace Wolpertinger.AppHost.Tests;

public sealed class PackagedActivationParsingTests
{
    [Fact]
    public void ProtocolArgumentBecomesBoundedCallbackActivation()
    {
        const string uri = "wolpertinger://frontier/oauth?code=synthetic&state=synthetic";

        var activation = Parse("--protocol", uri);

        Assert.Equal(ProductActivationKind.ProtocolCallback, activation.Kind);
        Assert.Equal(uri, activation.Payload);
    }

    [Fact]
    public void StartupArgumentBecomesStartupActivation()
        => Assert.Equal(
            ProductActivationKind.Startup,
            Parse("--startup").Kind);

    [Fact]
    public void MismatchedProtocolAndExtraArgumentsFailClosed()
    {
        Assert.Throws<InvalidDataException>(() =>
            Parse("--protocol", "https://attacker.invalid/oauth?code=x&state=y"));
        Assert.Throws<InvalidDataException>(() =>
            Parse("--startup", "extra"));
    }

    [Fact]
    public void PackageManifestFreezesR0DesktopBoundary()
    {
        var root = RepoRoot();
        var manifestPath = Path.Combine(
            root,
            "packaging",
            "Wolpertinger.Package",
            "Package.appxmanifest");
        var document = XDocument.Load(manifestPath);

        XNamespace foundation =
            "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        XNamespace uap3 =
            "http://schemas.microsoft.com/appx/manifest/uap/windows10/3";
        XNamespace uap10 =
            "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
        XNamespace desktop =
            "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
        XNamespace rescap =
            "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

        var identity = Assert.Single(document.Descendants(foundation + "Identity"));
        Assert.Equal("KeilerHirsch.WOLPERTINGER.R0.Local", (string?)identity.Attribute("Name"));
        Assert.Equal("x64", (string?)identity.Attribute("ProcessorArchitecture"));

        var application = Assert.Single(document.Descendants(foundation + "Application"));
        Assert.Equal("Wolpertinger.AppHost.exe", (string?)application.Attribute("Executable"));
        Assert.Equal("Windows.FullTrustApplication", (string?)application.Attribute("EntryPoint"));
        Assert.Equal("packagedClassicApp", (string?)application.Attribute(uap10 + "RuntimeBehavior"));
        Assert.Equal("mediumIL", (string?)application.Attribute(uap10 + "TrustLevel"));

        var protocolExtension = Assert.Single(
            document.Descendants(uap3 + "Extension"),
            element => (string?)element.Attribute("Category") == "windows.protocol");
        var protocol = Assert.Single(protocolExtension.Descendants(uap3 + "Protocol"));
        Assert.Equal("wolpertinger", (string?)protocol.Attribute("Name"));
        Assert.Equal(@"--protocol ""%1""", (string?)protocol.Attribute("Parameters"));

        var startupExtension = Assert.Single(
            document.Descendants(desktop + "Extension"),
            element => (string?)element.Attribute("Category") == "windows.startupTask");
        var startup = Assert.Single(startupExtension.Descendants(desktop + "StartupTask"));
        Assert.Equal("WolpertingerStartup", (string?)startup.Attribute("TaskId"));
        Assert.Equal("false", (string?)startup.Attribute("Enabled"));

        Assert.Contains(
            document.Descendants(foundation + "Capability"),
            capability => (string?)capability.Attribute("Name") == "internetClient");
        Assert.Contains(
            document.Descendants(rescap + "Capability"),
            capability => (string?)capability.Attribute("Name") == "runFullTrust");
    }

    private static ProductActivation Parse(params string[] args)
    {
        var method = typeof(Wolpertinger.AppHost.Program).GetMethod(
            "ParseInitialActivation",
            BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new MissingMethodException("AppHost activation parser was not found.");

        try
        {
            return Assert.IsType<ProductActivation>(
                method.Invoke(null, [args]));
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "WOLPERTINGER.slnx")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("WOLPERTINGER repository root not found.");
    }
}
