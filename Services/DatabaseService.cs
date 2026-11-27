using Microsoft.Data.Sqlite;
using ScreenTimeTracker.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SQLitePCL;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;

namespace ScreenTimeTracker.Services
{
    public class DatabaseService : IDisposable
    {
        private const int LatestSchemaVersion = 2;
        private const int BusyTimeoutMilliseconds = 5000;
        private const string JournalMode = "WAL";
        private const string FinalizedSliceIndex = "idx_app_usage_finalized_slice_unique";
        private static readonly (string Name, string Sql)[] LedgerObjects =
        [
            ("idx_app_usage_date", "CREATE INDEX idx_app_usage_date ON app_usage(date)"),
            ("idx_app_usage_process_name", "CREATE INDEX idx_app_usage_process_name ON app_usage(process_name)"),
            (FinalizedSliceIndex, """
                CREATE UNIQUE INDEX idx_app_usage_finalized_slice_unique
                ON app_usage(date, process_name, start_time, end_time)
                WHERE end_time IS NOT NULL
                """),
            ("vw_daily_app_usage", """
                CREATE VIEW vw_daily_app_usage AS
                SELECT date, process_name, SUM(duration) AS total_duration_ms
                FROM app_usage
                GROUP BY date, process_name
                """)
        ];
        private readonly string _dbPath;
        internal static string DefaultDatabasePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenTimeTracker", "screentime.db");
        private readonly SqliteConnection _connection;
        private bool _initializedSuccessfully = false;
        private bool _disposed = false;

        /// <summary>
        /// Initializes a new instance of the DatabaseService class.
        /// </summary>
        public DatabaseService()
            : this(DefaultDatabasePath)
        {
        }

        internal DatabaseService(string dbPath)
        {
            _dbPath = dbPath;

            try
            {
                var dataDirectory = Path.GetDirectoryName(_dbPath);
                if (!string.IsNullOrEmpty(dataDirectory) && !Directory.Exists(dataDirectory))
                {
                    Directory.CreateDirectory(dataDirectory);
                    Debug.WriteLine($"Created database directory: {dataDirectory}");
                }

                Debug.WriteLine($"Database path: {_dbPath}");

                var databaseExists = DatabaseFileExists(_dbPath);
                var hadSidecars = HasDatabaseSidecars();
                var createDatabase = !databaseExists || IsDatabaseCorrupt();
                if (createDatabase)
                {
                    if (hadSidecars || HasDatabaseSidecars())
                        throw new IOException("Cannot replace or create a database while its journal files are present.");

                    if (databaseExists)
                    {
                        var backupPath = _dbPath + ".corrupt." + DateTime.Now.ToString("yyyyMMddHHmmss");
                        File.Move(_dbPath, backupPath);
                        Debug.WriteLine($"Moved corrupted database to: {backupPath}");
                    }
                }

                var mode = createDatabase ? SqliteOpenMode.ReadWriteCreate : SqliteOpenMode.ReadWrite;
                var connectionString = CreateConnectionString(_dbPath, mode);
                Debug.WriteLine($"Using connection string: {connectionString}");

                _connection = new SqliteConnection(connectionString);
                InitializeDatabase();
                
                Debug.WriteLine($"Database file exists after initialization: {File.Exists(_dbPath)}");
                _initializedSuccessfully = true;
            }
            catch (Exception ex)
            {
                _connection = new SqliteConnection();
                _initializedSuccessfully = false;
                Debug.WriteLine($"Database initialization failed; entering degraded state: {ex.Message}");
            }
        }

        private static string CreateConnectionString(string dataSource, SqliteOpenMode mode, bool pooling = true)
        {
            return new SqliteConnectionStringBuilder
            {
                DataSource = dataSource,
                Mode = mode,
                Cache = SqliteCacheMode.Shared,
                Pooling = pooling
            }.ToString();
        }

        private SqliteConnection CreateConnection()
        {
            return new SqliteConnection(_connection.ConnectionString);
        }

        private static void OpenConnection(SqliteConnection connection, int busyTimeoutMilliseconds = BusyTimeoutMilliseconds)
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {busyTimeoutMilliseconds};";
            command.ExecuteNonQuery();
        }

        private static bool DatabaseFileExists(string path)
        {
            try
            {
                File.GetAttributes(path);
                return true;
            }
            catch (FileNotFoundException)
            {
                return false;
            }
        }

        private bool HasDatabaseSidecars() =>
            DatabaseFileExists(_dbPath + "-wal") ||
            DatabaseFileExists(_dbPath + "-shm") ||
            DatabaseFileExists(_dbPath + "-journal");

