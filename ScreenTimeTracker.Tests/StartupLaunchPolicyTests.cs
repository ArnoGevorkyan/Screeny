using ScreenTimeTracker.Models;

namespace ScreenTimeTracker.Tests
{
    [TestClass]
    public class StartupLaunchPolicyTests
    {
        [TestMethod]
        public void ShouldShowWindow_NormalLaunch_ReturnsTrue()
        {
            Assert.IsTrue(StartupLaunchPolicy.ShouldShowWindow(startedFromWindowsStartup: false, isFirstRun: false));
        }

        [TestMethod]
        public void ShouldShowWindow_StartupLaunchAfterFirstRun_ReturnsFalse()
        {
            Assert.IsFalse(StartupLaunchPolicy.ShouldShowWindow(startedFromWindowsStartup: true, isFirstRun: false));
        }

        [TestMethod]
        public void ShouldShowWindow_StartupLaunchOnFirstRun_ReturnsTrue()
        {
            Assert.IsTrue(StartupLaunchPolicy.ShouldShowWindow(startedFromWindowsStartup: true, isFirstRun: true));
        }
    }
}
