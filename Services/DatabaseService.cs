using Microsoft.Data.Sqlite;
using ScreenTimeTracker.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SQLitePCL;
using System.Diagnostics;

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

        /// <summary>
        /// Initializes a new instance of the DatabaseService class.
        /// </summary>
        public DatabaseService()
        {
            try
            {
                // Create directory for database
                var dataDirectory = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "ScreenTimeTracker");

                if (!Directory.Exists(dataDirectory))
                {
                    Directory.CreateDirectory(dataDirectory);
                }

                // Path is already initialized in the field declaration
                Debug.WriteLine($"Database path: {_dbPath}");

                // Create connection string
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = _dbPath,
                    Mode = SqliteOpenMode.ReadWriteCreate
                }.ToString();

                // Create connection
                _connection = new SqliteConnection(connectionString);
                
                // Initialize database schema
                InitializeDatabase();
                
                _initializedSuccessfully = true;
            }
            catch (Exception ex)
            {
                // Log the error but don't crash
                Debug.WriteLine($"Database initialization error: {ex.Message}");
                Debug.WriteLine($"Stack trace: {ex.StackTrace}");
                
                // We'll still create a connection object with a default in-memory database
                // so the app can run in a degraded mode without crashing
                _connection = new SqliteConnection("Data Source=:memory:");
                try
                {
                    _connection.Open();
                    InitializeInMemoryDatabase();
                    _initializedSuccessfully = false;
                }
                catch (Exception memEx)
                {
                    // Last resort - we'll operate without database support
                    Debug.WriteLine($"In-memory database initialization failed: {memEx.Message}");
                    _initializedSuccessfully = false;
                }
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
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM app_usage";
                _connection.Open();
                var result = Convert.ToInt32(command.ExecuteScalar());
                _connection.Close();
                return result == 0;
            }
            catch
            {
                // If we can't query, assume it's first run
                return true;
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
        /// Saves an app usage record to the database
        /// </summary>
        public bool SaveRecord(AppUsageRecord record)
        {
            if (!_initializedSuccessfully)
            {
                Debug.WriteLine("Database not initialized, skipping save operation");
                return false;
            }
            
            try
            {
                _connection.Open();

                // Extract date component for date-based filtering
                string dateString = record.StartTime.ToString("yyyy-MM-dd");

                string insertSql = @"
                INSERT INTO app_usage 
                    (date, process_name, app_name, start_time, end_time, duration)
                VALUES 
                    (@Date, @ProcessName, @ApplicationName, @StartTime, @EndTime, @Duration);
                SELECT last_insert_rowid();";

                using (var command = new SqliteCommand(insertSql, _connection))
                {
                    command.Parameters.AddWithValue("@Date", dateString);
                    command.Parameters.AddWithValue("@ProcessName", record.ProcessName);
                    command.Parameters.AddWithValue("@ApplicationName", record.ApplicationName ?? "");
                    command.Parameters.AddWithValue("@StartTime", record.StartTime.ToString("o"));
                    command.Parameters.AddWithValue("@EndTime", record.EndTime.HasValue ? record.EndTime.Value.ToString("o") : null);
                    command.Parameters.AddWithValue("@Duration", (long)record.Duration.TotalMilliseconds);
                    
                    // Get the ID of the inserted record
                    var result = command.ExecuteScalar();
                    if (result != null)
                    {
                        long id = Convert.ToInt64(result);
                        record.Id = (int)id;
                    }
                }

                _connection.Close();
                System.Diagnostics.Debug.WriteLine($"Saved record: {record.ProcessName} with ID {record.Id}");
                return true;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving record: {ex.Message}");
                return false;
            }
            finally
            {
                // Ensure the connection is closed even on exception
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                }
            }
        }

        /// <summary>
        /// Updates an existing app usage record in the database
        /// </summary>
        public void UpdateRecord(AppUsageRecord record)
        {
            if (!_initializedSuccessfully)
            {
                Debug.WriteLine("Database not initialized, skipping update operation");
                return;
            }

            try
            {
                _connection.Open();

                // Extract date component for consistency
                string dateString = record.StartTime.ToString("yyyy-MM-dd");

                string updateSql = @"
                UPDATE app_usage 
                SET 
                    date = @Date,
                    process_name = @ProcessName,
                    app_name = @ApplicationName,
                    end_time = @EndTime,
                    duration = @Duration
                WHERE id = @Id;";

                using (var command = new SqliteCommand(updateSql, _connection))
                {
                    command.Parameters.AddWithValue("@Id", record.Id);
                    command.Parameters.AddWithValue("@Date", dateString);
                    command.Parameters.AddWithValue("@ProcessName", record.ProcessName);
                    command.Parameters.AddWithValue("@ApplicationName", record.ApplicationName ?? "");
                    command.Parameters.AddWithValue("@EndTime", record.EndTime.HasValue ? record.EndTime.Value.ToString("o") : null);
                    command.Parameters.AddWithValue("@Duration", (long)record.Duration.TotalMilliseconds);
                    
                    command.ExecuteNonQuery();
                }

                _connection.Close();
                System.Diagnostics.Debug.WriteLine($"Updated record: {record.ProcessName} with ID {record.Id}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error updating record: {ex.Message}");
                throw;
            }
            finally
            {
                // Ensure the connection is closed even on exception
                if (_connection.State != System.Data.ConnectionState.Closed)
                {
                    _connection.Close();
                }
            }
        }

        /// <summary>
        /// Retrieves all app usage records for a specific date
        /// </summary>
        public List<AppUsageRecord> GetRecordsForDate(DateTime date)
        {
            if (!_initializedSuccessfully)
                return new List<AppUsageRecord>();

            var records = new List<AppUsageRecord>();
            
            try
            {
                var dateString = date.ToString("yyyy-MM-dd");
                
                // Open connection
                _connection.Open();
                
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = @"
                        SELECT id, process_name, app_name, start_time, end_time, duration
                        FROM app_usage
                        WHERE date = $date
                        ORDER BY start_time DESC;";
                    command.Parameters.AddWithValue("$date", dateString);
                    
                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var record = new AppUsageRecord
                            {
                                Id = reader.GetInt32(0),
                                ProcessName = reader.GetString(1),
                                ApplicationName = !reader.IsDBNull(2) ? reader.GetString(2) : reader.GetString(1),
                                Date = date,
                                StartTime = DateTime.Parse(reader.GetString(3))
                            };
                            
                            if (!reader.IsDBNull(4))
                            {
                                record.EndTime = DateTime.Parse(reader.GetString(4));
                            }
                            
                            if (!reader.IsDBNull(5))
                            {
                                // Convert milliseconds to TimeSpan
                                record._accumulatedDuration = TimeSpan.FromMilliseconds(reader.GetInt64(5));
                            }
                            
                            records.Add(record);
                        }
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
            
            return records;
        }

        /// <summary>
        /// Gets aggregated usage data for a specific date, grouped by application
        /// </summary>
        public List<AppUsageRecord> GetAggregatedRecordsForDate(DateTime date)
        {
            if (!_initializedSuccessfully)
                return new List<AppUsageRecord>();
                
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
            if (!_initializedSuccessfully)
                return new List<(string, TimeSpan)>();

            List<(string, TimeSpan)> usageData = new List<(string, TimeSpan)>();
            string startDateString = startDate.ToString("yyyy-MM-dd");
            string endDateString = endDate.ToString("yyyy-MM-dd");

            try
            {
                _connection.Open();

                string selectSql = @"
                SELECT 
                    process_name, 
                    SUM(duration) as TotalDuration
                FROM app_usage 
                WHERE date >= @StartDate AND date <= @EndDate
                GROUP BY process_name
                ORDER BY TotalDuration DESC;";

                using (var command = new SqliteCommand(selectSql, _connection))
                {
                    command.Parameters.AddWithValue("@StartDate", startDateString);
                    command.Parameters.AddWithValue("@EndDate", endDateString);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string processName = reader.GetString(0);
                            long totalDurationMs = reader.GetInt64(1);
                            TimeSpan duration = TimeSpan.FromMilliseconds(totalDurationMs);

                            usageData.Add((processName, duration));
                        }
                    }
                }

                _connection.Close();
                System.Diagnostics.Debug.WriteLine($"Retrieved usage report with {usageData.Count} entries");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error retrieving usage report: {ex.Message}");
                throw;
            }

            return usageData;
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

            try
            {
                DateTime cutoffDate = DateTime.Now.AddDays(-daysToKeep).Date;
                string cutoffDateString = cutoffDate.ToString("yyyy-MM-dd");

                _connection.Open();

                string deleteSql = @"DELETE FROM app_usage WHERE date < @CutoffDate;";

                using (var command = new SqliteCommand(deleteSql, _connection))
                {
                    command.Parameters.AddWithValue("@CutoffDate", cutoffDateString);
                    int rowsDeleted = command.ExecuteNonQuery();
                    System.Diagnostics.Debug.WriteLine($"Deleted {rowsDeleted} expired records older than {cutoffDateString}");
                }

                // Run VACUUM after deleting records to reclaim space
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText = "VACUUM;";
                    command.ExecuteNonQuery();
                    System.Diagnostics.Debug.WriteLine("Database vacuumed to reclaim space");
                }

                _connection.Close();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error cleaning up expired records: {ex.Message}");
                throw;
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
            if (!_initializedSuccessfully)
            {
                Debug.WriteLine("Database not initialized, skipping maintenance");
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
    }
} 