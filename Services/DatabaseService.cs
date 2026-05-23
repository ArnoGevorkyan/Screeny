using Microsoft.Data.Sqlite;
using ScreenTimeTracker.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SQLitePCL;
using System.Diagnostics;
using System.Linq;

namespace ScreenTimeTracker.Services
{
    public class DatabaseService : IDisposable
    {
        private const int LatestSchemaVersion = 2;
        private const int BusyTimeoutMilliseconds = 5000;
        private const string JournalMode = "WAL";
        private readonly string _dbPath;
        private static string DefaultDatabasePath => Path.Combine(
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

                // Check integrity BEFORE opening connection
                if (File.Exists(_dbPath) && !CheckDatabaseIntegrity(_dbPath))
                {
                    // Handle corruption (e.g., backup and create new)
                    HandleCorruptedDatabase(_dbPath);
                    // After handling, initialization will create a new DB file
                }

                var connectionString = CreateConnectionString(_dbPath);
                Debug.WriteLine($"Using connection string: {connectionString}");

                _connection = new SqliteConnection(connectionString);
                InitializeDatabase(); // Includes Open, Schema, Migrations, Close
                
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

        // Helper to create connection string
        private string CreateConnectionString(string dataSource)
        {
             return new SqliteConnectionStringBuilder
             {
                 DataSource = dataSource,
                 Mode = SqliteOpenMode.ReadWriteCreate,
                 Cache = SqliteCacheMode.Shared,
                 Pooling = true // Pooling can be beneficial
             }.ToString();
        }

        private SqliteConnection CreateConnection()
        {
            return new SqliteConnection(_connection.ConnectionString);
        }

        private static void OpenConnection(SqliteConnection connection)
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
            command.ExecuteNonQuery();
        }

        // New method to check DB integrity using PRAGMA
        private bool CheckDatabaseIntegrity(string dbPath)
        {
            Debug.WriteLine($"Checking database integrity for: {dbPath}");
            if (!File.Exists(dbPath)) return true; // No file, considered intact

            // Use a temporary connection for the check
            var checkConnectionString = new SqliteConnectionStringBuilder(CreateConnectionString(dbPath))
            {
                Pooling = false
            }.ToString();
            using (var checkConnection = new SqliteConnection(checkConnectionString))
            {
                try
                {
                    OpenConnection(checkConnection);
                    using (var command = checkConnection.CreateCommand())
                    {
                        command.CommandText = "PRAGMA integrity_check;";
                        var result = command.ExecuteScalar()?.ToString();
                        Debug.WriteLine($"PRAGMA integrity_check result: {result}");
                        // Integrity check returns "ok" if the database is valid
                        return result != null && result.Equals("ok", StringComparison.OrdinalIgnoreCase);
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"Error during integrity check: {ex.Message}");
                    SqliteConnection.ClearPool(checkConnection);
                    return false; // Assume corruption if check fails
                }
                // No need for finally block, 'using' handles disposal/closing
            }
        }

        // New method to handle corrupted database
        private void HandleCorruptedDatabase(string dbPath)
        {
            string backupPath = dbPath + ".corrupt." + DateTime.Now.ToString("yyyyMMddHHmmss");
            Debug.WriteLine($"Database integrity check failed! Moving corrupted file to: {backupPath}");
            try
            {
                SqliteConnection.ClearAllPools();
                File.Move(dbPath, backupPath);
                Debug.WriteLine("Successfully moved corrupted database file.");
                // Optionally: Notify user about the corruption and reset
            }
            catch (IOException ioEx)
            {
                 Debug.WriteLine($"Error moving corrupted database file (IOException): {ioEx.Message}");
                 // Attempt to delete if move failed (e.g., file locked)
                 try
                 {
                     File.Delete(dbPath);
                     Debug.WriteLine("Successfully deleted corrupted database file after move failed.");
                 }
                 catch (Exception delEx)
                 {
                     Debug.WriteLine($"CRITICAL ERROR: Failed to move AND delete corrupted database file: {delEx.Message}");
                     // At this point, initialization will fail into degraded mode.
                 }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CRITICAL ERROR: Failed to move corrupted database file: {ex.Message}");
                // Initialization might fail into degraded mode.
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
                // Open connection
                OpenConnection(_connection);
                ConfigureDatabasePragmas();

                // Create schema
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = @"
                    CREATE TABLE IF NOT EXISTS app_usage (
                        id INTEGER PRIMARY KEY AUTOINCREMENT,
                        date TEXT NOT NULL,
                        process_name TEXT NOT NULL,
                        app_name TEXT,
                        start_time TEXT NOT NULL,
                        end_time TEXT,
                        duration INTEGER,
                        is_focused INTEGER DEFAULT 0,
                        last_updated TEXT 
                    );";
                    command.ExecuteNonQuery();
                }

                // Check if we need to perform any migrations
                CheckAndPerformMigrations();

                if (HasLatestSchema())
                {
                    SetDatabaseVersion(LatestSchemaVersion);
                }

                // Create indices for better performance after migrations have ensured date exists.
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "CREATE INDEX IF NOT EXISTS idx_app_usage_date ON app_usage(date);";
                    command.ExecuteNonQuery();
                }

                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "CREATE INDEX IF NOT EXISTS idx_app_usage_process_name ON app_usage(process_name);";
                    command.ExecuteNonQuery();
                }

