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
        private readonly string _dbPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenTimeTracker", "screentime.db");
        private readonly SqliteConnection _connection;
        private bool _initializedSuccessfully = false;
        private bool _disposed = false;
        private bool _useInMemoryFallback = false;
        private List<AppUsageRecord> _memoryFallbackRecords = new List<AppUsageRecord>();

        /// <summary>
        /// Initializes a new instance of the DatabaseService class.
        /// </summary>
        public DatabaseService()
        {
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
                _useInMemoryFallback = false; // Ensure we know we are using the file

                // Test record logic moved inside InitializeDatabase if needed or removed if not required
                // ... (Removed IsFirstRun check and test record creation from here)
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CRITICAL Database initialization error: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                
                // Fallback to in-memory database
                Debug.WriteLine("Attempting to initialize in-memory database as fallback.");
                _connection = new SqliteConnection("Data Source=:memory:;Mode=Memory;Cache=Shared");
                try
                {
                    InitializeInMemoryDatabase();
                    _initializedSuccessfully = true; // Considered initialized for in-memory operation
                    _useInMemoryFallback = true;
                    Debug.WriteLine("Successfully initialized in-memory database.");
                }
                catch (Exception memEx)
                {
                    Debug.WriteLine($"FATAL: In-memory database initialization failed: {memEx.Message}");
                    // If even in-memory fails, create a null connection or handle appropriately
                     _connection = new SqliteConnection(); // Avoid null reference, but it won't work
                    _initializedSuccessfully = false;
                    _useInMemoryFallback = true; // Still in fallback mode, just failed
                }
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

        // New method to check DB integrity using PRAGMA
        private bool CheckDatabaseIntegrity(string dbPath)
        {
            Debug.WriteLine($"Checking database integrity for: {dbPath}");
            if (!File.Exists(dbPath)) return true; // No file, considered intact

            // Use a temporary connection for the check
            var checkConnectionString = CreateConnectionString(dbPath);
            using (var checkConnection = new SqliteConnection(checkConnectionString))
            {
                try
                {
                    checkConnection.Open();
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
                     // At this point, initialization will likely fail, leading to in-memory fallback
                 }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"CRITICAL ERROR: Failed to move corrupted database file: {ex.Message}");
                // Initialization might fail, leading to in-memory fallback
            }
        }

        private void InitializeInMemoryDatabase()
        {
            // Create basic schema for in-memory database
            _connection.Open();
            using var command = _connection.CreateCommand();
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
            _connection.Close();
            Debug.WriteLine("In-memory database initialized with basic schema");
        }

        // Check if database was initialized correctly
        public bool IsDatabaseInitialized() => _initializedSuccessfully;

        public bool IsFirstRun()
        {
            if (!_initializedSuccessfully)
                return true;

            try
            {
                // Make sure we're using a fresh connection state
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                }
                
                _connection.Open();
                Debug.WriteLine("IsFirstRun: Opened database connection");
                
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM app_usage";
                var result = Convert.ToInt32(command.ExecuteScalar());
                
                Debug.WriteLine($"IsFirstRun: Found {result} records in database");
                
                _connection.Close();
                return result == 0;
            }
            catch (Exception ex)
            {
                // If we can't query, assume it's first run
                Debug.WriteLine($"IsFirstRun failed: {ex.Message}");
                return true;
            }
            finally
            {
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                }
            }
        }

        private void InitializeDatabase()
        {
            try
            {
                // Open connection
                _connection.Open();

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

                // Create indices for better performance
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

                // Create aggregated VIEW for per-day per-process totals
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

                // Check if we need to perform any migrations
                CheckAndPerformMigrations();

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

        /// <summary>
        /// Migrates database to version 1 
        /// Ensures date column exists and is populated
        /// </summary>
        private void MigrateToV1()
        {
            Debug.WriteLine("Performing migration to version 1");
            
            try
            {
                // Check if we need to add date column by getting column info
                bool hasDateColumn = false;
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA table_info(app_usage);";
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string columnName = reader.GetString(1);
                            if (columnName.Equals("date", StringComparison.OrdinalIgnoreCase))
                            {
                                hasDateColumn = true;
                                break;
                            }
                        }
                    }
                }

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
                                command.CommandText = "ALTER TABLE app_usage ADD COLUMN date TEXT;";
                                command.ExecuteNonQuery();
                            }
                            
                            // Update existing records to extract date from start_time
                            using (var command = _connection.CreateCommand())
                            {
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
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA user_version = 1;";
                    command.ExecuteNonQuery();
                }
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
                // Begin transaction
                using (var transaction = _connection.BeginTransaction())
                {
                    try
                    {
                        // Add is_focused column
                        using (var command = _connection.CreateCommand())
                        {
                            command.CommandText = "ALTER TABLE app_usage ADD COLUMN is_focused INTEGER DEFAULT 0;";
                            command.ExecuteNonQuery();
                            Debug.WriteLine("Added is_focused column to app_usage table.");
                        }

                        // Add last_updated column
                        using (var command = _connection.CreateCommand())
                        {
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
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA user_version = 2;";
                    command.ExecuteNonQuery();
                    Debug.WriteLine("Database user_version set to 2.");
                }
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
            if (!_initializedSuccessfully || _useInMemoryFallback)
            {
                return false;
            }

            try
            {
                var (validatedStartTime, validatedEndTime, dateString, durationMs) = ValidateRecordData(record);

                // Use connection-per-operation pattern
                using var connection = new SqliteConnection(_connection.ConnectionString);
                connection.Open();
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

        /// <summary>
        /// Updates an existing app usage record in the database
        /// </summary>
        public void UpdateRecord(AppUsageRecord record)
        {
            if (!_initializedSuccessfully || _useInMemoryFallback)
            {
                return;
            }

            try
            {
                var (validatedStartTime, validatedEndTime, dateString, durationMs) = ValidateRecordData(record);

                using var connection = new SqliteConnection(_connection.ConnectionString);
                connection.Open();
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
            if (!_initializedSuccessfully || _useInMemoryFallback)
            {
                Debug.WriteLine("Database not initialized or using in-memory fallback, skipping get records operation");
                return new List<AppUsageRecord>();
            }

            try
            {
                var dateString = date.ToString("yyyy-MM-dd");
                Debug.WriteLine($"Getting records for date: {dateString}");
                
                using var connection = new SqliteConnection(_connection.ConnectionString);
                connection.Open();
                
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
            if (!_initializedSuccessfully || _useInMemoryFallback) // Also skip if using fallback
            {
                Debug.WriteLine("Database not initialized or using in-memory fallback, skipping get usage report operation");
                return new List<(string, TimeSpan)>();
            }

            var results = new List<(string, TimeSpan)>();
            string startDateString = startDate.ToString("yyyy-MM-dd");
            string endDateString   = endDate.ToString("yyyy-MM-dd");

            try
            {
                _connection.Open();
                const string sql = @"
                    SELECT
                        process_name,
                        SUM(total_duration_ms) AS total_ms
                    FROM vw_daily_app_usage
                    WHERE date >= @StartDate AND date <= @EndDate
                    GROUP BY process_name;";

                using (var cmd = new SqliteCommand(sql, _connection))
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

                _connection.Close();

                // Filter entries shorter than 5 minutes and sort descending.
                return results.Where(r => r.Item2.TotalSeconds >= 300)
                               .OrderByDescending(r => r.Item2)
                               .ToList();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error retrieving usage report: {ex.Message}");
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                }
                throw;
            }
        }

        /// <summary>
        /// Cleans up expired records (optional feature for data retention policy)
        /// </summary>
        public void CleanupExpiredRecords(int daysToKeep)
        {
            if (!_initializedSuccessfully || _useInMemoryFallback) // Also skip if using fallback
            {
                Debug.WriteLine("Database not initialized or using in-memory fallback, skipping cleanup operation");
                return;
            }

            SqliteTransaction? transaction = null;
            try
            {
                DateTime cutoffDate = DateTime.Now.AddDays(-daysToKeep).Date;
                string cutoffDateString = cutoffDate.ToString("yyyy-MM-dd");

                _connection.Open();
                transaction = _connection.BeginTransaction();

                string deleteSql = @"DELETE FROM app_usage WHERE date < @CutoffDate;";
                int rowsDeleted = 0;

                using (var command = new SqliteCommand(deleteSql, _connection, transaction))
                {
                    command.Parameters.AddWithValue("@CutoffDate", cutoffDateString);
                    rowsDeleted = command.ExecuteNonQuery();
                }

                // Only run VACUUM if records were actually deleted
                if (rowsDeleted > 0)
                {
                    using (var command = _connection.CreateCommand())
                    {
                        command.Transaction = transaction; // Assign transaction to VACUUM command
                        command.CommandText = "VACUUM;";
                        command.ExecuteNonQuery();
                    }
                }

                transaction.Commit(); // Commit transaction
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
            finally
            {
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                }
            }
        }

        /// <summary>
        /// Performs database maintenance and integrity checks
        /// </summary>
        /// <returns>True if the database passes integrity checks</returns>
        public bool PerformDatabaseMaintenance()
        {
            if (!_initializedSuccessfully || _useInMemoryFallback) // Also skip if using fallback
            {
                Debug.WriteLine("Database not initialized or using in-memory fallback, skipping maintenance");
                return false;
            }
            
            try
            {
                _connection.Open();
                bool integrityPassed = true;
                
                // Run integrity check
                using (var command = _connection.CreateCommand())
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
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "PRAGMA optimize;";
                    command.ExecuteNonQuery();
                    Debug.WriteLine("Database optimized");
                }
                
                // Analyze the database to improve query performance
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "ANALYZE;";
                    command.ExecuteNonQuery();
                    Debug.WriteLine("Database analyzed");
                }
                
                _connection.Close();
                return integrityPassed;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error during database maintenance: {ex.Message}");
                return false;
            }
            finally
            {
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                }
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

                if (_useInMemoryFallback)
                {
                    // Use the in-memory backup data if we're in fallback mode
                    return _memoryFallbackRecords
                        .Where(r => r.Date.Date >= startDate.Date && r.Date.Date <= endDate.Date)
                        .ToList();
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
                using (var connection = new SqliteConnection(_connection.ConnectionString))
                {
                    connection.Open();
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
                // If we are running in in-memory fallback mode, just clear the in-memory list
                if (_useInMemoryFallback)
                {
                    _memoryFallbackRecords.Clear();
                    return true;
                }

                if (_connection == null)
                    return false;

                // Open the connection if required
                var needClose = false;
                if (_connection.State != System.Data.ConnectionState.Open)
                {
                    _connection.Open();
                    needClose = true;
                }

                // 1) Delete all rows inside an explicit transaction
                using (var tx = _connection.BeginTransaction())
                using (var cmd = _connection.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM app_usage;";
                    cmd.ExecuteNonQuery();
                    tx.Commit(); // Commit BEFORE running VACUUM – it cannot execute inside a transaction
                }

                // 2) Reclaim file space – now that we are outside the transaction
                using (var vacuumCmd = _connection.CreateCommand())
                {
                    vacuumCmd.CommandText = "VACUUM;";
                    vacuumCmd.ExecuteNonQuery();
                }

                if (needClose)
                    _connection.Close();

                return true;
            }
            catch (Exception)
            {
                // Swallow any exception – caller receives failure via 'false'
                return false;
            }
        }

        /// <summary>
        /// Gets aggregated records for date range, optionally including live tracking data
        /// (Replaces UsageAggregationService.GetAggregatedRecordsForDateRange)
        /// </summary>
        public List<AppUsageRecord> GetAggregatedRecordsWithLive(DateTime startDate, DateTime endDate, WindowTrackingService? trackingService, bool includeLiveRecords = true)
        {
            if (startDate > endDate) (startDate, endDate) = (endDate, startDate);

            var unique = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);

            // Get database records
            var dbReport = GetUsageReportForDateRange(startDate, endDate);
            foreach (var (processNameRaw, totalDuration) in dbReport)
            {
                var temp = new AppUsageRecord { ProcessName = processNameRaw };
                Helpers.ApplicationProcessingHelper.ProcessApplicationRecord(temp);
                var processName = temp.ProcessName;

                if (unique.TryGetValue(processName, out var existing))
                {
                    // Merge with existing database entry
                    existing._accumulatedDuration += totalDuration;
                }
                else
                {
                    unique[processName] = new AppUsageRecord
                    {
                        ProcessName = processName,
                        ApplicationName = processName,
                        _accumulatedDuration = totalDuration,
                        Date = startDate,
                        StartTime = startDate
                    };
                }
            }

            // Merge live records if requested
            if (includeLiveRecords && endDate.Date >= DateTime.Today && trackingService != null)
            {
                var live = trackingService.GetRecords()
                    .Where(r => r.Date >= startDate && r.Date <= endDate)
                    .ToList();

                foreach (var liveRec in live)
                {
                    if (liveRec.Duration.TotalSeconds <= 0) continue;

                    var tmpLive = new AppUsageRecord { ProcessName = liveRec.ProcessName, WindowTitle = liveRec.WindowTitle };
                    Helpers.ApplicationProcessingHelper.ProcessApplicationRecord(tmpLive);
                    var canonicalLive = tmpLive.ProcessName;

                    if (unique.TryGetValue(canonicalLive, out var existing))
                    {
                        // Add live duration to existing database duration instead of replacing
                        existing._accumulatedDuration += liveRec.Duration;
                        existing.WindowHandle = liveRec.WindowHandle;
                        if (!string.IsNullOrEmpty(liveRec.WindowTitle)) existing.WindowTitle = liveRec.WindowTitle;
                        if (liveRec.StartTime < existing.StartTime) existing.StartTime = liveRec.StartTime;
                    }
                    else
                    {
                        var clone = new AppUsageRecord
                        {
                            ProcessName = canonicalLive,
                            ApplicationName = liveRec.ApplicationName,
                            WindowTitle = liveRec.WindowTitle,
                            WindowHandle = liveRec.WindowHandle,
                            _accumulatedDuration = liveRec.Duration,
                            Date = liveRec.Date,
                            StartTime = liveRec.StartTime,
                            IsFocused = liveRec.IsFocused
                        };
                        unique[clone.ProcessName] = clone;
                    }
                }
            }

            return unique.Values
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
            var list = GetRecordsForDate(date) ?? new List<AppUsageRecord>();

            if (date.Date == DateTime.Today && trackingService != null)
            {
                var live = trackingService.GetRecords()
                    .Where(r => r.IsFromDate(date))
                    .ToList();
                list.AddRange(live);
            }

            var byProcess = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);

            foreach (var rec in list)
            {
                if (rec.Duration.TotalSeconds < 5) continue;
                
                var tmp = new AppUsageRecord { ProcessName = rec.ProcessName, WindowTitle = rec.WindowTitle };
                Helpers.ApplicationProcessingHelper.ProcessApplicationRecord(tmp);
                var canonical = tmp.ProcessName;

                if (Models.ProcessFilter.ShouldIgnoreProcess(canonical) ||
                    canonical.Equals(System.Diagnostics.Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (byProcess.TryGetValue(canonical, out var existing))
                {
                    // Add durations instead of taking the longer one to avoid losing data
                    existing._accumulatedDuration += rec.Duration;
                    if (rec.StartTime < existing.StartTime) existing.StartTime = rec.StartTime;
                    if (rec.EndTime.HasValue)
                    {
                        if (!existing.EndTime.HasValue || rec.EndTime > existing.EndTime) existing.EndTime = rec.EndTime;
                    }
                }
                else
                {
                    rec.ProcessName = canonical;
                    byProcess[canonical] = rec;
                }
            }

            return byProcess.Values.OrderByDescending(r => r.Duration.TotalSeconds).ToList();
        }

    }
} 