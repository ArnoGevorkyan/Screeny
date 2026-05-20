namespace ScreenTimeTracker.Models
{
    public sealed record UsageSlice(
        string ProcessName,
        string ApplicationName,
        string WindowTitle,
        DateTime StartTime,
        DateTime EndTime,
        TimeSpan Duration,
        DateTime Date)
    {
        public static bool TryCreate(
            string processName,
            string applicationName,
            string windowTitle,
            DateTime startTime,
            DateTime endTime,
            out UsageSlice? slice)
        {
            slice = null;

            if (string.IsNullOrWhiteSpace(processName))
            {
                return false;
            }

            if (endTime <= startTime)
            {
                return false;
            }

            var duration = endTime - startTime;
            if (duration.TotalHours > 24)
            {
                duration = TimeSpan.FromHours(24);
            }

            var stableProcessName = processName.Trim();
            var stableApplicationName = string.IsNullOrWhiteSpace(applicationName)
                ? stableProcessName
                : applicationName.Trim();

            slice = new UsageSlice(
                stableProcessName,
                stableApplicationName,
                windowTitle?.Trim() ?? string.Empty,
                startTime,
                endTime,
                duration,
                startTime.Date);

            return true;
        }
    }
}
