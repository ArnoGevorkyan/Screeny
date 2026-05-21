using System.Xml.Linq;

namespace ScreenTimeTracker.Tests;

[TestClass]
public sealed class ManifestPrivacyTests
{
    private static readonly XNamespace Foundation = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private static readonly XNamespace Restricted = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
    private static readonly XNamespace Uap = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    private static readonly XNamespace Desktop = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";

    [TestMethod]
    public void PackageManifest_Capabilities_DoNotIncludeNetworkAccess()
    {
        var manifest = LoadManifest();

        var capabilities = manifest
            .Descendants()
            .Where(e => e.Name.LocalName == "Capability")
            .Select(e => e.Attribute("Name")?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        CollectionAssert.DoesNotContain(capabilities, "internetClient");
        CollectionAssert.DoesNotContain(capabilities, "internetClientServer");
        CollectionAssert.DoesNotContain(capabilities, "privateNetworkClientServer");
    }

    [TestMethod]
    public void PackageManifest_Capabilities_KeepOnlyExpectedFullTrustCapability()
    {
        var manifest = LoadManifest();

        var restrictedCapabilities = manifest
            .Descendants(Restricted + "Capability")
            .Select(e => e.Attribute("Name")?.Value)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToList();

        Assert.HasCount(1, restrictedCapabilities);
        Assert.AreEqual("runFullTrust", restrictedCapabilities[0]);
    }

    [TestMethod]
    public void PackageManifest_StartupAndFullTrustExtensions_UsePackagedExecutable()
    {
        var manifest = LoadManifest();
        var app = manifest.Descendants(Foundation + "Application").Single();

        Assert.AreEqual("Screeny.exe", app.Attribute("Executable")?.Value);
        Assert.AreEqual("Windows.FullTrustApplication", app.Attribute("EntryPoint")?.Value);

        var fullTrustExtension = manifest
            .Descendants(Desktop + "Extension")
            .Single(e => e.Attribute("Category")?.Value == "windows.fullTrustProcess");
        Assert.AreEqual("Screeny.exe", fullTrustExtension.Attribute("Executable")?.Value);

        var startupExtension = manifest
            .Descendants(Desktop + "Extension")
            .Single(e => e.Attribute("Category")?.Value == "windows.startupTask");
        Assert.AreEqual("Screeny.exe", startupExtension.Attribute("Executable")?.Value);

        var startupTask = startupExtension.Element(Desktop + "StartupTask");
        Assert.IsNotNull(startupTask);
        Assert.AreEqual("ScreenyStartupTask", startupTask.Attribute("TaskId")?.Value);
        Assert.AreEqual("true", startupTask.Attribute("Enabled")?.Value);
    }

    [TestMethod]
    public void PackageManifest_ReferencedLogoAssets_Exist()
    {
        var repoRoot = FindRepoRoot();
        var manifest = LoadManifest();

        var assetPaths = manifest
            .Descendants()
            .Attributes()
            .Where(a => a.Name.LocalName.Contains("Logo", StringComparison.OrdinalIgnoreCase)
                || a.Name.LocalName.Equals("Image", StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Value)
            .Where(value => value.StartsWith("Assets", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var assetPath in assetPaths)
        {
            var normalizedPath = assetPath.Replace('\\', Path.DirectorySeparatorChar);
            Assert.IsTrue(File.Exists(Path.Combine(repoRoot, normalizedPath)), $"Missing manifest asset: {assetPath}");
        }
    }

    [TestMethod]
    public void ReadmeAndPrivacyPolicy_PromiseLocalOnlyNoTelemetryBehavior()
    {
        var repoRoot = FindRepoRoot();
        var readme = File.ReadAllText(Path.Combine(repoRoot, "README.md"));
        var privacy = File.ReadAllText(Path.Combine(repoRoot, "PRIVACY.md"));

        StringAssert.Contains(readme, "All tracking data is stored locally");
        StringAssert.Contains(readme, "never transmitted over the internet");
        StringAssert.Contains(privacy, "stored exclusively on your local device");
        StringAssert.Contains(privacy, "does not");
        StringAssert.Contains(privacy, "Connect to the internet");
        StringAssert.Contains(privacy, "analytics");
    }

    private static XDocument LoadManifest()
    {
        return XDocument.Load(Path.Combine(FindRepoRoot(), "Package.appxmanifest"));
    }

    private static string FindRepoRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);

        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Package.appxmanifest")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from test output directory.");
    }
}