                CreateFinalizedSliceUniqueIndex();

                // Create aggregated VIEW for per-day per-process totals after migrations have ensured date exists.
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = @"
                        CREATE VIEW IF NOT EXISTS vw_daily_app_usage AS
                        SELECT
                            date,
                            process_name,
                            SUM(duration) AS total_duration_ms
                        FROM app_usage
                        GROUP BY date, process_name;";
                    command.ExecuteNonQuery();
                }

                Debug.WriteLine("Database initialized successfully");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error initializing database: {ex.Message}");
                throw; // Rethrow to be caught in constructor
            }
            finally
            {
                // Make sure connection is closed
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                }
            }
        }

        /// <summary>
        /// Checks for and performs any necessary database migrations
        /// </summary>
        private void CheckAndPerformMigrations()
        {
            try
            {
                // Get current database version or schema information
                int currentVersion = GetDatabaseVersion();
                Debug.WriteLine($"Current database version: {currentVersion}");

                // Perform migrations based on version
                if (currentVersion < 1)
                {
                    // Migration to version 1 - Add date column if needed
                    MigrateToV1();
                    currentVersion = GetDatabaseVersion(); // Re-fetch version after V1 migration
                }

                if (currentVersion < 2)
                {
                    MigrateToV2();
                }

                // Future migrations can be added here
                // if (currentVersion < 2) { MigrateToV2(); }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during database migration: {ex.Message}");
                // We'll continue even if migration fails - the app can still work
            }
        }

        /// <summary>
        /// Retrieves the current database version from the pragma
        /// </summary>
        private int GetDatabaseVersion()
        {
            try
            {
                // First try to get version from user_version pragma
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA user_version;";
                    var result = command.ExecuteScalar();
                    return result != null ? Convert.ToInt32(result) : 0;
                }
            }
            catch
            {
                // If there's any error, assume version 0
                return 0;
            }
        }

        private void SetDatabaseVersion(int version)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"PRAGMA user_version = {version};";
            command.ExecuteNonQuery();
        }

        private void ConfigureDatabasePragmas()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"PRAGMA journal_mode = {JournalMode};";
            command.ExecuteScalar();
        }

        private void CreateFinalizedSliceUniqueIndex()
        {
            RemoveExactDuplicateFinalizedSlices();

            using var command = _connection.CreateCommand();
            command.CommandText = @"
                CREATE UNIQUE INDEX IF NOT EXISTS idx_app_usage_finalized_slice_unique
                ON app_usage(date, process_name, start_time, end_time)
                WHERE end_time IS NOT NULL;";
            command.ExecuteNonQuery();
        }

        private void RemoveExactDuplicateFinalizedSlices()
        {
            using var command = _connection.CreateCommand();
            command.CommandText = @"
                DELETE FROM app_usage
                WHERE end_time IS NOT NULL
                  AND id NOT IN (
                      SELECT MIN(id)
                      FROM app_usage
                      WHERE end_time IS NOT NULL
                      GROUP BY date, process_name, start_time, end_time
                  );";
            command.ExecuteNonQuery();
        }

        private bool ColumnExists(string tableName, string columnName)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var currentColumnName = reader.GetString(1);
                if (currentColumnName.Equals(columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private bool HasLatestSchema()
        {
            return ColumnExists("app_usage", "date")
                && ColumnExists("app_usage", "is_focused")
                && ColumnExists("app_usage", "last_updated");
        }

        /// <summary>
        /// Migrates database to version 1 
        /// Ensures date column exists and is populated
        /// </summary>
        private void MigrateToV1()
        {
            Debug.WriteLine("Performing migration to version 1");
            
            try
            {
                bool hasDateColumn = ColumnExists("app_usage", "date");

                // If no date column, add it and populate from start_time
                if (!hasDateColumn)
                {
                    Debug.WriteLine("Adding date column to app_usage table");
                    
                    // Begin transaction
                    using (var transaction = _connection.BeginTransaction())
                    {
                        try
                        {
                            // Add date column
                            using (var command = _connection.CreateCommand())
                            {
                                command.Transaction = transaction;
                                command.CommandText = "ALTER TABLE app_usage ADD COLUMN date TEXT;";
                                command.ExecuteNonQuery();
                            }
                            
                            // Update existing records to extract date from start_time
                            using (var command = _connection.CreateCommand())
                            {
                                command.Transaction = transaction;
                                command.CommandText = @"
                                    UPDATE app_usage 
                                    SET date = substr(start_time, 1, 10) 
                                    WHERE date IS NULL;";
                                command.ExecuteNonQuery();
                            }
                            
                            // Commit the transaction
                            transaction.Commit();
                            Debug.WriteLine("Migration completed successfully");
                        }
                        catch (Exception ex)
                        {
                            // Rollback on error
                            transaction.Rollback();
                            Debug.WriteLine($"Migration failed: {ex.Message}");
                            throw;
                        }
                    }
                    
                    // Create index on the new column
                    using (var command = _connection.CreateCommand())
                    {
                        command.CommandText = "CREATE INDEX IF NOT EXISTS idx_app_usage_date ON app_usage(date);";
                        command.ExecuteNonQuery();
                    }
                }
                
                // Update version in database
                SetDatabaseVersion(1);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during migration to V1: {ex.Message}");
                // We'll continue even if migration fails
            }
        }

        /// <summary>
        /// Migrates database to version 2
        /// Adds is_focused and last_updated columns
        /// </summary>
        private void MigrateToV2()
        {
            Debug.WriteLine("Performing migration to version 2");
            try
            {
                bool hasIsFocusedColumn = ColumnExists("app_usage", "is_focused");
                bool hasLastUpdatedColumn = ColumnExists("app_usage", "last_updated");

                // Begin transaction
                using (var transaction = _connection.BeginTransaction())
                {
                    try
                    {
                        if (!hasIsFocusedColumn)
                        {
                            using var command = _connection.CreateCommand();
                            command.Transaction = transaction;
                            command.CommandText = "ALTER TABLE app_usage ADD COLUMN is_focused INTEGER DEFAULT 0;";
                            command.ExecuteNonQuery();
                            Debug.WriteLine("Added is_focused column to app_usage table.");
                        }

                        if (!hasLastUpdatedColumn)
                        {
                            using var command = _connection.CreateCommand();
                            command.Transaction = transaction;
                            command.CommandText = "ALTER TABLE app_usage ADD COLUMN last_updated TEXT;";
                            command.ExecuteNonQuery();
                            Debug.WriteLine("Added last_updated column to app_usage table.");
                        }

                        // Commit the transaction
                        transaction.Commit();
                        Debug.WriteLine("Migration to V2 (columns) completed successfully.");
                    }
                    catch (Exception ex)
                    {
                        // Rollback on error
                        transaction.Rollback();
                        Debug.WriteLine($"Migration to V2 (columns) failed: {ex.Message}");
                        // Decide if to rethrow or not. For schema changes, it might be safer to inform and potentially stop.
                        // For now, we log and continue, but this might leave the DB in an inconsistent state for V2.
                        return; // Exit if column creation failed
                    }
                }

                // Update version in database
                SetDatabaseVersion(2);
                Debug.WriteLine("Database user_version set to 2.");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during migration to V2: {ex.Message}");
            }
        }

        /// <summary>
        /// Validates a date to ensure it's not in the future
        /// </summary>
        private DateTime ValidateDate(DateTime date)
        {
            if (date > DateTime.Now)
            {
                Debug.WriteLine($"WARNING: Future date detected ({date:yyyy-MM-dd}), using current date instead.");
                return DateTime.Now;
            }
            return date;
        }

        /// <summary>
        /// Validates and prepares duration and date data for database operations
        /// </summary>
        private (DateTime validatedStartTime, DateTime? validatedEndTime, string dateString, long durationMs) 
            ValidateRecordData(AppUsageRecord record)
        {
            DateTime validatedStartTime = ValidateDate(record.StartTime);
            DateTime? validatedEndTime = record.EndTime.HasValue ? ValidateDate(record.EndTime.Value) : null;
            string dateString = validatedStartTime.ToString("yyyy-MM-dd");

            long durationMs = (long)record.Duration.TotalMilliseconds;
            
            // Cap duration at 8 hours for a single session
            const long maxSessionDurationMs = 8 * 60 * 60 * 1000;
            if (durationMs > maxSessionDurationMs) durationMs = maxSessionDurationMs;
            if (durationMs < 0) durationMs = 0;
            
            // Recalculate from actual times if available
            if (validatedEndTime.HasValue)
            {
                var calculatedDuration = (validatedEndTime.Value - validatedStartTime).TotalMilliseconds;
                if (calculatedDuration >= 0 && calculatedDuration < durationMs)
                {
                    durationMs = (long)calculatedDuration;
                }
            }

            return (validatedStartTime, validatedEndTime, dateString, durationMs);
        }

        /// <summary>
        /// Saves an app usage record to the database
        /// </summary>
        public bool SaveRecord(AppUsageRecord record)
        {
            if (!_initializedSuccessfully)
            {
                return false;
            }

            try
            {
                var (validatedStartTime, validatedEndTime, dateString, durationMs) = ValidateRecordData(record);

                // Use connection-per-operation pattern
                using var connection = CreateConnection();
                OpenConnection(connection);
                using var transaction = connection.BeginTransaction();
                
                string insertSql = @"INSERT INTO app_usage (date, process_name, app_name, start_time, end_time, duration, is_focused, last_updated) 
                                   VALUES (@Date, @ProcessName, @ApplicationName, @StartTime, @EndTime, @Duration, @IsFocused, @LastUpdated); 
                                   SELECT last_insert_rowid();";

                using var command = new SqliteCommand(insertSql, connection, transaction);
                command.Parameters.AddWithValue("@Date", dateString);
                command.Parameters.AddWithValue("@ProcessName", record.ProcessName);
                command.Parameters.AddWithValue("@ApplicationName", record.ApplicationName ?? "");
                command.Parameters.AddWithValue("@StartTime", validatedStartTime.ToString("o"));
                command.Parameters.AddWithValue("@EndTime", validatedEndTime?.ToString("o") ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@Duration", durationMs);
                command.Parameters.AddWithValue("@IsFocused", record.IsFocused ? 1 : 0);
                command.Parameters.AddWithValue("@LastUpdated", DateTime.UtcNow.ToString("o"));
                
                var result = command.ExecuteScalar();
                
                if (result != null)
                {
                    record.Id = (int)Convert.ToInt64(result);
                    transaction.Commit();
                    return true;
                }
                
                return false;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving record: {ex.Message}");
                return false;
            }
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
                OpenConnection(connection);
                using var transaction = connection.BeginTransaction();

                const string duplicateSql = @"SELECT 1
                                   FROM app_usage
                                   WHERE date = @Date
                                     AND process_name = @ProcessName
                                     AND start_time = @StartTime
                                     AND end_time = @EndTime
                                   LIMIT 1;";

                using (var duplicateCommand = new SqliteCommand(duplicateSql, connection, transaction))
                {
                    duplicateCommand.Parameters.AddWithValue("@Date", slice.Date.ToString("yyyy-MM-dd"));
                    duplicateCommand.Parameters.AddWithValue("@ProcessName", slice.ProcessName);
                    duplicateCommand.Parameters.AddWithValue("@StartTime", slice.StartTime.ToString("o"));
                    duplicateCommand.Parameters.AddWithValue("@EndTime", slice.EndTime.ToString("o"));

                    if (duplicateCommand.ExecuteScalar() != null)
                    {
                        transaction.Commit();
                        return PersistenceResult.DuplicateIgnored;
                    }
                }

                const string insertSql = @"INSERT INTO app_usage (date, process_name, app_name, start_time, end_time, duration, is_focused, last_updated)
                                   VALUES (@Date, @ProcessName, @ApplicationName, @StartTime, @EndTime, @Duration, @IsFocused, @LastUpdated);";

                using var command = new SqliteCommand(insertSql, connection, transaction);
                command.Parameters.AddWithValue("@Date", slice.Date.ToString("yyyy-MM-dd"));
                command.Parameters.AddWithValue("@ProcessName", slice.ProcessName);
                command.Parameters.AddWithValue("@ApplicationName", slice.ApplicationName);
                command.Parameters.AddWithValue("@StartTime", slice.StartTime.ToString("o"));
                command.Parameters.AddWithValue("@EndTime", slice.EndTime.ToString("o"));
                command.Parameters.AddWithValue("@Duration", (long)slice.Duration.TotalMilliseconds);
                command.Parameters.AddWithValue("@IsFocused", 0);
                command.Parameters.AddWithValue("@LastUpdated", DateTime.UtcNow.ToString("o"));

                command.ExecuteNonQuery();
                transaction.Commit();
                return PersistenceResult.Saved;
            }
            catch (SqliteException ex) when (IsConstraintSqliteException(ex))
            {
                Debug.WriteLine($"Duplicate usage slice ignored: {ex.Message}");
                return PersistenceResult.DuplicateIgnored;
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

        private static bool IsConstraintSqliteException(SqliteException ex)
        {
            return ex.SqliteErrorCode == 19;
        }

        /// <summary>
        /// Updates an existing app usage record in the database
        /// </summary>
        public void UpdateRecord(AppUsageRecord record)
        {
            if (!_initializedSuccessfully)
            {
                return;
            }

            try
            {
                var (validatedStartTime, validatedEndTime, dateString, durationMs) = ValidateRecordData(record);

                using var connection = CreateConnection();
                OpenConnection(connection);
                using var transaction = connection.BeginTransaction();
                
                string updateSql = @"UPDATE app_usage SET date = @Date, process_name = @ProcessName, app_name = @ApplicationName, 
                                   end_time = @EndTime, duration = @Duration, is_focused = @IsFocused, last_updated = @LastUpdated 
                                   WHERE id = @Id;";

                using var command = new SqliteCommand(updateSql, connection, transaction);
                command.Parameters.AddWithValue("@Id", record.Id);
                command.Parameters.AddWithValue("@Date", dateString);
                command.Parameters.AddWithValue("@ProcessName", record.ProcessName);
                command.Parameters.AddWithValue("@ApplicationName", record.ApplicationName ?? "");
                command.Parameters.AddWithValue("@EndTime", validatedEndTime?.ToString("o") ?? (object)DBNull.Value);
                command.Parameters.AddWithValue("@Duration", durationMs);
                command.Parameters.AddWithValue("@IsFocused", record.IsFocused ? 1 : 0);
                command.Parameters.AddWithValue("@LastUpdated", DateTime.UtcNow.ToString("o"));
                
                int rowsAffected = command.ExecuteNonQuery();
                
                if (rowsAffected > 0)
                {
                    transaction.Commit();
                }
                else
                {
                    System.Diagnostics.Debug.WriteLine($"Update failed, record with ID {record.Id} not found.");
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating record: {ex.Message}");
            }
        }

        /// <summary>
        /// Retrieves all app usage records for a specific date
        /// </summary>
        public List<AppUsageRecord> GetRecordsForDate(DateTime date)
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
                                long durationMs = !reader.IsDBNull(reader.GetOrdinal("duration")) ? reader.GetInt64(reader.GetOrdinal("duration")) : 0;
                                bool isFocused = !reader.IsDBNull(reader.GetOrdinal("is_focused")) && reader.GetInt32(reader.GetOrdinal("is_focused")) == 1;
                                DateTime? lastUpdated = !reader.IsDBNull(reader.GetOrdinal("last_updated")) ? DateTime.Parse(reader.GetString(reader.GetOrdinal("last_updated"))) : null;
                                
                                // If we already have a record for this process, update it (this logic might need review for non-aggregated view)
                                // For now, GetRecordsForDate is used by the chart which shows aggregated bars per hour,
                                // but the main list is usually a simple list of timed events.
                                // The current aggregation here might be a leftover or for a specific purpose.
                                // Let's assume for now it's intended. If this is for a raw list, aggregation should be removed.
                                if (processGroups.TryGetValue(processName, out var existingRecord))
                                {
                                    // Add the duration to the existing record
                                    existingRecord._accumulatedDuration += TimeSpan.FromMilliseconds(durationMs);
                                    // How to handle IsFocused and LastUpdated for merged records? Take the latest?
                                    if (lastUpdated.HasValue && (!existingRecord.LastUpdated.HasValue || lastUpdated.Value > existingRecord.LastUpdated.Value))
                                    {
                                        existingRecord.LastUpdated = lastUpdated;
                                        existingRecord.IsFocused = isFocused; // Assume focus state from the latest update within the group
                                    }
                                    if (dbStartTime < existingRecord.StartTime) existingRecord.StartTime = dbStartTime;
                                    if (reader.IsDBNull(reader.GetOrdinal("end_time"))) existingRecord.EndTime = null; // If any segment is open, keep it open
                                    else if (existingRecord.EndTime.HasValue && DateTime.Parse(reader.GetString(reader.GetOrdinal("end_time"))) > existingRecord.EndTime.Value)
                                    {
                                        existingRecord.EndTime = DateTime.Parse(reader.GetString(reader.GetOrdinal("end_time")));
                                    }


                                    Debug.WriteLine($"Merged record: {processName}, total duration now: {existingRecord.Duration.TotalSeconds:F1}s, IsFocused: {existingRecord.IsFocused}");
                                }
                                else
                                {
                                    // Create a new record
                                    var record = new AppUsageRecord
                                    {
                                        Id = reader.GetInt32(reader.GetOrdinal("id")),
                                        ProcessName = processName,
                                        ApplicationName = !reader.IsDBNull(reader.GetOrdinal("app_name")) ? reader.GetString(reader.GetOrdinal("app_name")) : processName,
                                        Date = date, // Use date for the Date field
                                        StartTime = dbStartTime, // Use actual start time from database
                                        IsFocused = isFocused,
                                        LastUpdated = lastUpdated
                                    };
                                    
                                    if (!reader.IsDBNull(reader.GetOrdinal("end_time")))
                                    {
                                        record.EndTime = DateTime.Parse(reader.GetString(reader.GetOrdinal("end_time")));
                                    }
                                    
                                    record._accumulatedDuration = TimeSpan.FromMilliseconds(durationMs);
                                    
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
                        SUM(total_duration_ms) AS total_ms
                    FROM vw_daily_app_usage
                    WHERE date >= @StartDate AND date <= @EndDate
                    GROUP BY process_name;";

                using (var cmd = new SqliteCommand(sql, connection))
                {
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
        {
            try
            {
                // Ensure the database is initialized
                if (!IsDatabaseInitialized())
                {
                    System.Diagnostics.Debug.WriteLine($"ERROR: Database not initialized in GetRawRecordsForDateRange");
                    return new List<AppUsageRecord>();
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
                return new List<AppUsageRecord>();
            }
        }

        public List<AppUsageRecord> GetRecordsForDateRange(DateTime startDate, DateTime endDate)
        {
            // Reuse existing raw retrieval method. Caller is responsible for any aggregation.
            return GetRawRecordsForDateRange(startDate, endDate);
        }

        public bool WipeDatabase()
        {
            try
            {
                if (!_initializedSuccessfully)
                {
                    return false;
                }

                using var connection = CreateConnection();
                OpenConnection(connection);

                // 1) Delete all rows inside an explicit transaction
                using (var tx = connection.BeginTransaction())
                using (var cmd = connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM app_usage;";
                    cmd.ExecuteNonQuery();
                    tx.Commit(); // Commit BEFORE running VACUUM – it cannot execute inside a transaction
                }

                // 2) Reclaim file space – now that we are outside the transaction
                using (var vacuumCmd = connection.CreateCommand())
                {
                    vacuumCmd.CommandText = "VACUUM;";
                    vacuumCmd.ExecuteNonQuery();
                }

                return true;
            }
            catch (Exception)
            {
                // Swallow any exception – caller receives failure via 'false'
                return false;
            }
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
        public List<AppUsageRecord> GetDetailRecordsWithLive(DateTime date, WindowTrackingService? trackingService)
        {
            var dbRecords = GetRecordsForDate(date) ?? new List<AppUsageRecord>();
            var aggregated = AggregateRecords(dbRecords, date);
            
            if (date.Date == DateTime.Today && trackingService != null)
            {
                var liveRecords = trackingService.GetRecords().Where(r => r.IsFromDate(date));
                var liveAggregated = AggregateRecords(liveRecords, date);
                
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

    }
} 
