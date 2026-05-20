using ScreenTimeTracker.Models;

namespace ScreenTimeTracker.Tests;

[TestClass]
public sealed class UsageSliceTests
{
    [TestMethod]
    public void TryCreate_ValidInterval_ReturnsFinalizedSlice()
    {
        var start = new DateTime(2026, 5, 20, 9, 0, 0);
        var end = start.AddMinutes(15);

        var created = UsageSlice.TryCreate("chrome", "Google Chrome", "Inbox", start, end, out var slice);

        Assert.IsTrue(created);
        Assert.IsNotNull(slice);
        Assert.AreEqual("chrome", slice.ProcessName);
        Assert.AreEqual("Google Chrome", slice.ApplicationName);
        Assert.AreEqual("Inbox", slice.WindowTitle);
        Assert.AreEqual(TimeSpan.FromMinutes(15), slice.Duration);
        Assert.AreEqual(start.Date, slice.Date);
    }

    [TestMethod]
    public void TryCreate_MissingProcessName_ReturnsFalse()
    {
        var start = new DateTime(2026, 5, 20, 9, 0, 0);

        var created = UsageSlice.TryCreate("", "", "", start, start.AddSeconds(1), out var slice);

        Assert.IsFalse(created);
        Assert.IsNull(slice);
    }

    [TestMethod]
    public void TryCreate_EndTimeEqualsStartTime_ReturnsFalse()
    {
        var start = new DateTime(2026, 5, 20, 9, 0, 0);

        var created = UsageSlice.TryCreate("chrome", "chrome", "", start, start, out var slice);

        Assert.IsFalse(created);
        Assert.IsNull(slice);
    }

    [TestMethod]
    public void TryCreate_ApplicationNameMissing_DefaultsToProcessName()
    {
        var start = new DateTime(2026, 5, 20, 9, 0, 0);

        UsageSlice.TryCreate("notepad", "", "", start, start.AddSeconds(2), out var slice);

        Assert.IsNotNull(slice);
        Assert.AreEqual("notepad", slice.ApplicationName);
    }

    [TestMethod]
    public void TryCreate_DurationLongerThanOneDay_CapsDuration()
    {
        var start = new DateTime(2026, 5, 20, 9, 0, 0);

        UsageSlice.TryCreate("chrome", "chrome", "", start, start.AddHours(30), out var slice);

        Assert.IsNotNull(slice);
        Assert.AreEqual(TimeSpan.FromHours(24), slice.Duration);
    }
}
