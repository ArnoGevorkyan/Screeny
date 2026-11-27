using System.Diagnostics;
using LiveChartsCore.SkiaSharpView;
using ScreenTimeTracker.Models;
using ScreenTimeTracker.Services;

namespace ScreenTimeTracker;

[TestClass]
public sealed class DashboardTotalsTests
{
    [TestMethod]
    [DataRow("dominant", "8d 1h 18m", 695_880)]
    [DataRow("spread", "14h 0m 0s", 50_400)]
    public void Month_ShowsSameExactUsageInChartSummaryAndAppTotals(string mode, string top, int topSeconds)
    {
        using var fixture = new Fixture(mode);
        var end = DateTime.Today;
        var start = end.AddDays(-29);
        fixture.Window.LoadRange(start, end);
        var chart = fixture.Window.UsageChartLive.Series.OfType<ColumnSeries<double>>().Single().Values;
        var apps = fixture.Database.GetAggregatedRecordsWithLive(start, end, fixture.Tracking, includeLiveRecords: false);
        Assert.AreEqual("8d 14h 57m", fixture.Window.ChartTimeValue.Text);
        Assert.AreEqual(745_035, chart.Sum() * 3600, 0.00001);
        Assert.HasCount(1052, fixture.Window.AppList);
        Assert.AreEqual(27, fixture.Window.AppList.Count(r => r.FormattedDuration == "7h 0m 0s"));
        Assert.AreEqual("8d 14h 57m", fixture.Window.TotalScreenTime.Text);
        Assert.AreEqual(745_035L * TimeSpan.TicksPerSecond, apps.Sum(r => r.Duration.Ticks));
        Assert.AreEqual(topSeconds, apps[0].Duration.TotalSeconds);
        Assert.AreEqual(top, fixture.Window.MostUsedAppTime.Text);
        Assert.AreEqual("7h 8m 10s", fixture.Window.DailyAverage.Text);
        fixture.Window.Refresh();
        Assert.AreEqual(top, fixture.Window.MostUsedAppTime.Text);
        fixture.Window.PeriodicRedraw();
        Assert.AreEqual(top, fixture.Window.MostUsedAppTime.Text);
    }

    [TestMethod]
    [DataRow(-1, "54m 0s", 1)]
    [DataRow(-2, "17h 3m 15s", 6)]
    [DataRow(-45, "41m 0s", 1)]
    [DataRow(-46, "0s", 0)]
    public void Day_ShowsTheSelectedHistoryAndKeepsOneRowPerProcess(int offset, string total, int count)
    {
        using var fixture = new Fixture("dominant");
        fixture.Window.Load(DateTime.Today.AddDays(offset));
        Assert.AreEqual(total, fixture.Window.ChartTimeValue.Text);
        Assert.AreEqual(total, fixture.Window.TotalScreenTime.Text);
        Assert.HasCount(count, fixture.Window.AppList);
    }

    [TestMethod]
    public void Day_WithSeveralVisitsOver16Hours_ShowsUncappedAppTotal()
    {
        using var fixture = new Fixture();
        var day = DateTime.Today.AddDays(-1);
        fixture.Save("editor", day, day.AddHours(8));
        fixture.Save("editor", day.AddHours(8), day.AddHours(16));
        fixture.Save("editor", day.AddHours(16), day.AddHours(19));
        fixture.Window.Load(day);
        Assert.AreEqual("19h 0m 0s", fixture.Window.ChartTimeValue.Text);
        Assert.AreEqual("19h 0m 0s", fixture.Window.TotalScreenTime.Text);
        Assert.AreEqual("19h 0m 0s", fixture.Window.AppList.Single().FormattedDuration);
    }

    [TestMethod]
    public void Visit_BoundsRemainInForceBeforeRollup()
    {
        using var fixture = new Fixture();
        var day = DateTime.Today.AddDays(-1);
        Assert.IsFalse(UsageSlice.TryCreate("editor", "Editor", "", day, day, out _));
        Assert.IsFalse(UsageSlice.TryCreate("editor", "Editor", "", day, day.AddSeconds(-1), out _));
        Assert.IsFalse(UsageSlice.TryCreate(" ", "Editor", "", day, day.AddHours(1), out _));
        Assert.IsTrue(UsageSlice.TryCreate("editor", "Editor", "", day, day.AddHours(29), out var bounded));
        Assert.AreEqual(TimeSpan.FromHours(24), bounded!.Duration);
        Assert.AreEqual(day, bounded.Date);
        fixture.Save("editor", day, day.AddHours(20));
        fixture.Save("editor", day.AddHours(20), day.AddHours(22));

        fixture.Window.Load(day);
        Assert.AreEqual("18h 0m 0s", fixture.Window.ChartTimeValue.Text);
        Assert.AreEqual("18h 0m 0s", fixture.Window.TotalScreenTime.Text);
        fixture.Window.LoadRange(day.AddDays(-1), day);
        Assert.AreEqual("18h 0m 0s", fixture.Window.ChartTimeValue.Text);
        Assert.AreEqual("18h 0m 0s", fixture.Window.TotalScreenTime.Text);
        CollectionAssert.AreEqual(new[] { "16h 0m 0s", "2h 0m 0s" }, fixture.Window.AppList.Select(r => r.FormattedDuration).ToArray());
        Assert.AreEqual("18h 0m 0s", fixture.Window.MostUsedAppTime.Text);
    }

