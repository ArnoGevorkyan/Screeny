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

                string insertSql = @"
                INSERT INTO app_usage 
                    (process_name, app_name, start_time, end_time, duration)
                VALUES 
                    (@ProcessName, @ApplicationName, @StartTime, @EndTime, @Duration);
                SELECT last_insert_rowid();";

                using (var command = new SqliteCommand(insertSql, _connection))
                {
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

                string updateSql = @"
                UPDATE app_usage 
                SET 
                    process_name = @ProcessName,
                    app_name = @ApplicationName,
                    end_time = @EndTime,
                    duration = @Duration
                WHERE id = @Id;";

                using (var command = new SqliteCommand(updateSql, _connection))
                {
                    command.Parameters.AddWithValue("@Id", record.Id);
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
                                record._accumulatedDuration = TimeSpan.FromSeconds(reader.GetInt32(5));
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
                                record._accumulatedDuration = TimeSpan.FromSeconds(reader.GetInt32(2));
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

                _connection.Close();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error cleaning up expired records: {ex.Message}");
                throw;
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