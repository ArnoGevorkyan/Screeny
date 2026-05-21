using ScreenTimeTracker.Models;
using ScreenTimeTracker.Services;
using Microsoft.Data.Sqlite;
using SQLitePCL;

namespace ScreenTimeTracker.Tests;

[TestClass]
public sealed class DatabaseServiceTests
{
    [ClassInitialize]
    public static void Initialize(TestContext _)
    {
        Batteries_V2.Init();
    }

    [TestMethod]
    public void Constructor_FreshDatabase_SetsLatestSchemaVersion()
    {
        using var db = CreateDatabaseService(out var dbPath);

        try
        {
            Assert.IsTrue(db.IsDatabaseInitialized());
            Assert.AreEqual(2, db.SchemaVersion);
            Assert.IsTrue(File.Exists(dbPath));
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void Constructor_FreshDatabase_UsesWalJournalMode()
    {
        using var db = CreateDatabaseService(out var dbPath);

        try
        {
            Assert.IsTrue(db.IsDatabaseInitialized());
            Assert.AreEqual("wal", db.CurrentJournalMode, ignoreCase: true);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void SaveSlice_ValidSlice_CanReadRawRecord()
    {
        using var db = CreateDatabaseService(out var dbPath);
        var start = new DateTime(2026, 5, 20, 9, 0, 0);
        UsageSlice.TryCreate("notepad", "Notepad", "Untitled", start, start.AddMinutes(3), out var slice);

        try
        {
            Assert.IsNotNull(slice);
            Assert.IsTrue(db.SaveSlice(slice));

            var records = db.GetRawRecordsForDateRange(start.Date, start.Date);

            Assert.HasCount(1, records);
            Assert.AreEqual("notepad", records[0].ProcessName);
            Assert.AreEqual("Notepad", records[0].ApplicationName);
            Assert.AreEqual(TimeSpan.FromMinutes(3), records[0].Duration);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void SaveSliceWithResult_ValidSlice_ReturnsSaved()
    {
        using var db = CreateDatabaseService(out var dbPath);

        try
        {
            var result = db.SaveSliceWithResult(CreateSlice());

            Assert.AreEqual(PersistenceResult.Saved, result);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void SaveSliceWithResult_SameSliceSavedTwice_ReturnsDuplicateIgnored()
    {
        using var db = CreateDatabaseService(out var dbPath);
        var slice = CreateSlice();

        try
        {
            Assert.AreEqual(PersistenceResult.Saved, db.SaveSliceWithResult(slice));
            Assert.AreEqual(PersistenceResult.DuplicateIgnored, db.SaveSliceWithResult(slice));

            var records = db.GetRawRecordsForDateRange(slice.Date, slice.Date);
            Assert.HasCount(1, records);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void IsFirstRun_FreshThenSavedDatabase_ReturnsExpectedState()
    {
        using var db = CreateDatabaseService(out var dbPath);

        try
        {
            Assert.IsTrue(db.IsFirstRun());

            Assert.AreEqual(PersistenceResult.Saved, db.SaveSliceWithResult(CreateSlice()));

            Assert.IsFalse(db.IsFirstRun());
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void GetUsageReportForDateRange_MultipleDurations_FiltersShortRowsAndSortsDescending()
    {
        using var db = CreateDatabaseService(out var dbPath);
        var date = new DateTime(2026, 5, 20, 9, 0, 0);

        try
        {
            db.SaveSlice(CreateSlice("notepad", date, minutes: 6));
            db.SaveSlice(CreateSlice("calc", date.AddMinutes(10), minutes: 3));
            db.SaveSlice(CreateSlice("chrome", date.AddMinutes(20), minutes: 8));

            var report = db.GetUsageReportForDateRange(date.Date, date.Date);

            Assert.HasCount(2, report);
            Assert.AreEqual("chrome", report[0].ProcessName);
            Assert.AreEqual(TimeSpan.FromMinutes(8), report[0].TotalDuration);
            Assert.AreEqual("notepad", report[1].ProcessName);
            Assert.AreEqual(TimeSpan.FromMinutes(6), report[1].TotalDuration);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void CleanupExpiredRecords_OlderThanRetention_RemovesOnlyExpiredRows()
    {
        using var db = CreateDatabaseService(out var dbPath);
        var expiredStart = DateTime.Now.Date.AddDays(-10).AddHours(9);
        var retainedStart = DateTime.Now.Date.AddDays(-1).AddHours(9);

        try
        {
            db.SaveSlice(CreateSlice("expired", expiredStart, minutes: 6));
            db.SaveSlice(CreateSlice("retained", retainedStart, minutes: 6));

            db.CleanupExpiredRecords(daysToKeep: 7);

            var records = db.GetRawRecordsForDateRange(expiredStart.Date, retainedStart.Date);
            Assert.HasCount(1, records);
            Assert.AreEqual("retained", records[0].ProcessName);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void PerformDatabaseMaintenance_FreshDatabase_ReturnsTrue()
    {
        using var db = CreateDatabaseService(out var dbPath);

        try
        {
            Assert.IsTrue(db.PerformDatabaseMaintenance());
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void Constructor_DatabaseWithDuplicateFinalizedIntervals_RemovesDuplicatesAndCreatesUniqueIndex()
    {
        var dbPath = CreateDatabasePath();
        var slice = CreateSlice();
        CreateLegacyDatabase(dbPath, includeDate: true, includeV2Columns: true, userVersion: 2);
        InsertRawSlice(dbPath, slice);
        InsertRawSlice(dbPath, slice);

        try
        {
            using var db = new DatabaseService(dbPath);

            Assert.IsTrue(db.IsDatabaseInitialized());
            Assert.AreEqual(PersistenceResult.DuplicateIgnored, db.SaveSliceWithResult(slice));

            var records = db.GetRawRecordsForDateRange(slice.Date, slice.Date);
            Assert.HasCount(1, records);
            Assert.IsTrue(IndexExists(dbPath, "idx_app_usage_finalized_slice_unique"));
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void WipeDatabase_ExistingRecords_RemovesRows()
    {
        using var db = CreateDatabaseService(out var dbPath);
        var start = new DateTime(2026, 5, 20, 9, 0, 0);
        UsageSlice.TryCreate("notepad", "Notepad", "Untitled", start, start.AddMinutes(3), out var slice);

        try
        {
            Assert.IsNotNull(slice);
            db.SaveSlice(slice);

            Assert.IsTrue(db.WipeDatabase());

            var records = db.GetRawRecordsForDateRange(start.Date, start.Date);
            Assert.IsEmpty(records);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void Constructor_V0DatabaseWithoutDateColumn_MigratesToLatestSchema()
    {
        var dbPath = CreateDatabasePath();
        CreateLegacyDatabase(dbPath, includeDate: false, includeV2Columns: false, userVersion: 0);

        try
        {
            using var db = new DatabaseService(dbPath);

            Assert.IsTrue(db.IsDatabaseInitialized());
            Assert.AreEqual(2, db.SchemaVersion);

            using var reopened = new DatabaseService(dbPath);
            Assert.IsTrue(reopened.IsDatabaseInitialized());
            Assert.AreEqual(2, reopened.SchemaVersion);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void Constructor_V1DatabaseWithoutV2Columns_MigratesToLatestSchema()
    {
        var dbPath = CreateDatabasePath();
        CreateLegacyDatabase(dbPath, includeDate: true, includeV2Columns: false, userVersion: 1);

        try
        {
            using var db = new DatabaseService(dbPath);

            Assert.IsTrue(db.IsDatabaseInitialized());
            Assert.AreEqual(2, db.SchemaVersion);

            using var reopened = new DatabaseService(dbPath);
            Assert.IsTrue(reopened.IsDatabaseInitialized());
            Assert.AreEqual(2, reopened.SchemaVersion);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void Constructor_V0DatabaseWithLatestColumns_SetsLatestSchemaVersion()
    {
        var dbPath = CreateDatabasePath();
        CreateLegacyDatabase(dbPath, includeDate: true, includeV2Columns: true, userVersion: 0);

        try
        {
            using var db = new DatabaseService(dbPath);

            Assert.IsTrue(db.IsDatabaseInitialized());
            Assert.AreEqual(2, db.SchemaVersion);
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void Constructor_CorruptDatabaseFile_MovesFileAndCreatesFreshDatabase()
    {
        var dbPath = CreateDatabasePath();
        File.WriteAllText(dbPath, "not a sqlite database");

        try
        {
            using var db = new DatabaseService(dbPath);

            Assert.IsTrue(db.IsDatabaseInitialized());
            Assert.AreEqual(2, db.SchemaVersion);
            Assert.HasCount(1, Directory.GetFiles(Path.GetDirectoryName(dbPath)!, "*.corrupt.*"));
        }
        finally
        {
            DeleteDatabase(dbPath);
        }
    }

    [TestMethod]
    public void Constructor_DirectoryPathInsteadOfFile_EntersDegradedState()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ScreenTimeTrackerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            using var db = new DatabaseService(directory);

            Assert.IsFalse(db.IsDatabaseInitialized());
            Assert.AreEqual(0, db.SchemaVersion);
            Assert.IsFalse(db.SaveSlice(CreateSlice()));
            Assert.AreEqual(PersistenceResult.FatalFailure, db.SaveSliceWithResult(CreateSlice()));
            Assert.IsFalse(db.WipeDatabase());
            Assert.IsEmpty(db.GetRawRecordsForDateRange(DateTime.Today, DateTime.Today));
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static DatabaseService CreateDatabaseService(out string dbPath)
    {
        dbPath = CreateDatabasePath();
        return new DatabaseService(dbPath);
    }

    private static string CreateDatabasePath()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ScreenTimeTrackerTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "screentime.db");
    }

    private static void CreateLegacyDatabase(string dbPath, bool includeDate, bool includeV2Columns, int userVersion)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        var columns = new List<string>
        {
            "id INTEGER PRIMARY KEY AUTOINCREMENT",
            "process_name TEXT NOT NULL",
            "app_name TEXT",
            "start_time TEXT NOT NULL",
            "end_time TEXT",
            "duration INTEGER"
        };

        if (includeDate)
        {
            columns.Insert(1, "date TEXT");
        }

        if (includeV2Columns)
        {
            columns.Add("is_focused INTEGER DEFAULT 0");
            columns.Add("last_updated TEXT");
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"CREATE TABLE app_usage ({string.Join(", ", columns)});";
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA user_version = {userVersion};";
            command.ExecuteNonQuery();
        }
    }

    private static UsageSlice CreateSlice()
    {
        var start = new DateTime(2026, 5, 20, 9, 0, 0);
        return CreateSlice("notepad", start, minutes: 3);
    }

    private static UsageSlice CreateSlice(string processName, DateTime start, int minutes)
    {
        UsageSlice.TryCreate(processName, processName, "Untitled", start, start.AddMinutes(minutes), out var slice);
        Assert.IsNotNull(slice);
        return slice;
    }

    private static void InsertRawSlice(string dbPath, UsageSlice slice)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO app_usage (date, process_name, app_name, start_time, end_time, duration, is_focused, last_updated)
            VALUES (@Date, @ProcessName, @ApplicationName, @StartTime, @EndTime, @Duration, 0, @LastUpdated);";
        command.Parameters.AddWithValue("@Date", slice.Date.ToString("yyyy-MM-dd"));
        command.Parameters.AddWithValue("@ProcessName", slice.ProcessName);
        command.Parameters.AddWithValue("@ApplicationName", slice.ApplicationName);
        command.Parameters.AddWithValue("@StartTime", slice.StartTime.ToString("o"));
        command.Parameters.AddWithValue("@EndTime", slice.EndTime.ToString("o"));
        command.Parameters.AddWithValue("@Duration", (long)slice.Duration.TotalMilliseconds);
        command.Parameters.AddWithValue("@LastUpdated", DateTime.UtcNow.ToString("o"));
        command.ExecuteNonQuery();
    }

    private static bool IndexExists(string dbPath, string indexName)
    {
        using var connection = new SqliteConnection($"Data Source={dbPath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = @Name;";
        command.Parameters.AddWithValue("@Name", indexName);
        return command.ExecuteScalar() != null;
    }

    private static void DeleteDatabase(string dbPath)
    {
        var directory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // Best-effort cleanup for temp test files.
            }
        }
    }
}