        private bool IsDatabaseCorrupt()
        {
            try
            {
                return IntegrityCheckReportsCorruption(SqliteOpenMode.ReadOnly);
            }
            catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == raw.SQLITE_READONLY_ROLLBACK)
            {
                return IntegrityCheckReportsCorruption(SqliteOpenMode.ReadWrite);
            }
        }

        private bool IntegrityCheckReportsCorruption(SqliteOpenMode mode)
        {
            using var checkConnection = new SqliteConnection(CreateConnectionString(_dbPath, mode, pooling: false));
            try
            {
                OpenConnection(checkConnection);
                using var command = checkConnection.CreateCommand();
                ReadSupportedSchemaVersion(command);
                command.CommandText = "PRAGMA integrity_check(1);";
                var result = command.ExecuteScalar() as string
                    ?? throw new InvalidDataException("Database integrity check returned no result.");
                Debug.WriteLine($"PRAGMA integrity_check result: {result}");
                return !result.Equals("ok", StringComparison.OrdinalIgnoreCase);
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode is raw.SQLITE_CORRUPT or raw.SQLITE_NOTADB)
            {
                Debug.WriteLine($"Database integrity check confirmed corruption: {ex.Message}");
                return true;
            }
        }

        // Check if database was initialized correctly
        public bool IsDatabaseInitialized() => _initializedSuccessfully;

        internal int SchemaVersion
        {
            get
            {
                if (!_initializedSuccessfully)
                {
                    return 0;
                }

                using var connection = CreateConnection();
                OpenConnection(connection);
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA user_version;";
                var result = command.ExecuteScalar();
                return result != null ? Convert.ToInt32(result) : 0;
            }
        }

        internal string CurrentJournalMode
        {
            get
            {
                if (!_initializedSuccessfully)
                {
                    return string.Empty;
                }

                using var connection = CreateConnection();
                OpenConnection(connection);
                using var command = connection.CreateCommand();
                command.CommandText = "PRAGMA journal_mode;";
                return command.ExecuteScalar()?.ToString() ?? string.Empty;
            }
        }

        public bool IsFirstRun()
        {
            if (!_initializedSuccessfully)
                return true;

            try
            {
                using var connection = CreateConnection();
                OpenConnection(connection);
                Debug.WriteLine("IsFirstRun: Opened database connection");
                
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM app_usage";
                var result = Convert.ToInt32(command.ExecuteScalar());
                
                Debug.WriteLine($"IsFirstRun: Found {result} records in database");
                
                return result == 0;
            }
            catch (Exception ex)
            {
                // If we can't query, assume it's first run
                Debug.WriteLine($"IsFirstRun failed: {ex.Message}");
                return true;
            }
        }

        private void InitializeDatabase()
        {
            try
            {
                OpenConnection(_connection);
                InitializeLedger();
                ConfigureDatabasePragmas();
                Debug.WriteLine("Database initialized successfully");
            }
            finally
            {
                _connection.Close();
            }
        }

        private void InitializeLedger()
        {
            using var transaction = _connection.BeginTransaction(deferred: true);
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            int version = ReadSupportedSchemaVersion(command);

            command.CommandText = "SELECT name, sql FROM main.sqlite_schema WHERE name NOT GLOB 'sqlite_*';";
            var objects = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                    objects.Add(reader.GetString(0), NormalizeSchemaSql(reader.GetString(1)));
            }

            bool existingLedger = objects.Remove("app_usage", out var table);
            if (!existingLedger && (version != 0 || objects.Count != 0))
                throw new InvalidDataException("The database does not have a recognized Screeny ledger.");

            foreach (var (name, sql) in LedgerObjects)
            {
                if (objects.TryGetValue(name, out var definition) && definition != NormalizeSchemaSql(sql))
                    throw new InvalidDataException($"Unexpected definition for ledger object {name}.");
            }

            if (objects.Keys.Any(name => !LedgerObjects.Any(obj => name.Equals(obj.Name, StringComparison.OrdinalIgnoreCase))))
                throw new InvalidDataException("The database contains unrecognized schema objects.");

            bool backfilledDates = false;
            bool originalV0 = false;
            if (!existingLedger)
            {
                Execute("""
                    CREATE TABLE app_usage (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        date TEXT NOT NULL,
                        process_name TEXT NOT NULL,
                        app_name TEXT,
                        start_time TEXT NOT NULL,
                        end_time TEXT,
                        duration INTEGER,
                        is_focused INTEGER DEFAULT 0,
                        last_updated TEXT
                    );
                    """);
            }
            else
            {
                var (hasDate, hasV2Columns) = ReadLedgerLayout(table!);
                if ((version == 1 && !hasDate) || (version == LatestSchemaVersion && !hasV2Columns))
                    throw new InvalidDataException($"Unrecognized ledger layout for schema version {version}.");

                originalV0 = version == 0 && !hasDate && !hasV2Columns;
                if (!hasDate)
                    Execute("ALTER TABLE app_usage ADD COLUMN date TEXT;");

                command.CommandText = "SELECT 1 FROM app_usage WHERE date IS NULL LIMIT 1;";
                if (command.ExecuteScalar() != null)
                {
                    command.CommandText = """
                        SELECT 1 FROM app_usage
                        WHERE date IS NULL
                          AND (datetime(start_time) IS NULL
                               OR substr(start_time, 1, 4) = '0000'
                               OR date(substr(start_time, 1, 10), '+0 days') IS NOT substr(start_time, 1, 10))
                        LIMIT 1;
                        """;
                    if (command.ExecuteScalar() != null)
                        throw new InvalidDataException("A legacy ledger row has no usable start date.");

                    Execute("UPDATE app_usage SET date = substr(start_time, 1, 10) WHERE date IS NULL;");
                    backfilledDates = true;
                }

                command.CommandText = "SELECT 1 FROM app_usage WHERE date IS NULL OR date = '' LIMIT 1;";
                if (command.ExecuteScalar() != null)
                    throw new InvalidDataException("The ledger contains a missing date.");

                if (!hasV2Columns)
                {
                    Execute("ALTER TABLE app_usage ADD COLUMN is_focused INTEGER DEFAULT 0;");
                    Execute("ALTER TABLE app_usage ADD COLUMN last_updated TEXT;");
                }
            }

            foreach (var (name, sql) in LedgerObjects)
            {
                if (objects.ContainsKey(name))
                    continue;

                if (name == FinalizedSliceIndex && existingLedger && (!backfilledDates || originalV0))
                {
                    Execute("""
                        DELETE FROM app_usage
                        WHERE end_time IS NOT NULL
                          AND id NOT IN (
                              SELECT MIN(id)
                              FROM app_usage
                              WHERE end_time IS NOT NULL
                              GROUP BY date, process_name, start_time, end_time
                          );
                        """);
                }

                Execute(sql);
            }

            if (version != LatestSchemaVersion)
                Execute($"PRAGMA user_version = {LatestSchemaVersion};");

            transaction.Commit();

            void Execute(string sql)
            {
                command.CommandText = sql;
                command.ExecuteNonQuery();
            }
        }

        private static (bool HasDate, bool HasV2Columns) ReadLedgerLayout(string table)
        {
            const string prefix = "CREATE TABLE APP_USAGE ( ";
            const string suffix = " )";
            if (!table.StartsWith(prefix, StringComparison.Ordinal) || !table.EndsWith(suffix, StringComparison.Ordinal))
                throw new InvalidDataException("Unrecognized app_usage table definition.");

            var columns = table[prefix.Length..^suffix.Length].Split(" , ").ToHashSet();
            string[] required = ["PROCESS_NAME TEXT NOT NULL", "APP_NAME TEXT", "START_TIME TEXT NOT NULL", "END_TIME TEXT", "DURATION INTEGER"];
            bool hasId = columns.Remove("ID INTEGER PRIMARY KEY") || columns.Remove("ID INTEGER PRIMARY KEY AUTOINCREMENT");
            bool hasDate = columns.Remove("DATE TEXT") || columns.Remove("DATE TEXT NOT NULL");
            bool hasFocus = columns.Remove("IS_FOCUSED INTEGER DEFAULT 0") || columns.Remove("IS_FOCUSED INTEGER");
            bool hasLastUpdated = columns.Remove("LAST_UPDATED TEXT");
            if (!hasId || !required.All(columns.Remove) || hasFocus != hasLastUpdated || columns.Count != 0)
                throw new InvalidDataException("Unrecognized fields in the app_usage table.");

            return (hasDate, hasFocus);
        }

        private static string NormalizeSchemaSql(string sql) =>
            string.Join(" ", Regex.Matches(sql, @"[A-Za-z_][A-Za-z0-9_]*|[0-9]+|[^ \t\r\n\f]")).ToUpperInvariant();

        private static int ReadSupportedSchemaVersion(SqliteCommand command)
        {
            command.CommandText = "PRAGMA user_version;";
            int version = (int)(long)(command.ExecuteScalar() ?? throw new InvalidDataException("Missing schema version."));
            if (version < 0 || version > LatestSchemaVersion)
                throw new InvalidDataException($"Unsupported database schema version {version}.");

            return version;
        }

        private void ConfigureDatabasePragmas()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode;";
            if (string.Equals(command.ExecuteScalar() as string, JournalMode, StringComparison.OrdinalIgnoreCase))
                return;

            command.CommandText = $"PRAGMA journal_mode = {JournalMode};";
            if (!string.Equals(command.ExecuteScalar() as string, JournalMode, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Could not enable WAL for the Screeny database.");
        }

        public bool SaveSlice(UsageSlice slice)
        {
            var result = SaveSliceWithResult(slice);
            return result == PersistenceResult.Saved || result == PersistenceResult.DuplicateIgnored;
        }

        public PersistenceResult SaveSliceWithResult(UsageSlice slice)
        {
            if (!_initializedSuccessfully)
            {
                return PersistenceResult.FatalFailure;
            }

            try
            {
                using var connection = CreateConnection();
                connection.DefaultTimeout = 1;
                OpenConnection(connection, busyTimeoutMilliseconds: 0);

                const string insertSql = @"INSERT INTO app_usage (date, process_name, app_name, start_time, end_time, duration, is_focused, last_updated)
                                   VALUES (@Date, @ProcessName, @ApplicationName, @StartTime, @EndTime, @Duration, @IsFocused, @LastUpdated)
                                   ON CONFLICT(date, process_name, start_time, end_time) WHERE end_time IS NOT NULL DO NOTHING;";

                using var command = new SqliteCommand(insertSql, connection);
                command.Parameters.AddWithValue("@Date", slice.Date.ToString("yyyy-MM-dd"));
                command.Parameters.AddWithValue("@ProcessName", slice.ProcessName);
                command.Parameters.AddWithValue("@ApplicationName", slice.ApplicationName);
                command.Parameters.AddWithValue("@StartTime", slice.StartTime.ToString("o"));
                command.Parameters.AddWithValue("@EndTime", slice.EndTime.ToString("o"));
                command.Parameters.AddWithValue("@Duration", (long)slice.Duration.TotalMilliseconds);
                command.Parameters.AddWithValue("@IsFocused", 0);
                command.Parameters.AddWithValue("@LastUpdated", DateTime.UtcNow.ToString("o"));

                return command.ExecuteNonQuery() == 0 ? PersistenceResult.DuplicateIgnored : PersistenceResult.Saved;
            }
            catch (SqliteException ex) when (IsRetryableSqliteException(ex))
            {
                Debug.WriteLine($"Retryable error saving usage slice: {ex.Message}");
                return PersistenceResult.RetryableFailure;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error saving usage slice: {ex.Message}");
                return PersistenceResult.FatalFailure;
            }
        }

        private static bool IsRetryableSqliteException(SqliteException ex)
        {
            return ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6;
        }

        /// <summary>
        /// Retrieves all app usage records for a specific date
        /// </summary>
        public List<AppUsageRecord> GetRecordsForDate(DateTime date, List<AppUsageRecord>? hourlyRecords = null)
        {
            if (!_initializedSuccessfully)
            {
                Debug.WriteLine("Database not initialized, skipping get records operation");
                return new List<AppUsageRecord>();
            }

            try
            {
                var dateString = date.ToString("yyyy-MM-dd");
                Debug.WriteLine($"Getting records for date: {dateString}");
                
                using var connection = CreateConnection();
                OpenConnection(connection);
                
                using var command = connection.CreateCommand();
                {
                    command.CommandText = @"
                        SELECT id, process_name, app_name, start_time, end_time, duration, is_focused, last_updated
                        FROM app_usage
                        WHERE date = $date
                        ORDER BY process_name, start_time;"; // Order by start_time as well
                    command.Parameters.AddWithValue("$date", dateString);
                    
                    using (var reader = command.ExecuteReader())
                    {
                        // Dictionary to aggregate records with the same process name
                        var processGroups = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);
                        
                        while (reader.Read())
                        {
                            try
                            {
                                string processName = reader.GetString(reader.GetOrdinal("process_name"));
                                var dbStartTime = DateTime.Parse(reader.GetString(reader.GetOrdinal("start_time")));
                                DateTime? dbEndTime = reader.IsDBNull(reader.GetOrdinal("end_time")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("end_time")));
                                var duration = AppUsageRecord.BoundVisitDuration(TimeSpan.FromMilliseconds(
                                    !reader.IsDBNull(reader.GetOrdinal("duration")) ? reader.GetInt64(reader.GetOrdinal("duration")) : 0));
                                bool isFocused = !reader.IsDBNull(reader.GetOrdinal("is_focused")) && reader.GetInt32(reader.GetOrdinal("is_focused")) == 1;
                                DateTime? lastUpdated = !reader.IsDBNull(reader.GetOrdinal("last_updated")) ? DateTime.Parse(reader.GetString(reader.GetOrdinal("last_updated"))) : null;

                                if (hourlyRecords != null && !isFocused && dbEndTime > dbStartTime && IsHourlyProcess(processName))
                                {
                                    hourlyRecords.Add(new AppUsageRecord
                                    {
                                        Id = reader.GetInt32(reader.GetOrdinal("id")),
                                        Date = date.Date,
                                        ProcessName = processName,
                                        StartTime = dbStartTime,
                                        EndTime = dbEndTime,
                                        _accumulatedDuration = duration
                                    });
                                }

                                if (processGroups.TryGetValue(processName, out var existingRecord))
                                {
                                    // Add the duration to the existing record
                                    existingRecord._accumulatedDuration += duration;
                                    // How to handle IsFocused and LastUpdated for merged records? Take the latest?
                                    if (lastUpdated.HasValue && (!existingRecord.LastUpdated.HasValue || lastUpdated.Value > existingRecord.LastUpdated.Value))
                                    {
                                        existingRecord.LastUpdated = lastUpdated;
                                        existingRecord.IsFocused = isFocused; // Assume focus state from the latest update within the group
                                    }
                                    if (dbStartTime < existingRecord.StartTime) existingRecord.StartTime = dbStartTime;
                                    if (!dbEndTime.HasValue || dbEndTime > existingRecord.EndTime)
                                        existingRecord.EndTime = dbEndTime;


                                    Debug.WriteLine($"Merged record: {processName}, total duration now: {existingRecord.Duration.TotalSeconds:F1}s, IsFocused: {existingRecord.IsFocused}");
                                }
                                else
                                {
                                    // Create a new record
                                    var record = new AppUsageRecord
                                    {
                                        Id = reader.GetInt32(reader.GetOrdinal("id")),
                                        IsAggregate = true,
                                        ProcessName = processName,
                                        ApplicationName = !reader.IsDBNull(reader.GetOrdinal("app_name")) ? reader.GetString(reader.GetOrdinal("app_name")) : processName,
                                        Date = date, // Use date for the Date field
                                        StartTime = dbStartTime, // Use actual start time from database
                                        EndTime = dbEndTime,
                                        IsFocused = isFocused,
                                        LastUpdated = lastUpdated
                                    };
                                    
                                    record._accumulatedDuration = duration;
                                    
                                    // Add to our dictionary
                                    processGroups[processName] = record;
                                    Debug.WriteLine($"Added new record: {processName}, Duration: {record.Duration.TotalSeconds:F1}s, IsFocused: {record.IsFocused}");
                                }
                            }
                            catch (Exception parseEx)
                            {
                                Debug.WriteLine($"Error parsing record in GetRecordsForDate: {parseEx.Message}");
                                // Continue to next record
                            }
                        }
                        
                        // Convert dictionary values to our final list
                        return processGroups.Values.ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting records for date: {ex.Message}");
                return new List<AppUsageRecord>();
            }
        }


        /// <summary>
        /// Gets usage data for a date range, for generating reports
        /// </summary>
        public List<(string ProcessName, TimeSpan TotalDuration)> GetUsageReportForDateRange(DateTime startDate, DateTime endDate)
        {
            if (!_initializedSuccessfully)
            {
                Debug.WriteLine("Database not initialized, skipping get usage report operation");
                return new List<(string, TimeSpan)>();
            }

            var results = new List<(string, TimeSpan)>();
            string startDateString = startDate.ToString("yyyy-MM-dd");
            string endDateString   = endDate.ToString("yyyy-MM-dd");

            try
            {
                using var connection = CreateConnection();
                OpenConnection(connection);
                const string sql = @"
                    SELECT
                        process_name,
                        SUM(MIN(duration, @MaxVisitDurationMs)) AS total_ms
                    FROM app_usage
                    WHERE date >= @StartDate AND date <= @EndDate
                    GROUP BY process_name;";

                using (var cmd = new SqliteCommand(sql, connection))
                {
                    cmd.Parameters.AddWithValue("@MaxVisitDurationMs", (long)AppUsageRecord.MaxVisitDuration.TotalMilliseconds);
                    cmd.Parameters.AddWithValue("@StartDate", startDateString);
                    cmd.Parameters.AddWithValue("@EndDate",   endDateString);

                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        var processName = reader.GetString(0);
                        long totalMs    = reader.GetInt64(1);
                        results.Add((processName, TimeSpan.FromMilliseconds(totalMs)));
                    }
                }

                return results.OrderByDescending(r => r.Item2).ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error retrieving usage report: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Cleans up expired records (optional feature for data retention policy)
        /// </summary>
        public void CleanupExpiredRecords(int daysToKeep)
        {
            if (!_initializedSuccessfully)
            {
                Debug.WriteLine("Database not initialized, skipping cleanup operation");
                return;
            }

            SqliteTransaction? transaction = null;
            try
            {
                DateTime cutoffDate = DateTime.Now.AddDays(-daysToKeep).Date;
                string cutoffDateString = cutoffDate.ToString("yyyy-MM-dd");

                using var connection = CreateConnection();
                OpenConnection(connection);
                transaction = connection.BeginTransaction();

                string deleteSql = @"DELETE FROM app_usage WHERE date < @CutoffDate;";
                int rowsDeleted = 0;

                using (var command = new SqliteCommand(deleteSql, connection, transaction))
                {
                    command.Parameters.AddWithValue("@CutoffDate", cutoffDateString);
                    rowsDeleted = command.ExecuteNonQuery();
                }

                transaction.Commit(); // Commit transaction

                if (rowsDeleted > 0)
                {
                    using var vacuumCommand = connection.CreateCommand();
                    vacuumCommand.CommandText = "VACUUM;";
                    vacuumCommand.ExecuteNonQuery();
                }
            }
            catch (Exception)
            {
                try
                {
                    transaction?.Rollback(); // Rollback transaction on error
                }
                catch (Exception)
                {
                }
            }
        }

        /// <summary>
        /// Performs database maintenance and integrity checks
        /// </summary>
        /// <returns>True if the database passes integrity checks</returns>
        public bool PerformDatabaseMaintenance()
        {
            if (!_initializedSuccessfully)
            {
                Debug.WriteLine("Database not initialized, skipping maintenance");
                return false;
            }
            
            try
            {
                using var connection = CreateConnection();
                OpenConnection(connection);
                bool integrityPassed = true;
                
                // Run integrity check
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA integrity_check;";
                    var resultObj = command.ExecuteScalar();
                    string? result = resultObj as string;
                    integrityPassed = result != null && result.Equals("ok", StringComparison.OrdinalIgnoreCase);
                    Debug.WriteLine($"Database integrity check: {result ?? "null"}");
                }
                
                // If check failed, try to recover
                if (!integrityPassed)
                {
                    Debug.WriteLine("Database integrity check failed, attempting recovery");
                    // Integrity check failed, we should try to repair if possible
                    // For SQLite, often the best approach is to make a new copy
                    // But for simplicity, we'll just run basic optimization commands
                }
                
                // Optimize the database
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA optimize;";
                    command.ExecuteNonQuery();
                    Debug.WriteLine("Database optimized");
                }
                
                // Analyze the database to improve query performance
                using (var command = connection.CreateCommand())
                {
                    command.CommandText = "ANALYZE;";
                    command.ExecuteNonQuery();
                    Debug.WriteLine("Database analyzed");
                }
                
                return integrityPassed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during database maintenance: {ex.Message}");
                return false;
            }
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    _connection?.Dispose();
                }
                _disposed = true;
            }
        }

        public List<AppUsageRecord> GetRawRecordsForDateRange(DateTime startDate, DateTime endDate)
            => ReadRawRecordsForDateRange(startDate, endDate) ?? new List<AppUsageRecord>();

        private List<AppUsageRecord>? ReadRawRecordsForDateRange(DateTime startDate, DateTime endDate)
        {
            try
            {
                // Ensure the database is initialized
                if (!IsDatabaseInitialized())
                {
                    System.Diagnostics.Debug.WriteLine($"ERROR: Database not initialized in GetRawRecordsForDateRange");
                    return null;
                }

                // Convert dates to the format stored in the database (YYYY-MM-DD)
                string startDateStr = startDate.ToString("yyyy-MM-dd");
                string endDateStr = endDate.ToString("yyyy-MM-dd");

                // Create SQL to get all records for the date range
                string sql = @"
                    SELECT id, process_name, app_name, start_time, end_time, duration, is_focused, last_updated, date
                    FROM app_usage
                    WHERE date >= @StartDate AND date <= @EndDate
                    ORDER BY date ASC, start_time ASC, process_name ASC;";

                List<AppUsageRecord> records = new List<AppUsageRecord>();

                // Use a new connection for safety, though the class member _connection should be managed (opened/closed) per method.
                using (var connection = CreateConnection())
                {
                    OpenConnection(connection);
                    using (var command = connection.CreateCommand())
                    {
                        command.CommandText = sql;
                        command.Parameters.AddWithValue("@StartDate", startDateStr);
                        command.Parameters.AddWithValue("@EndDate", endDateStr);

                        using (var reader = command.ExecuteReader())
                        {
                            while (reader.Read())
                            {
                                try
                                {
                                    var record = new AppUsageRecord
                                    {
                                        Id = reader.GetInt32(reader.GetOrdinal("id")),
                                        ProcessName = reader.GetString(reader.GetOrdinal("process_name")),
                                        ApplicationName = !reader.IsDBNull(reader.GetOrdinal("app_name")) ? reader.GetString(reader.GetOrdinal("app_name")) : reader.GetString(reader.GetOrdinal("process_name")),
                                        StartTime = DateTime.Parse(reader.GetString(reader.GetOrdinal("start_time"))),
                                        EndTime = !reader.IsDBNull(reader.GetOrdinal("end_time")) ? DateTime.Parse(reader.GetString(reader.GetOrdinal("end_time"))) : (DateTime?)null,
                                        _accumulatedDuration = TimeSpan.FromMilliseconds(!reader.IsDBNull(reader.GetOrdinal("duration")) ? reader.GetInt64(reader.GetOrdinal("duration")) : 0),
                                        IsFocused = !reader.IsDBNull(reader.GetOrdinal("is_focused")) && reader.GetInt32(reader.GetOrdinal("is_focused")) == 1,
                                        LastUpdated = !reader.IsDBNull(reader.GetOrdinal("last_updated")) ? DateTime.Parse(reader.GetString(reader.GetOrdinal("last_updated"))) : (DateTime?)null,
                                        Date = DateTime.Parse(reader.GetString(reader.GetOrdinal("date")))
                                    };
                                    records.Add(record);
                                }
                                catch (Exception ex)
                                {
                                    System.Diagnostics.Debug.WriteLine($"Error parsing record in GetRawRecordsForDateRange: {ex.Message} on row ID {reader.GetInt32(reader.GetOrdinal("id"))}");
                                    // Optionally skip this record and continue
                                }
                            }
                        }
                    }
                }

                return records;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error in GetRawRecordsForDateRange: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                return null;
            }
        }

        public List<AppUsageRecord> GetRecordsForDateRange(DateTime startDate, DateTime endDate)
        {
            // Reuse existing raw retrieval method. Caller is responsible for any aggregation.
            return GetRawRecordsForDateRange(startDate, endDate);
        }

        public bool WipeDatabase()
        {
            if (!_initializedSuccessfully)
            {
                return false;
            }

            bool deleteCommitted = false;
            try
            {
                using var connection = CreateConnection();
                OpenConnection(connection);

                using (var tx = connection.BeginTransaction())
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM app_usage;";
                    cmd.ExecuteNonQuery();
                    tx.Commit();
                    deleteCommitted = true;
                }

                using var vacuumCmd = connection.CreateCommand();
                vacuumCmd.CommandText = "VACUUM;";
                vacuumCmd.ExecuteNonQuery();
            }
            catch (Exception ex)
            {
                Debug.WriteLine(deleteCommitted
                    ? $"Usage data deleted, but database compaction failed: {ex.Message}"
                    : $"Error deleting usage data: {ex.Message}");
            }

            return deleteCommitted;
        }