    [TestMethod]
    public void Range_InitiallyLoadsOnlyFinalizedVisits_WhileLiveQueryCanOptIn()
    {
        using var fixture = new Fixture();
        var today = DateTime.Today;
        var older = today.AddDays(-2);
        fixture.Save("editor", older.AddHours(8), older.AddHours(16));
        fixture.Save("editor", older.AddDays(1).AddHours(8), older.AddDays(1).AddHours(15).AddMinutes(50));
        fixture.Tracking.SetOpenRecordsForTest(new AppUsageRecord
        {
            ProcessName = "EDITOR",
            ApplicationName = "Other title, same process",
            Date = today,
            StartTime = today.AddHours(1),
            _accumulatedDuration = TimeSpan.FromMinutes(20),
        }, null);
        fixture.Window.LoadRange(older, today);
        Assert.AreEqual("15h 50m 0s", fixture.Window.ChartTimeValue.Text);
        Assert.AreEqual("15h 50m 0s", fixture.Window.TotalScreenTime.Text);
        Assert.HasCount(2, fixture.Window.AppList);
        Assert.AreEqual(TimeSpan.FromMinutes(970), fixture.Database.GetAggregatedRecordsWithLive(older, today, fixture.Tracking).Single().Duration);
        Assert.AreEqual(TimeSpan.FromMinutes(950), fixture.Database.GetAggregatedRecordsWithLive(older, today.AddDays(-1), fixture.Tracking).Single().Duration);
        Assert.AreEqual(TimeSpan.FromMinutes(20), fixture.Tracking.GetRecords().Single().Duration);
    }

    [TestMethod]
    public void Day_WithOvernightIdleInTheStartDayBucket_KeepsTheDailyCeiling()
    {
        using var fixture = new Fixture();
        var day = DateTime.Today.AddDays(-2);
        fixture.Save("editor", day.AddHours(8), day.AddHours(18));
        fixture.Save("Idle / Away", day.AddHours(18), day.AddHours(33));
        fixture.Window.Load(day);
        Assert.AreEqual("1d 1h 0m", fixture.Window.ChartTimeValue.Text);
        Assert.AreEqual("1d 0h 0m", fixture.Window.TotalScreenTime.Text);
    }

    [TestMethod]
    public void Range_EndingToday_ReflectsANewVisitAfterRedrawWithoutChangingInitialLoad()
    {
        using var fixture = new Fixture();
        var today = DateTime.Today;
        var yesterday = today.AddDays(-1);
        fixture.Save("editor", yesterday.AddHours(8), yesterday.AddHours(9));
        fixture.Window.LoadRange(yesterday, today);
        Assert.AreEqual("1h 0m 0s", fixture.Window.TotalScreenTime.Text);

        var start = today.AddHours(8);
        var end = start.AddHours(2);
        fixture.Save("browser", start, end);
        fixture.Window.ReceiveTrackedVisit(new AppUsageRecord
        {
            ProcessName = "browser", Date = today, StartTime = start, EndTime = end, _accumulatedDuration = end - start
        });
        fixture.Window.Refresh();

        Assert.AreEqual("3h 0m 0s", fixture.Window.ChartTimeValue.Text);
        Assert.AreEqual("3h 0m 0s", fixture.Window.TotalScreenTime.Text);
        Assert.AreEqual("browser", fixture.Window.MostUsedApp.Text);
        Assert.AreEqual("2h 0m 0s", fixture.Window.MostUsedAppTime.Text);
        Assert.HasCount(2, fixture.Window.AppList);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _folder = Directory.CreateTempSubdirectory("screeny-totals-").FullName;

        internal Fixture(string? mode = null)
        {
            var db = Path.Combine(_folder, "data", "usage.db");
            if (mode != null)
            {
                using var process = Process.Start("python3", [Path.Combine(Environment.GetEnvironmentVariable("SCREENY_TEST_DATA")!, "seed.py"), db, mode])!;
                process.WaitForExit();
                Assert.AreEqual(0, process.ExitCode);
            }
            Database = new DatabaseService(db);
            Assert.IsTrue(Database.IsDatabaseInitialized());
            Window = new MainWindow(Database, Tracking);
        }

        internal DatabaseService Database { get; }
        internal WindowTrackingService Tracking { get; } = new();
        internal MainWindow Window { get; }
        internal void Save(string process, DateTime start, DateTime end)
        {
            Assert.IsTrue(UsageSlice.TryCreate(process, "Friendly Editor", "", start, end, out var slice));
            Assert.AreEqual(PersistenceResult.Saved, Database.SaveSliceWithResult(slice!));
        }

        public void Dispose()
        {
            Tracking.Dispose();
            Database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            Directory.Delete(_folder, recursive: true);
        }
    }
}
