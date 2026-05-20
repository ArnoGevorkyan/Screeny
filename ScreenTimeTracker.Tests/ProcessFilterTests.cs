using ScreenTimeTracker.Models;

namespace ScreenTimeTracker.Tests;

[TestClass]
public sealed class ProcessFilterTests
{
    [TestMethod]
    public void ShouldIgnoreProcess_EmptyProcessName_ReturnsTrue()
    {
        Assert.IsTrue(ProcessFilter.ShouldIgnoreProcess(""));
    }

    [TestMethod]
    public void ShouldIgnoreProcess_SystemShellName_ReturnsTrue()
    {
        Assert.IsTrue(ProcessFilter.ShouldIgnoreProcess("Windows Shell Experience Host"));
    }

    [TestMethod]
    public void ShouldIgnoreProcess_NormalApplication_ReturnsFalse()
    {
        Assert.IsFalse(ProcessFilter.ShouldIgnoreProcess("notepad"));
    }
}
