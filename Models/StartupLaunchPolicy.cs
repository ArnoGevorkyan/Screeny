namespace ScreenTimeTracker.Models
{
    public static class StartupLaunchPolicy
    {
        public static bool ShouldShowWindow(bool startedFromWindowsStartup, bool isFirstRun)
        {
            return !startedFromWindowsStartup || isFirstRun;
        }
    }
}
