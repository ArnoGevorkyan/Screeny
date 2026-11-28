using Microsoft.Data.Sqlite;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ScreenTimeTracker.Models;
using ScreenTimeTracker.Services;
using SQLitePCL;

[assembly: DoNotParallelize]

namespace ScreenTimeTracker.Core.Tests;

[TestClass]
public sealed class UsageLedgerTests
{
    private static readonly DateTime Start = new(2026, 5, 20, 9, 0, 0);
    private DirectoryInfo _directory = null!;
    private string DatabasePath => Path.Combine(_directory.FullName, "fixture.db");

    [ClassInitialize]
    public static void Initialize(TestContext _) => Batteries_V2.Init();

    [TestInitialize]
    public void CreateTempDirectory() => _directory = Directory.CreateTempSubdirectory("screeny-core-");

    [TestCleanup]
    public void DeleteTempDirectory()
    {
        SqliteConnection.ClearAllPools();
        _directory.Delete(recursive: true);
    }

    [TestMethod]
    public async Task StopTracking_OpenVisit_PersistsOnlyAfterFinalization()
    {
        using var db = OpenDatabase();
        using var tracker = new WindowTrackingService();
        var writer = new UsageSliceWriter(db, (_, _) => { });
        tracker.UsageSliceFinalized += (_, slice) => writer.Enqueue(slice);

        try
        {
            tracker.ClockForTest = () => Start;
            tracker.SetOpenRecordsForTest(null, null);
            tracker.ProcessWindowChangeForTest((IntPtr)1, 0, "fixture-editor", "Fixture document");
            tracker.UpdateFocusedRecordForTest(Start.AddSeconds(4));

            var live = tracker.GetRecords().Single();
            Assert.IsTrue(live.IsFocused);
            Assert.IsNull(live.EndTime);
            Assert.IsTrue(await writer.WaitForDrainAsync());
            Assert.IsEmpty(db.GetRawRecordsForDateRange(Start, Start));

            tracker.ClockForTest = () => Start.AddSeconds(8);
            tracker.StopTracking();
            tracker.StopTracking();

            Assert.IsTrue(await writer.WaitForDrainAsync());
            var committed = db.GetRawRecordsForDateRange(Start, Start);
            Assert.HasCount(1, committed);
            Assert.AreEqual("fixture-editor", committed[0].ProcessName);
            Assert.AreEqual(Start, committed[0].StartTime);
            Assert.AreEqual(Start.AddSeconds(8), committed[0].EndTime);
            Assert.AreEqual(TimeSpan.FromSeconds(8), committed[0].Duration);
            Assert.IsFalse(committed[0].IsFocused);
        }
        finally
        {
            writer.Stop();
        }
    }

    [TestMethod]
    public void SaveSliceWithResult_ReofferedAfterReopen_KeepsOriginalRow()
    {
        var slice = CreateSlice("fixture-editor", Start, 8);
        using (var db = OpenDatabase())
        {
            Assert.AreEqual(PersistenceResult.Saved, db.SaveSliceWithResult(slice));
        }
        SqliteConnection.ClearAllPools();

        using var reopened = OpenDatabase();
        var reoffered = slice with { ApplicationName = "New fixture label", WindowTitle = "Other fixture document" };
        Assert.AreEqual(PersistenceResult.DuplicateIgnored, reopened.SaveSliceWithResult(reoffered));

        var committed = reopened.GetRawRecordsForDateRange(Start, Start);
        Assert.HasCount(1, committed);
        Assert.AreEqual("Fixture app", committed[0].ApplicationName);
        Assert.AreEqual(TimeSpan.FromSeconds(8), committed[0].Duration);
    }

    [TestMethod]
    public void GetUsageReportForDateRange_ShortVisits_CountsOnlySelectedDay()
    {
        using var db = OpenDatabase();
        Assert.AreEqual(PersistenceResult.Saved, db.SaveSliceWithResult(CreateSlice("fixture-editor", Start, 4)));
        Assert.AreEqual(PersistenceResult.Saved, db.SaveSliceWithResult(CreateSlice("fixture-editor", Start.AddMinutes(1), 7)));
        Assert.AreEqual(PersistenceResult.Saved, db.SaveSliceWithResult(CreateSlice("fixture-editor", Start.AddDays(1), 12)));

        var report = db.GetUsageReportForDateRange(Start, Start);
        Assert.HasCount(1, report);
        Assert.AreEqual(("fixture-editor", TimeSpan.FromSeconds(11)), report[0]);
    }

    [TestMethod]
    public async Task Enqueue_BusyLedger_CommitsFailedHeadBeforeNextOffer()
    {
        using var db = OpenDatabase();
        var retry = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = new UsageSliceWriter(db, (health, _) =>
        {
            if (health == PersistenceHealthStatus.RetryableIssue)
                retry.TrySetResult();
        });

        try
        {
            using (var blocker = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false
            }.ToString()))
            {
                blocker.Open();
                using var reservation = blocker.BeginTransaction(deferred: false);
                writer.Enqueue(CreateSlice("fixture-editor", Start.AddMinutes(1), 8));
                await retry.Task.WaitAsync(TimeSpan.FromSeconds(10));
                writer.Enqueue(CreateSlice("fixture-browser", Start, 5));
                reservation.Commit();
            }

            Assert.IsTrue(await writer.WaitForDrainAsync());
            var committed = db.GetRawRecordsForDateRange(Start, Start).OrderBy(row => row.Id);
            CollectionAssert.AreEqual(
                new[] { "fixture-editor", "fixture-browser" },
                committed.Select(row => row.ProcessName).ToArray());
        }
        finally
        {
            writer.Stop();
        }
    }

    private DatabaseService OpenDatabase()
    {
        var database = new DatabaseService(DatabasePath);
        if (database.IsDatabaseInitialized())
            return database;

        database.Dispose();
        throw new InvalidOperationException("Could not open the temporary SQLite ledger.");
    }

    private static UsageSlice CreateSlice(string processName, DateTime start, int seconds)
    {
        Assert.IsTrue(UsageSlice.TryCreate(
            processName, "Fixture app", "Fixture document", start, start.AddSeconds(seconds), out var slice));
        Assert.IsNotNull(slice);
        return slice;
    }
}
