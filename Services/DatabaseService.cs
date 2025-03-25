using Microsoft.Data.Sqlite;
using ScreenTimeTracker.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SQLitePCL;

namespace ScreenTimeTracker.Services
{
    public class DatabaseService : IDisposable
    {
        private readonly string _databasePath;
        private readonly SqliteConnection _connection;
        private bool _disposed;

        /// <summary>
        /// Initializes a new instance of the DatabaseService class.
        /// </summary>
        public DatabaseService()
        {
            // Initialize SQLite
            Batteries_V2.Init();
            
            // Set up database file path in the user's AppData folder
            string appDataPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ScreenTimeTracker");
                
            // Ensure directory exists
            if (!Directory.Exists(appDataPath))
            {
                Directory.CreateDirectory(appDataPath);
            }
            
            _databasePath = Path.Combine(appDataPath, "ScreenTimeData.db");
            
            try
            {
                _connection = new SqliteConnection($"Data Source={_databasePath}");
                
                // Initialize the database
                InitializeDatabase();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing database connection: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Creates database tables if they don't exist
        /// </summary>
        private void InitializeDatabase()
        {
            try
            {
                _connection.Open();

                // Create AppUsageRecords table
                string createTableSql = @"
                CREATE TABLE IF NOT EXISTS AppUsageRecords (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProcessName TEXT NOT NULL,
                    WindowTitle TEXT,
                    ProcessId INTEGER,
                    StartTime TEXT NOT NULL,
                    EndTime TEXT,
                    AccumulatedDuration INTEGER,
                    Date TEXT NOT NULL
                );";

                using (var command = new SqliteCommand(createTableSql, _connection))
                {
                    command.ExecuteNonQuery();
                }

                _connection.Close();
                System.Diagnostics.Debug.WriteLine("Database initialized successfully");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error initializing database: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Saves an app usage record to the database
        /// </summary>
        public void SaveRecord(AppUsageRecord record)
        {
            ThrowIfDisposed();

            try
            {
                _connection.Open();

                string insertSql = @"
                INSERT INTO AppUsageRecords 
                    (ProcessName, WindowTitle, ProcessId, StartTime, EndTime, AccumulatedDuration, Date)
                VALUES 
                    (@ProcessName, @WindowTitle, @ProcessId, @StartTime, @EndTime, @AccumulatedDuration, @Date);
                SELECT last_insert_rowid();";

                using (var command = new SqliteCommand(insertSql, _connection))
                {
                    command.Parameters.AddWithValue("@ProcessName", record.ProcessName);
                    command.Parameters.AddWithValue("@WindowTitle", record.WindowTitle ?? "");
                    command.Parameters.AddWithValue("@ProcessId", record.ProcessId);
                    command.Parameters.AddWithValue("@StartTime", record.StartTime.ToString("o"));
                    command.Parameters.AddWithValue("@EndTime", record.EndTime.HasValue ? record.EndTime.Value.ToString("o") : null);
                    command.Parameters.AddWithValue("@AccumulatedDuration", (long)record.Duration.TotalMilliseconds);
                    command.Parameters.AddWithValue("@Date", record.StartTime.Date.ToString("yyyy-MM-dd"));
                    
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
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error saving record: {ex.Message}");
                throw;
            }
        }

        /// <summary>
        /// Updates an existing app usage record in the database
        /// </summary>
        public void UpdateRecord(AppUsageRecord record)
        {
            ThrowIfDisposed();

            try
            {
                _connection.Open();

                string updateSql = @"
                UPDATE AppUsageRecords 
                SET 
                    ProcessName = @ProcessName,
                    WindowTitle = @WindowTitle,
                    EndTime = @EndTime,
                    AccumulatedDuration = @AccumulatedDuration
                WHERE Id = @Id;";

                using (var command = new SqliteCommand(updateSql, _connection))
                {
                    command.Parameters.AddWithValue("@Id", record.Id);
                    command.Parameters.AddWithValue("@ProcessName", record.ProcessName);
                    command.Parameters.AddWithValue("@WindowTitle", record.WindowTitle ?? "");
                    command.Parameters.AddWithValue("@EndTime", record.EndTime.HasValue ? record.EndTime.Value.ToString("o") : null);
                    command.Parameters.AddWithValue("@AccumulatedDuration", (long)record.Duration.TotalMilliseconds);
                    
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
            ThrowIfDisposed();

            List<AppUsageRecord> records = new List<AppUsageRecord>();
            string dateString = date.ToString("yyyy-MM-dd");

            try
            {
                _connection.Open();

                string selectSql = @"
                SELECT 
                    Id, ProcessName, WindowTitle, ProcessId, StartTime, EndTime, AccumulatedDuration
                FROM AppUsageRecords 
                WHERE Date = @Date;";

                using (var command = new SqliteCommand(selectSql, _connection))
                {
                    command.Parameters.AddWithValue("@Date", dateString);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            AppUsageRecord record = new AppUsageRecord
                            {
                                Id = reader.GetInt32(0),
                                ProcessName = reader.GetString(1),
                                WindowTitle = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                                ProcessId = reader.GetInt32(3),
                                StartTime = DateTime.Parse(reader.GetString(4)),
                            };

                            if (!reader.IsDBNull(5))
                            {
                                record.EndTime = DateTime.Parse(reader.GetString(5));
                            }

                            // Set the accumulated duration
                            long durationMs = reader.GetInt64(6);
                            record._accumulatedDuration = TimeSpan.FromMilliseconds(durationMs);

                            records.Add(record);
                        }
                    }
                }

                _connection.Close();
                System.Diagnostics.Debug.WriteLine($"Retrieved {records.Count} records for date {dateString}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error retrieving records: {ex.Message}");
                throw;
            }

            return records;
        }

        /// <summary>
        /// Gets aggregated usage data for a specific date, grouped by application
        /// </summary>
        public List<AppUsageRecord> GetAggregatedRecordsForDate(DateTime date)
        {
            ThrowIfDisposed();

            List<AppUsageRecord> records = new List<AppUsageRecord>();
            string dateString = date.ToString("yyyy-MM-dd");

            try
            {
                _connection.Open();

                string selectSql = @"
                SELECT 
                    ProcessName, 
                    MIN(StartTime) as FirstStartTime,
                    SUM(AccumulatedDuration) as TotalDuration
                FROM AppUsageRecords 
                WHERE Date = @Date
                GROUP BY ProcessName
                ORDER BY TotalDuration DESC;";

                using (var command = new SqliteCommand(selectSql, _connection))
                {
                    command.Parameters.AddWithValue("@Date", dateString);

                    using (var reader = command.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            string processName = reader.GetString(0);
                            DateTime startTime = DateTime.Parse(reader.GetString(1));
                            long totalDurationMs = reader.GetInt64(2);

                            // Create an aggregated record
                            AppUsageRecord record = AppUsageRecord.CreateAggregated(processName, date);
                            record.StartTime = startTime;
                            record._accumulatedDuration = TimeSpan.FromMilliseconds(totalDurationMs);

                            records.Add(record);
                        }
                    }
                }

                _connection.Close();
                System.Diagnostics.Debug.WriteLine($"Retrieved {records.Count} aggregated records for date {dateString}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error retrieving aggregated records: {ex.Message}");
                throw;
            }

            return records;
        }

        /// <summary>
        /// Gets usage data for a date range, for generating reports
        /// </summary>
        public List<(string ProcessName, TimeSpan TotalDuration)> GetUsageReportForDateRange(DateTime startDate, DateTime endDate)
        {
            ThrowIfDisposed();

            List<(string, TimeSpan)> usageData = new List<(string, TimeSpan)>();
            string startDateString = startDate.ToString("yyyy-MM-dd");
            string endDateString = endDate.ToString("yyyy-MM-dd");

            try
            {
                _connection.Open();

                string selectSql = @"
                SELECT 
                    ProcessName, 
                    SUM(AccumulatedDuration) as TotalDuration
                FROM AppUsageRecords 
                WHERE Date >= @StartDate AND Date <= @EndDate
                GROUP BY ProcessName
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
            ThrowIfDisposed();

            try
            {
                DateTime cutoffDate = DateTime.Now.AddDays(-daysToKeep).Date;
                string cutoffDateString = cutoffDate.ToString("yyyy-MM-dd");

                _connection.Open();

                string deleteSql = @"DELETE FROM AppUsageRecords WHERE Date < @CutoffDate;";

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

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(DatabaseService));
            }
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _connection?.Dispose();
                _disposed = true;
            }
        }
    }
} 