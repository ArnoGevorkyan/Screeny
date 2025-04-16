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
            using var command = _connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS app_usage (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    date TEXT NOT NULL,
                    process_name TEXT NOT NULL,
                    app_name TEXT,
                    start_time TEXT NOT NULL,
                    end_time TEXT,
                    duration INTEGER
                );";
            command.ExecuteNonQuery();
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
                        duration INTEGER
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
        /// Saves an app usage record to the database
        /// </summary>
        public bool SaveRecord(AppUsageRecord record)
        {
            System.Diagnostics.Debug.WriteLine($"[LOG] ENTERING SaveRecord for {record.ProcessName}");
            if (!_initializedSuccessfully || _useInMemoryFallback) // Also skip if using fallback
            {
                System.Diagnostics.Debug.WriteLine($"[LOG] SaveRecord: Database not initialized or using in-memory fallback, skipping.");
                return false;
            }
            
            bool success = false;
            SqliteTransaction? transaction = null; // Declare transaction variable
            try
            {
                _connection.Open();
                transaction = _connection.BeginTransaction(); // Start transaction
                System.Diagnostics.Debug.WriteLine("[LOG] SaveRecord: Connection opened and transaction started.");

                // Validate date to prevent future dates
                DateTime validatedStartTime = ValidateDate(record.StartTime);
                DateTime? validatedEndTime = record.EndTime.HasValue ? ValidateDate(record.EndTime.Value) : null;
                string dateString = validatedStartTime.ToString("yyyy-MM-dd");

                // Log if there was validation
                if (validatedStartTime != record.StartTime)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOG] SaveRecord: Corrected future start date from {record.StartTime:yyyy-MM-dd} to {validatedStartTime:yyyy-MM-dd}");
                }

                string insertSql = @"INSERT INTO app_usage (date, process_name, app_name, start_time, end_time, duration) VALUES (@Date, @ProcessName, @ApplicationName, @StartTime, @EndTime, @Duration); SELECT last_insert_rowid();";

                using (var command = new SqliteCommand(insertSql, _connection, transaction)) // Assign transaction
                {
                    command.Parameters.AddWithValue("@Date", dateString);
                    command.Parameters.AddWithValue("@ProcessName", record.ProcessName);
                    command.Parameters.AddWithValue("@ApplicationName", record.ApplicationName ?? "");
                    command.Parameters.AddWithValue("@StartTime", validatedStartTime.ToString("o"));
                    command.Parameters.AddWithValue("@EndTime", validatedEndTime.HasValue ? validatedEndTime.Value.ToString("o") : (object?)DBNull.Value); // Use DBNull
                    command.Parameters.AddWithValue("@Duration", (long)record.Duration.TotalMilliseconds);
                    
                    System.Diagnostics.Debug.WriteLine("[LOG] SaveRecord: BEFORE ExecuteScalar...");
                    var result = command.ExecuteScalar();
                    System.Diagnostics.Debug.WriteLine("[LOG] SaveRecord: AFTER ExecuteScalar.");
                    
                    if (result != null)
                    {
                        long id = Convert.ToInt64(result);
                        record.Id = (int)id;
                        System.Diagnostics.Debug.WriteLine($"[LOG] SaveRecord: Saved record {record.ProcessName} with ID {record.Id}");
                        success = true;
                    }
                    else
                    {
                         System.Diagnostics.Debug.WriteLine("[LOG] SaveRecord: ExecuteScalar returned null, save failed?");
                    }
                }
                
                transaction.Commit(); // Commit transaction if successful
                System.Diagnostics.Debug.WriteLine("[LOG] SaveRecord: Transaction committed.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOG] SaveRecord: **** ERROR **** saving record for {record.ProcessName}: {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                try
                {
                    transaction?.Rollback(); // Rollback transaction on error
                    System.Diagnostics.Debug.WriteLine("[LOG] SaveRecord: Transaction rolled back.");
                }
                catch (Exception rbEx)
                {
                     System.Diagnostics.Debug.WriteLine($"[LOG] SaveRecord: **** CRITICAL ERROR **** during rollback: {rbEx.Message}");
                }
                success = false; // Ensure success is false on error
            }
            finally
            {
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                    System.Diagnostics.Debug.WriteLine("[LOG] SaveRecord: Connection closed.");
                }
                System.Diagnostics.Debug.WriteLine($"[LOG] EXITING SaveRecord for {record.ProcessName}, Success: {success}");
            }
            return success;
        }

        /// <summary>
        /// Updates an existing app usage record in the database
        /// </summary>
        public void UpdateRecord(AppUsageRecord record)
        {
            System.Diagnostics.Debug.WriteLine($"[LOG] ENTERING UpdateRecord for ID {record.Id} ({record.ProcessName})");
            if (!_initializedSuccessfully || _useInMemoryFallback) // Also skip if using fallback
            {
                System.Diagnostics.Debug.WriteLine("[LOG] UpdateRecord: Database not initialized or using in-memory fallback, skipping.");
                return;
            }
            
            SqliteTransaction? transaction = null; // Declare transaction variable
            try
            {
                _connection.Open();
                transaction = _connection.BeginTransaction(); // Start transaction
                System.Diagnostics.Debug.WriteLine("[LOG] UpdateRecord: Connection opened and transaction started.");

                // Validate date to prevent future dates
                DateTime validatedStartTime = ValidateDate(record.StartTime);
                DateTime? validatedEndTime = record.EndTime.HasValue ? ValidateDate(record.EndTime.Value) : null;
                string dateString = validatedStartTime.ToString("yyyy-MM-dd");

                // Log if there was validation
                if (validatedStartTime != record.StartTime)
                {
                    System.Diagnostics.Debug.WriteLine($"[LOG] UpdateRecord: Corrected future start date from {record.StartTime:yyyy-MM-dd} to {validatedStartTime:yyyy-MM-dd}");
                }

                string updateSql = @"UPDATE app_usage SET date = @Date, process_name = @ProcessName, app_name = @ApplicationName, end_time = @EndTime, duration = @Duration WHERE id = @Id;";

                using (var command = new SqliteCommand(updateSql, _connection, transaction)) // Assign transaction
                {
                    command.Parameters.AddWithValue("@Id", record.Id);
                    command.Parameters.AddWithValue("@Date", dateString);
                    command.Parameters.AddWithValue("@ProcessName", record.ProcessName);
                    command.Parameters.AddWithValue("@ApplicationName", record.ApplicationName ?? "");
                    command.Parameters.AddWithValue("@EndTime", validatedEndTime.HasValue ? validatedEndTime.Value.ToString("o") : (object?)DBNull.Value); // Use DBNull
                    command.Parameters.AddWithValue("@Duration", (long)record.Duration.TotalMilliseconds);
                    
                    System.Diagnostics.Debug.WriteLine($"[LOG] UpdateRecord: BEFORE ExecuteNonQuery for ID {record.Id}...");
                    int rowsAffected = command.ExecuteNonQuery();
                    System.Diagnostics.Debug.WriteLine($"[LOG] UpdateRecord: AFTER ExecuteNonQuery for ID {record.Id}. Rows affected: {rowsAffected}.");
                    
                    if (rowsAffected <= 0)
                    {
                         System.Diagnostics.Debug.WriteLine($"[LOG] UpdateRecord: ExecuteNonQuery affected 0 rows, ID {record.Id} not found? Update failed.");
                         throw new InvalidOperationException($"Update failed, record with ID {record.Id} not found."); // Force rollback
                    }
                     System.Diagnostics.Debug.WriteLine($"[LOG] UpdateRecord: Updated record {record.ProcessName} with ID {record.Id}");
                }
                
                transaction.Commit(); // Commit transaction
                System.Diagnostics.Debug.WriteLine("[LOG] UpdateRecord: Transaction committed.");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[LOG] UpdateRecord: **** ERROR **** updating record ID {record.Id} ({record.ProcessName}): {ex.Message}");
                System.Diagnostics.Debug.WriteLine(ex.StackTrace);
                try
                {
                    transaction?.Rollback(); // Rollback transaction on error
                     System.Diagnostics.Debug.WriteLine("[LOG] UpdateRecord: Transaction rolled back.");
                }
                catch (Exception rbEx)
                {
                     System.Diagnostics.Debug.WriteLine($"[LOG] UpdateRecord: **** CRITICAL ERROR **** during rollback: {rbEx.Message}");
                }
            }
            finally
            {
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                    System.Diagnostics.Debug.WriteLine("[LOG] UpdateRecord: Connection closed.");
                }
                System.Diagnostics.Debug.WriteLine($"[LOG] EXITING UpdateRecord for ID {record.Id}");
            }
        }

        /// <summary>
        /// Retrieves all app usage records for a specific date
        /// </summary>
        public List<AppUsageRecord> GetRecordsForDate(DateTime date)
        {
            if (!_initializedSuccessfully || _useInMemoryFallback) // Also skip if using fallback
            {
                Debug.WriteLine("Database not initialized or using in-memory fallback, skipping get records operation");
                return new List<AppUsageRecord>();
            }

            var records = new List<AppUsageRecord>();
            
            try
            {
                var dateString = date.ToString("yyyy-MM-dd");
                Debug.WriteLine($"Getting records for date: {dateString}");
                
                // Open connection
                _connection.Open();
                
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT id, process_name, app_name, start_time, end_time, duration
                        FROM app_usage
                        WHERE date = $date
                        ORDER BY process_name, duration DESC;"; // Group by process_name to keep related entries together
                    command.Parameters.AddWithValue("$date", dateString);
                    
                    using (var reader = command.ExecuteReader())
                    {
                        // Dictionary to aggregate records with the same process name
                        var processGroups = new Dictionary<string, AppUsageRecord>(StringComparer.OrdinalIgnoreCase);
                        
                        while (reader.Read())
                        {
                            try
                            {
                                string processName = reader.GetString(1);
                                var dbStartTime = DateTime.Parse(reader.GetString(3));
                                long durationMs = !reader.IsDBNull(5) ? reader.GetInt64(5) : 0;
                                
                                // If we already have a record for this process, update it
                                if (processGroups.TryGetValue(processName, out var existingRecord))
                                {
                                    // Add the duration to the existing record
                                    existingRecord._accumulatedDuration += TimeSpan.FromMilliseconds(durationMs);
                                    Debug.WriteLine($"Merged record: {processName}, total duration now: {existingRecord.Duration.TotalSeconds:F1}s");
                                }
                                else
                                {
                                    // Create a new record
                                    var record = new AppUsageRecord
                                    {
                                        Id = reader.GetInt32(0),
                                        ProcessName = processName,
                                        ApplicationName = !reader.IsDBNull(2) ? reader.GetString(2) : processName,
                                        Date = date, // Use date for the Date field
                                        StartTime = dbStartTime // Use actual start time from database
                                    };
                                    
                                    if (!reader.IsDBNull(4))
                                    {
                                        record.EndTime = DateTime.Parse(reader.GetString(4));
                                    }
                                    
                                    record._accumulatedDuration = TimeSpan.FromMilliseconds(durationMs);
                                    
                                    // Add to our dictionary
                                    processGroups[processName] = record;
                                    Debug.WriteLine($"Added new record: {processName}, Duration: {record.Duration.TotalSeconds:F1}s");
                                }
                            }
                            catch (Exception parseEx)
                            {
                                Debug.WriteLine($"Error parsing record: {parseEx.Message}");
                                // Continue to next record
                            }
                        }
                        
                        // Convert dictionary values to our final list
                        records = processGroups.Values.ToList();
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting records for date: {ex.Message}");
            }
            finally
            {
                // Make sure connection is closed
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                }
            }
            
            Debug.WriteLine($"Loaded {records.Count} aggregated records for date {date:yyyy-MM-dd}");
            return records;
        }

        /// <summary>
        /// Gets aggregated usage data for a specific date, grouped by application
        /// </summary>
        public List<AppUsageRecord> GetAggregatedRecordsForDate(DateTime date)
        {
            if (!_initializedSuccessfully || _useInMemoryFallback) // Also skip if using fallback
            {
                Debug.WriteLine("Database not initialized or using in-memory fallback, skipping get aggregated records operation");
                return new List<AppUsageRecord>();
            }
                
            var records = new List<AppUsageRecord>();
            
            try
            {
                var dateString = date.ToString("yyyy-MM-dd");
                
                // Open connection
                _connection.Open();
                
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT process_name, app_name, SUM(duration)
                        FROM app_usage
                        WHERE date = $date
                        GROUP BY process_name
                        ORDER BY SUM(duration) DESC;";
                    command.Parameters.AddWithValue("$date", dateString);
                    
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var record = new AppUsageRecord
                            {
                                ProcessName = reader.GetString(0),
                                ApplicationName = !reader.IsDBNull(1) ? reader.GetString(1) : reader.GetString(0),
                                Date = date
                            };
                            
                            if (!reader.IsDBNull(2))
                            {
                                // Convert milliseconds to TimeSpan
                                record._accumulatedDuration = TimeSpan.FromMilliseconds(reader.GetInt64(2));
                            }
                            
                            records.Add(record);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Error getting aggregated records: {ex.Message}");
            }
            finally
            {
                // Make sure connection is closed
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                }
            }
            
            return records;
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

            var usageDataGrouped = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            string startDateString = startDate.ToString("yyyy-MM-dd");
            string endDateString = endDate.ToString("yyyy-MM-dd");

            try
            {
                _connection.Open();

                // --- MODIFIED SQL: Select individual durations, not SUM --- 
                string selectSql = @"
                SELECT 
                    process_name, 
                    duration
                FROM app_usage 
                WHERE date >= @StartDate AND date <= @EndDate;";

                using (var command = new SqliteCommand(selectSql, _connection))
                {
                    command.Parameters.AddWithValue("@StartDate", startDateString);
                    command.Parameters.AddWithValue("@EndDate", endDateString);

                    // --- Add detailed logging --- 
                    System.Diagnostics.Debug.WriteLine($"--- Executing SQL ---");
                    System.Diagnostics.Debug.WriteLine($"SQL: {command.CommandText}");
                    System.Diagnostics.Debug.WriteLine($"Param @StartDate: {startDateString}");
                    System.Diagnostics.Debug.WriteLine($"Param @EndDate: {endDateString}");
                    System.Diagnostics.Debug.WriteLine($"---------------------");
                    // --- End detailed logging ---
                    
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string processName = reader.GetString(0);
                            long durationMs = reader.GetInt64(1);

                            if (!usageDataGrouped.ContainsKey(processName))
                            {
                                usageDataGrouped[processName] = new List<long>();
                            }
                            usageDataGrouped[processName].Add(durationMs);
                        }
                    }
                }

                _connection.Close();

                // --- Aggregate in C# with overflow check --- 
                var finalUsageData = new List<(string, TimeSpan)>();
                foreach (var kvp in usageDataGrouped)
                {
                    string processName = kvp.Key;
                    long totalMs = 0;
                    bool overflow = false;

                    foreach (long ms in kvp.Value)
                    {
                        // Check for potential overflow BEFORE adding
                        if (long.MaxValue - ms < totalMs)
                        {
                            totalMs = long.MaxValue; // Cap at MaxValue
                            overflow = true;
                            Debug.WriteLine($"WARNING: Potential long overflow during C# aggregation for {processName}. Capping total milliseconds.");
                            break; // Stop summing for this process if overflow detected
                        }
                        totalMs += ms;
                    }

                    // Further capping similar to before (optional but safe)
                    int daysBetween = (int)(endDate - startDate).TotalDays + 1;
                    long maxReasonableDurationMs = (long)daysBetween * 86400000; // Ensure cast to long
                    long maxAllowedMs = (long)(TimeSpan.MaxValue.TotalMilliseconds * 0.9);

                    if (!overflow && (totalMs < 0 || totalMs > maxReasonableDurationMs))
                    {
                        Debug.WriteLine($"WARNING: Unrealistic duration calculated in C# for {processName}: {totalMs}ms. Capping.");
                        totalMs = maxReasonableDurationMs;
                    }
                    if (totalMs > maxAllowedMs)
                    {
                        Debug.WriteLine($"WARNING: C# calculated duration exceeds TimeSpan.MaxValue limits for {processName}. Capping.");
                        totalMs = maxAllowedMs;
                    }
                    
                    TimeSpan duration = TimeSpan.FromMilliseconds(totalMs);
                    finalUsageData.Add((processName, duration));
                }
                
                // --- Re-introduce the 5-minute filter --- 
                var filteredData = finalUsageData
                    .Where(d => d.Item2.TotalSeconds >= 300)
                    .OrderByDescending(d => d.Item2)
                    .ToList();
                
                System.Diagnostics.Debug.WriteLine($"Retrieved and aggregated usage report with {filteredData.Count} entries in C# after filtering");
                return filteredData;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error retrieving/aggregating usage report: {ex.Message}");
                // Ensure connection is closed on error
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                }
                throw; // Rethrow the exception
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
                Debug.WriteLine("[LOG] CleanupExpiredRecords: Connection opened and transaction started.");

                string deleteSql = @"DELETE FROM app_usage WHERE date < @CutoffDate;";
                int rowsDeleted = 0;

                using (var command = new SqliteCommand(deleteSql, _connection, transaction))
                {
                    command.Parameters.AddWithValue("@CutoffDate", cutoffDateString);
                    rowsDeleted = command.ExecuteNonQuery();
                    System.Diagnostics.Debug.WriteLine($"Deleted {rowsDeleted} expired records older than {cutoffDateString}");
                }

                // Only run VACUUM if records were actually deleted
                if (rowsDeleted > 0)
                {
                    Debug.WriteLine("[LOG] CleanupExpiredRecords: Running VACUUM...");
                    using (var command = _connection.CreateCommand())
                    {
                        command.Transaction = transaction; // Assign transaction to VACUUM command
                        command.CommandText = "VACUUM;";
                        command.ExecuteNonQuery();
                        System.Diagnostics.Debug.WriteLine("Database vacuumed to reclaim space");
                    }
                }
                else
                {
                    Debug.WriteLine("[LOG] CleanupExpiredRecords: No records deleted, skipping VACUUM.");
                }

                transaction.Commit(); // Commit transaction
                Debug.WriteLine("[LOG] CleanupExpiredRecords: Transaction committed.");

            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error cleaning up expired records: {ex.Message}");
                 try
                 {
                     transaction?.Rollback(); // Rollback transaction on error
                     Debug.WriteLine("[LOG] CleanupExpiredRecords: Transaction rolled back.");
                 }
                 catch (Exception rbEx)
                 {
                      Debug.WriteLine($"[LOG] CleanupExpiredRecords: **** CRITICAL ERROR **** during rollback: {rbEx.Message}");
                 }
                 // Consider rethrowing if cleanup is critical, otherwise just log
            }
            finally
            {
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                    Debug.WriteLine("[LOG] CleanupExpiredRecords: Connection closed.");
                }
                 Debug.WriteLine("[LOG] EXITING CleanupExpiredRecords");
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
                    SELECT ProcessName, ApplicationName, Duration, StartHour, StartMinute, StartSecond, Date
                    FROM AppUsage
                    WHERE Date BETWEEN @StartDate AND @EndDate
                    ORDER BY Date ASC, ProcessName ASC";

                List<AppUsageRecord> records = new List<AppUsageRecord>();

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
                                string processName = reader.GetString(0);
                                string appName = reader.GetString(1);
                                double durationSeconds = reader.GetDouble(2);
                                int startHour = reader.GetInt32(3);
                                int startMinute = reader.GetInt32(4);
                                int startSecond = reader.GetInt32(5);
                                string dateStr = reader.GetString(6);

                                // Parse the date
                                if (DateTime.TryParse(dateStr, out DateTime date))
                                {
                                    // Create the start time
                                    DateTime startTime = new DateTime(
                                        date.Year, date.Month, date.Day,
                                        startHour, startMinute, startSecond
                                    );

                                    // Create the app usage record
                                    var record = new AppUsageRecord
                                    {
                                        ProcessName = processName,
                                        ApplicationName = appName ?? processName,
                                        Date = date,
                                        StartTime = startTime,
                                        _accumulatedDuration = TimeSpan.FromSeconds(durationSeconds)
                                    };

                                    records.Add(record);
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
    }
} 