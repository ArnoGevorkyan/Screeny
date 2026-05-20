using ScreenTimeTracker.Helpers;

namespace ScreenTimeTracker.Tests;

[TestClass]
public sealed class TimeFormatterTests
{
    [TestMethod]
    public void FormatTimeSpan_MinutesAndSeconds_ReturnsCompactLabel()
    {
        var formatted = TimeFormatter.FormatTimeSpan(TimeSpan.FromSeconds(125));

        Assert.AreEqual("2m 5s", formatted);
    }

    [TestMethod]
    public void FormatTimeSpan_ExcessiveDuration_CapsAtReasonableMaximum()
    {
        var formatted = TimeFormatter.FormatTimeSpan(TimeSpan.FromDays(400));

        Assert.AreEqual("365d 0h 0m", formatted);
    }
}