#if !UNIT_TEST
        /// <summary>
        /// Gets aggregated records for date range, optionally including live tracking data
        /// (Replaces UsageAggregationService.GetAggregatedRecordsForDateRange)
        /// </summary>
        public List<AppUsageRecord> GetAggregatedRecordsWithLive(DateTime startDate, DateTime endDate, WindowTrackingService? trackingService, bool includeLiveRecords = true)
        {
            if (startDate > endDate) (startDate, endDate) = (endDate, startDate);

            var aggregated = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);

            // Get database records and convert to AppUsageRecord format
            var dbReport = GetUsageReportForDateRange(startDate, endDate);
            var dbRecords = dbReport.Select(pair => new AppUsageRecord
            {
                ProcessName = pair.ProcessName,
                IsAggregate = true,
                _accumulatedDuration = pair.TotalDuration,
                Date = startDate,
                StartTime = startDate
            });

            // Aggregate database records
            var dbAggregated = AggregateRecords(dbRecords, startDate);
            foreach (var (processName, record) in dbAggregated)
            {
                aggregated[processName] = record;
            }

            // Merge live records if requested
            if (includeLiveRecords && trackingService != null)
            {
                MergeLiveRecords(aggregated, trackingService, startDate, endDate);
            }

            return aggregated.Values
                .Where(r => !Models.ProcessFilter.ShouldIgnoreProcess(r.ProcessName) && 
                       !r.ProcessName.Equals(System.Diagnostics.Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Duration.TotalSeconds)
                .ToList();
        }

        /// <summary>
        /// Gets detailed records for a date, optionally including live data
        /// (Replaces UsageAggregationService.GetDetailRecordsForDate)
        /// </summary>
        public List<AppUsageRecord> GetDetailRecordsWithLive(DateTime date, WindowTrackingService? trackingService, List<AppUsageRecord>? hourlyRecords = null)
        {
            var liveRecords = hourlyRecords == null ? null : GetLiveRecordsForDay(date, trackingService);
            var dbRecords = GetRecordsForDate(date, hourlyRecords);
            var aggregated = AggregateRecords(dbRecords, date);

            if (hourlyRecords == null)
                liveRecords = GetLiveRecordsForDay(date, trackingService);
            else if (liveRecords != null)
                liveRecords = liveRecords.Where(r => !HasFinalizedRecord(hourlyRecords, r)).ToList();

            if (liveRecords != null)
            {
                var liveAggregated = AggregateRecords(liveRecords, date);

                if (hourlyRecords != null)
                    AddHourlyLiveRecords(hourlyRecords, liveRecords);
                
                foreach (var (processName, liveRecord) in liveAggregated)
                {
                    if (aggregated.TryGetValue(processName, out var existing))
                    {
                        existing._accumulatedDuration += liveRecord.Duration;
                        existing.WindowHandle = liveRecord.WindowHandle;
                        if (!string.IsNullOrEmpty(liveRecord.WindowTitle)) existing.WindowTitle = liveRecord.WindowTitle;
                    }
                    else
                    {
                        aggregated[processName] = liveRecord;
                    }
                }
            }

            return aggregated.Values.OrderByDescending(r => r.Duration.TotalSeconds).ToList();
        }

        public List<AppUsageRecord>? GetHourlyRecordsWithLive(DateTime date, WindowTrackingService? trackingService)
            => GetDashboardRecordsWithLive(date, date, trackingService);

        public List<AppUsageRecord>? GetDashboardRecordsWithLive(DateTime start, DateTime end, WindowTrackingService? trackingService)
        {
            var today = DateTime.Today;
            var liveRecords = start.Date <= today && today <= end.Date ? GetLiveRecordsForDay(today, trackingService) : null;
            var records = ReadRawRecordsForDateRange(start, end);
            if (records == null) return null;
            records.RemoveAll(r => r.IsFocused || !(r.EndTime > r.StartTime) || !IsHourlyProcess(r.ProcessName));

            if (liveRecords != null)
                AddHourlyLiveRecords(records, liveRecords.Where(r => !HasFinalizedRecord(records, r)));

            return records;
        }

        private static IEnumerable<AppUsageRecord>? GetLiveRecordsForDay(DateTime date, WindowTrackingService? trackingService)
            => date.Date == DateTime.Today && trackingService != null
                ? trackingService.GetRecords().Where(r => r.IsFromDate(date))
                : null;

        private static bool HasFinalizedRecord(List<AppUsageRecord> records, AppUsageRecord liveRecord)
            => records.Any(r => r.StartTime == liveRecord.StartTime &&
                string.Equals(r.ProcessName, liveRecord.ProcessName, StringComparison.OrdinalIgnoreCase));

        private static void AddHourlyLiveRecords(List<AppUsageRecord> records, IEnumerable<AppUsageRecord> liveRecords)
        {
            foreach (var record in liveRecords)
            {
                var duration = record.Duration;
                if ((record.EndTime == null && duration.TotalSeconds < 5) || !IsHourlyProcess(record.ProcessName)) continue;

                record.EndTime ??= record.StartTime + duration;
                records.Add(record);
            }
        }

        #region Helper Methods

        /// <summary>
        /// Merges live tracking records into existing aggregated records dictionary
        /// </summary>
        private static void MergeLiveRecords(Dictionary<string, AppUsageRecord> aggregated, WindowTrackingService trackingService, DateTime startDate, DateTime endDate)
        {
            if (trackingService == null || endDate.Date < DateTime.Today) return;

            var liveRecords = trackingService.GetRecords()
                .Where(r => r.Date >= startDate && r.Date <= endDate && r.Duration.TotalSeconds > 0)
                .ToList();

            foreach (var liveRec in liveRecords)
            {
                var temp = new AppUsageRecord { ProcessName = liveRec.ProcessName, WindowTitle = liveRec.WindowTitle };
                Helpers.ApplicationProcessingHelper.ProcessApplicationRecord(temp);
                var processName = temp.ProcessName;

                if (aggregated.TryGetValue(processName, out var existing))
                {
                    existing._accumulatedDuration += liveRec.Duration;
                    existing.WindowHandle = liveRec.WindowHandle;
                    if (!string.IsNullOrEmpty(liveRec.WindowTitle)) existing.WindowTitle = liveRec.WindowTitle;
                    if (liveRec.StartTime < existing.StartTime) existing.StartTime = liveRec.StartTime;
                }
                else
                {
                    var newRecord = new AppUsageRecord
                    {
                        ProcessName = processName,
                        IsAggregate = true,
                        ApplicationName = processName,
                        _accumulatedDuration = liveRec.Duration,
                        Date = liveRec.Date,
                        StartTime = liveRec.StartTime,
                        EndTime = liveRec.EndTime,
                        WindowHandle = liveRec.WindowHandle,
                        WindowTitle = liveRec.WindowTitle
                    };
                    aggregated[processName] = newRecord;
                }
            }
        }

        /// <summary>
        /// Aggregates records by process name, merging durations and handling duplicates
        /// </summary>
        private static Dictionary<string, AppUsageRecord> AggregateRecords(IEnumerable<AppUsageRecord> records, DateTime? fallbackDate = null)
        {
            var aggregated = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);

            foreach (var rec in records)
            {
                if (rec.Duration.TotalSeconds < 5) continue;
                
                var temp = new AppUsageRecord { ProcessName = rec.ProcessName, WindowTitle = rec.WindowTitle };
                Helpers.ApplicationProcessingHelper.ProcessApplicationRecord(temp);
                var processName = temp.ProcessName;

                if (Models.ProcessFilter.ShouldIgnoreProcess(processName) ||
                    processName.Equals(System.Diagnostics.Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (aggregated.TryGetValue(processName, out var existing))
                {
                    existing._accumulatedDuration += rec.Duration;
                    if (rec.StartTime < existing.StartTime) existing.StartTime = rec.StartTime;
                    if (rec.EndTime.HasValue)
                    {
                        if (!existing.EndTime.HasValue || rec.EndTime > existing.EndTime) existing.EndTime = rec.EndTime;
                    }
                }
                else
                {
                    var newRecord = new AppUsageRecord
                    {
                        ProcessName = processName,
                        IsAggregate = true,
                        ApplicationName = processName,
                        _accumulatedDuration = rec.Duration,
                        Date = rec.Date != default ? rec.Date : (fallbackDate ?? DateTime.Today),
                        StartTime = rec.StartTime,
                        EndTime = rec.EndTime,
                        WindowTitle = rec.WindowTitle
                    };
                    aggregated[processName] = newRecord;
                }
            }

            return aggregated;
        }

        #endregion
#endif

        private static bool IsHourlyProcess(string processName)
            => !ProcessFilter.ShouldIgnoreProcess(Helpers.ApplicationNameNormalizer.NormalizeProcessName(processName));

    }
} 
