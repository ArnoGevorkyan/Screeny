using LiveChartsCore.SkiaSharpView;
using ScreenTimeTracker.Models;
using ScreenTimeTracker.Services;

[assembly: DoNotParallelize]

namespace ScreenTimeTracker;

[TestClass]
public sealed class HourlyChartTests
{
    private string _folder = null!;
    private DatabaseService _database = null!;
    private WindowTrackingService _tracking = null!;

    [TestInitialize]
    public void CreateFixture()
    {
        _folder = Directory.CreateTempSubdirectory("screeny-hourly-").FullName;
        _database = new DatabaseService(Path.Combine(_folder, "usage.db"));
        Assert.IsTrue(_database.IsDatabaseInitialized());
        _tracking = new WindowTrackingService();
    }

    [TestCleanup]
    public void RemoveFixture()
    {
        _tracking.Dispose();
        _database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_folder, recursive: true);
    }

    [TestMethod]
    public void HourlyChart_ExactMidnightBoundary_MatchesSavedSegmentsOnBothDays()
    {
        var today = DateTime.Today;
        var yesterdayStart = today.AddMinutes(-2);
        var todayEnd = today.AddMinutes(3);
        _tracking.UsageSliceFinalized += (_, slice) => Assert.IsTrue(_database.SaveSlice(slice));
        _tracking.SetOpenRecordsForTest(new AppUsageRecord {
            ProcessName = "FakeBrowser", ApplicationName = "FakeBrowser", StartTime = yesterdayStart,
            Date = yesterdayStart.Date, IsFocused = true, WindowHandle = (nint)17
        }, null);
        _tracking.UpdateFocusedRecordForTest(today.AddSeconds(1));
        _tracking.ClockForTest = () => todayEnd;
        _tracking.StopTracking();

        var yesterday = _database.GetHourlyRecordsWithLive(today.AddDays(-1), _tracking).Single();
        var current = _database.GetHourlyRecordsWithLive(today, _tracking).Single();
        Assert.AreEqual(yesterdayStart, yesterday.StartTime);
        Assert.AreEqual(today, yesterday.EndTime);
        Assert.AreEqual(120000, yesterday.Duration.TotalMilliseconds);
        Assert.AreEqual(today, current.StartTime);
        Assert.AreEqual(todayEnd, current.EndTime);
        Assert.AreEqual(180000, current.Duration.TotalMilliseconds);

        var window = new MainWindow(_database, _tracking);
        window.Load(today.AddDays(-1));
        AssertBars(Minutes(window), ("11 PM", 2));
        Assert.AreEqual("2m 0s", window.ChartTimeValue.Text);
        window.Load(today);
        AssertBars(Minutes(window), ("12 AM", 3));
        Assert.AreEqual("3m 0s", window.ChartTimeValue.Text);
        Console.WriteLine("Yesterday 11 PM: one 120-second bar. Today 12 AM: one 180-second bar. Both equal their SQLite rows.");
    }

    [TestMethod]
    public void HourlyChart_ArbitraryOvernightRows_DoNotExpandPreviousDayBars()
    {
        var day = DateTime.Today.AddDays(-2);
        Save("FakeBrowser", day.AddHours(9), day.AddHours(9).AddMinutes(10));
        Save("OtherDay", day.AddHours(10), day.AddDays(1).AddHours(11));
        Save("MultiDay", day.AddHours(19), day.AddDays(2));
        var window = new MainWindow(_database, _tracking);
        window.Load(day);
        AssertBars(Minutes(window).Where(bar => bar.Value > 0).ToDictionary(), ("9 AM", 10));
    }

    [TestMethod]
    public void HourlyChart_SameProcessAtNineAndFive_LeavesTheGapEmpty()
    {
        var day = DateTime.Today.AddDays(-1);
        Save("FakeBrowser", day.AddHours(9), day.AddHours(9).AddMinutes(10));
        Save("FakeBrowser", day.AddHours(17), day.AddHours(17).AddMinutes(10));

        var window = new MainWindow(_database, _tracking);
        window.Load(day);
        var list = window.AppList;
        Assert.HasCount(1, list);
        Assert.AreEqual(TimeSpan.FromMinutes(20), list[0].Duration);
        Assert.AreEqual(day.AddHours(9), list[0].StartTime);
        Assert.AreEqual(day.AddHours(17).AddMinutes(10), list[0].EndTime);

        var bars = Minutes(window);
        Console.WriteLine($"List: {list.Count} row, {list[0].Duration:c}. Chart: {bars.Values.Sum():F0}m; {string.Join(", ", bars.Select(b => $"{b.Key}={b.Value:F0}m"))}");
        AssertBars(bars, ("9 AM", 10), ("5 PM", 10));
        Assert.AreEqual("20m 0s", window.ChartTimeValue.Text);
    }

    [TestMethod]
    public void HourlyChart_TwoProcessesAndAnHourBoundary_CountsEachOccupiedMinute()
    {
        var day = DateTime.Today.AddDays(-1);
        Save("FakeBrowser", day.AddHours(9).AddMinutes(55), day.AddHours(10).AddMinutes(5));
        Save("FakeEditor", day.AddHours(10).AddMinutes(5), day.AddHours(10).AddMinutes(20));
        var window = new MainWindow(_database, _tracking);
        window.Load(day);

        AssertBars(Minutes(window), ("9 AM", 5), ("10 AM", 20));
        CollectionAssert.AreEqual(new[] { "FakeEditor", "FakeBrowser" }, window.AppList.Select(r => r.ProcessName).ToArray());
        CollectionAssert.AreEqual(new[] { 15D, 10D }, window.AppList.Select(r => r.Duration.TotalMinutes).ToArray());
        Assert.AreEqual("25m 0s", window.ChartTimeValue.Text);
    }

    [TestMethod]
    public void HourlyChart_OlderDateWithFarApartVisits_KeepsBothBarsAndNoLiveTime()
    {
        var day = DateTime.Today.AddDays(-7);
        Save("FakeBrowser", day.AddHours(9), day.AddHours(9).AddMinutes(10));
        Save("FakeBrowser", day.AddHours(17), day.AddHours(17).AddMinutes(10));
        Save("FakeEditor", day.AddDays(1).AddHours(11), day.AddDays(1).AddHours(11).AddMinutes(40));
        SetTodayLiveRecord();

        var window = new MainWindow(_database, _tracking);
        window.Load(day);

        AssertBars(Minutes(window).Where(b => b.Value > 0).ToDictionary(), ("9 AM", 10), ("5 PM", 10));
        Assert.AreEqual("20m 0s", window.ChartTimeValue.Text);
        Assert.HasCount(1, window.AppList);
    }

    [TestMethod]
    public void HourlyChart_EmptyDate_HasZeroTotalAndOnlyTheExistingPlaceholder()
    {
        var day = DateTime.Today.AddDays(-1);
        Save("FakeBrowser", day.AddDays(-1).AddHours(9), day.AddDays(-1).AddHours(9).AddMinutes(10));
        SetTodayLiveRecord();
        var window = new MainWindow(_database, _tracking);
        window.Load(day);

        Assert.HasCount(0, window.AppList);
        Assert.HasCount(1, Minutes(window));
        Assert.AreEqual(0.06, Minutes(window).Values.Single(), 0.00001);
        Assert.AreEqual("0s", window.ChartTimeValue.Text);
    }

    [TestMethod]
    public void HourlyChart_HistoricalDayWithOnlyFourSeconds_DoesNotRedirectToToday()
    {
        var day = DateTime.Today.AddDays(-1);
        Save("FakeBrowser", day.AddHours(9), day.AddHours(9).AddSeconds(4));
        var window = new MainWindow(_database, _tracking) { Content = new() };
        window.Load(day);

        Assert.AreEqual(day, window.SelectedDate);
        Assert.HasCount(0, window.AppList);
        AssertBars(Minutes(window), ("9 AM", 4D / 60));
        Assert.AreEqual("4s", window.ChartTimeValue.Text);
    }

    [TestMethod]
    public void HourlyChart_StaleOpenRowAndIgnoredProcess_DontBecomeBars()
    {
        var day = DateTime.Today.AddDays(-1);
        using (var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_folder, "usage.db")};Cache=Shared;Pooling=False"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO app_usage (date, process_name, app_name, start_time, end_time, duration, is_focused, last_updated)
                VALUES ($date, 'LegacyOpenBrowser', '', $start, NULL, 600000, 0, $updated)
                """;
            command.Parameters.AddWithValue("$date", day.ToString("yyyy-MM-dd"));
            command.Parameters.AddWithValue("$start", day.AddHours(8).ToString("o"));
            command.Parameters.AddWithValue("$updated", DateTime.UtcNow.ToString("o"));
            Assert.AreEqual(1, command.ExecuteNonQuery());
        }
        Save("dwm (x86)", day.AddHours(10), day.AddHours(10).AddMinutes(10));
        Save("FakeEditor", day.AddHours(11), day.AddHours(11).AddMinutes(10));
        var window = new MainWindow(_database, _tracking);
        window.Load(day);

        CollectionAssert.AreEquivalent(new[] { "LegacyOpenBrowser", "FakeEditor" }, window.AppList.Select(r => r.ProcessName).ToArray());
        AssertBars(Minutes(window), ("11 AM", 10));
        Assert.AreEqual("10m 0s", window.ChartTimeValue.Text);

        window.Render();
        AssertBars(Minutes(window), ("11 AM", 10));
    }

    [TestMethod]
    public async Task HourlyChart_TodaysLiveAndFinalizedVisitsToOneProcess_KeepSeparateIntervalsOnRefresh()
    {
        var day = DateTime.Today;
        var start = SetTodayLiveRecord();
        Save("FakeBrowser", start.AddMinutes(5), start.AddMinutes(15));
        var window = new MainWindow(_database, _tracking);
        window.Load(day);

        Assert.HasCount(1, window.AppList);
        Assert.AreEqual(12, window.AppList[0].Duration.TotalMinutes, 0.1);
        Assert.AreEqual(12, Minutes(window).Values.Sum(), 0.1);
        Assert.IsNull(_tracking.CurrentRecord!.EndTime);

        _tracking.SetOpenRecordsForTest(null, null, isTracking: false);
        Save("FakeBrowser", start, start.AddMinutes(2));
        var rendered = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ChartTimeValue.NextText = rendered;
        window.Refresh();

        Assert.AreEqual("12m 0s", await rendered.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(12, Minutes(window).Values.Sum(), 0.00001);
    }

    [TestMethod]
    public void HourlyChart_LiveSnapshotAlsoSeenAsFinalized_CountsOnlyTheFinalizedVisit()
    {
        var start = SetTodayLiveRecord();
        Save("FakeBrowser", start, start.AddMinutes(3));
        var window = new MainWindow(_database, _tracking);
        window.Load(DateTime.Today);

        AssertBars(Minutes(window), ("1 AM", 3));
        Assert.AreEqual("3m 0s", window.ChartTimeValue.Text);
        Assert.AreEqual(TimeSpan.FromMinutes(3), window.AppList.Single().Duration);

        window.Render();
        Assert.AreEqual("3m 0s", window.ChartTimeValue.Text);
    }

    [TestMethod]
    public async Task HourlyChart_PeriodicRefreshUnderWriteLock_ReturnsBeforeReadingCompletes()
    {
        var day = DateTime.Today.AddDays(-1);
        Save("FakeBrowser", day.AddHours(9), day.AddHours(9).AddMinutes(10));
        var window = new MainWindow(_database, _tracking);
        window.Load(day);
        Assert.AreEqual("10m 0s", window.ChartTimeValue.Text);

        using var locked = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var writer = Task.Run(() =>
        {
            using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
                $"Data Source={Path.Combine(_folder, "usage.db")};Cache=Shared;Pooling=False");
            connection.Open();
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM app_usage";
            command.ExecuteNonQuery();
            locked.Set();
            if (release.Wait(TimeSpan.FromSeconds(10)))
                transaction.Commit();
        });
        var nextValue = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        window.ChartTimeValue.NextText = nextValue;
        var refresh = Task.CompletedTask;
        try
        {
            Assert.IsTrue(locked.Wait(TimeSpan.FromSeconds(3)));
            refresh = Task.Run(window.Refresh);
            await refresh.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.AreEqual("10m 0s", window.ChartTimeValue.Text);
        }
        finally
        {
            release.Set();
            await writer;
            await refresh;
        }

        Assert.AreEqual("0s", await nextValue.Task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [TestMethod]
    public async Task HourlyChart_HistoricalMaintenanceUnderWriteLock_RedrawsLoadedIntervals()
    {
        var day = DateTime.Today.AddDays(-1);
        Save("FakeBrowser", day.AddHours(9), day.AddHours(9).AddMinutes(10));
        var window = new MainWindow(_database, _tracking);
        window.Load(day);

        using var writer = new Microsoft.Data.Sqlite.SqliteConnection(
            $"Data Source={Path.Combine(_folder, "usage.db")};Cache=Shared;Pooling=False");
        writer.Open();
        using var transaction = writer.BeginTransaction();
        using var command = writer.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM app_usage";
        command.ExecuteNonQuery();

        var redraw = Task.Run(window.PeriodicRedraw);
        try
        {
            await redraw.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.AreEqual("10m 0s", window.ChartTimeValue.Text);
            AssertBars(Minutes(window), ("9 AM", 10));
        }
        finally
        {
            transaction.Rollback();
            await redraw;
        }
    }

    private DateTime SetTodayLiveRecord()
    {
        var start = DateTime.Today.AddHours(1).AddMinutes(10);
        var record = new AppUsageRecord
        {
            ProcessName = "FakeBrowser",
            Date = DateTime.Today,
            StartTime = start,
            _accumulatedDuration = TimeSpan.FromMinutes(2)
        };
        record.SetFocus(true);
        _tracking.SetOpenRecordsForTest(record, null);
        return start;
    }

    private void Save(string process, DateTime start, DateTime end)
    {
        Assert.IsTrue(UsageSlice.TryCreate(process, process, string.Empty, start, end, out var slice));
        Assert.AreEqual(PersistenceResult.Saved, _database.SaveSliceWithResult(slice!));
    }

    private static Dictionary<string, double> Minutes(MainWindow window)
        => window.UsageChartLive.XAxes.Single().Labels.Zip(
            ((ColumnSeries<double>)window.UsageChartLive.Series.Single()).Values)
            .ToDictionary(p => p.First, p => p.Second * 60);

    private static void AssertBars(Dictionary<string, double> bars, params (string Hour, double Minutes)[] expected)
    {
        Assert.HasCount(expected.Length, bars);
        foreach (var (hour, minutes) in expected)
        {
            Assert.IsTrue(bars.TryGetValue(hour, out var actual), $"Missing bar at {hour}");
            Assert.AreEqual(minutes, actual, 0.00001, hour);
        }
    }
}
