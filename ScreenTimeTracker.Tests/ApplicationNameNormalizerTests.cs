using ScreenTimeTracker.Helpers;

namespace ScreenTimeTracker.Tests;

[TestClass]
public sealed class ApplicationNameNormalizerTests
{
    [TestMethod]
    public void NormalizeProcessName_WindowTitleProvidedByCaller_IsNotPartOfStableIdentity()
    {
        var normalized = ApplicationNameNormalizer.NormalizeProcessName("chrome");

        Assert.AreEqual("chrome", normalized);
    }

    [TestMethod]
    public void NormalizeProcessName_ProcessNameHasArchitectureSuffix_RemovesSuffix()
    {
        var normalized = ApplicationNameNormalizer.NormalizeProcessName("Example App (x64)");

        Assert.AreEqual("Example App", normalized);
    }
}
