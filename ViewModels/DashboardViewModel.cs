using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using ScreenTimeTracker.Models;
using ScreenTimeTracker.Services;

namespace ScreenTimeTracker.ViewModels
{
    public class DashboardViewModel : INotifyPropertyChanged
    {
        private readonly DatabaseService _databaseService;
        private DateTime _selectedDate;
        private ObservableCollection<ISeries> _appUsageSeries;
        private ObservableCollection<ISeries> _weeklyUsageSeries;
        private List<string> _labels;
        private List<string> _weeklyLabels;
        private TimeSpan _totalScreenTime;
        private TimeSpan _averageScreenTime;
        private string _mostUsedApp;
        private TimeSpan _mostUsedAppTime;
        private ObservableCollection<AppUsageRecord> _topApps;
        private string _timeRangeSummary;
        
        public event PropertyChangedEventHandler? PropertyChanged;

        public DashboardViewModel(DatabaseService databaseService)
        {
            _databaseService = databaseService;
            _selectedDate = DateTime.Today;
            _appUsageSeries = new ObservableCollection<ISeries>();
            _weeklyUsageSeries = new ObservableCollection<ISeries>();
            _labels = new List<string>();
            _weeklyLabels = new List<string>();
            _topApps = new ObservableCollection<AppUsageRecord>();
            
            // Initialize with today's data
            LoadDataForDate(_selectedDate);
            LoadWeeklyData();
        }

        public DateTime SelectedDate
        {
            get => _selectedDate;
            set
            {
                if (_selectedDate != value)
                {
                    _selectedDate = value;
                    OnPropertyChanged();
                    LoadDataForDate(_selectedDate);
                }
            }
        }

        public ObservableCollection<ISeries> AppUsageSeries 
        { 
            get => _appUsageSeries;
            private set
            {
                if (_appUsageSeries != value)
                {
                    _appUsageSeries = value;
                    OnPropertyChanged();
                }
            }
        }

        public ObservableCollection<ISeries> WeeklyUsageSeries 
        { 
            get => _weeklyUsageSeries;
            private set
            {
                if (_weeklyUsageSeries != value)
                {
                    _weeklyUsageSeries = value;
                    OnPropertyChanged();
                }
            }
        }

        public List<string> Labels 
        { 
            get => _labels;
            private set
            {
                if (_labels != value)
                {
                    _labels = value;
                    OnPropertyChanged();
                }
            }
        }

        public List<string> WeeklyLabels 
        { 
            get => _weeklyLabels;
            private set
            {
                if (_weeklyLabels != value)
                {
                    _weeklyLabels = value;
                    OnPropertyChanged();
                }
            }
        }

        public TimeSpan TotalScreenTime 
        { 
            get => _totalScreenTime;
            private set
            {
                if (_totalScreenTime != value)
                {
                    _totalScreenTime = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FormattedTotalScreenTime));
                }
            }
        }

        public string FormattedTotalScreenTime => FormatTimeSpan(TotalScreenTime);

        public TimeSpan AverageScreenTime 
        { 
            get => _averageScreenTime;
            private set
            {
                if (_averageScreenTime != value)
                {
                    _averageScreenTime = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FormattedAverageScreenTime));
                }
            }
        }

        public string FormattedAverageScreenTime => FormatTimeSpan(AverageScreenTime);

        public string MostUsedApp 
        { 
            get => _mostUsedApp;
            private set
            {
                if (_mostUsedApp != value)
                {
                    _mostUsedApp = value;
                    OnPropertyChanged();
                }
            }
        }

        public TimeSpan MostUsedAppTime 
        { 
            get => _mostUsedAppTime;
            private set
            {
                if (_mostUsedAppTime != value)
                {
                    _mostUsedAppTime = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(FormattedMostUsedAppTime));
                }
            }
        }

        public string FormattedMostUsedAppTime => FormatTimeSpan(MostUsedAppTime);

        public ObservableCollection<AppUsageRecord> TopApps 
        { 
            get => _topApps;
            private set
            {
                if (_topApps != value)
                {
                    _topApps = value;
                    OnPropertyChanged();
                }
            }
        }

        public string TimeRangeSummary
        {
            get => _timeRangeSummary;
            private set
            {
                if (_timeRangeSummary != value)
                {
                    _timeRangeSummary = value;
                    OnPropertyChanged();
                }
            }
        }

        private void LoadDataForDate(DateTime date)
        {
            try
            {
                // Get data from database
                var records = _databaseService.GetAggregatedRecordsForDate(date);

                // Order by duration (descending)
                var orderedRecords = records
                    .Where(r => !IsSystemProcess(r.ProcessName))
                    .OrderByDescending(r => r.Duration)
                    .ToList();

                // Get top 5 apps for pie chart
                var top5Apps = orderedRecords.Take(5).ToList();
                
                // Calculate total screen time
                TimeSpan totalDuration = TimeSpan.Zero;
                foreach (var record in orderedRecords)
                {
                    totalDuration += record.Duration;
                }
                TotalScreenTime = totalDuration;

                // Set most used app
                if (orderedRecords.Any())
                {
                    var mostUsed = orderedRecords.First();
                    MostUsedApp = mostUsed.ProcessName;
                    MostUsedAppTime = mostUsed.Duration;
                }
                else
                {
                    MostUsedApp = "None";
                    MostUsedAppTime = TimeSpan.Zero;
                }

                // Create pie chart data
                var series = new ObservableCollection<ISeries>();
                var labels = new List<string>();

                // Add the top 5 apps
                var pieValues = new List<double>();
                foreach (var app in top5Apps)
                {
                    double percentage = totalDuration.TotalMinutes > 0 
                        ? app.Duration.TotalMinutes / totalDuration.TotalMinutes * 100 
                        : 0;
                        
                    pieValues.Add(Math.Round(percentage, 1));
                    labels.Add(app.ProcessName);
                }

                // Update the top apps collection
                TopApps = new ObservableCollection<AppUsageRecord>(top5Apps);

                // Create the pie series
                var pieSeries = new PieSeries<double>
                {
                    Values = pieValues,
                    Name = "App Usage",
                    DataLabelsSize = 12,
                    DataLabelsPosition = LiveChartsCore.Measure.PolarLabelsPosition.Outer,
                    DataLabelsFormatter = point => $"{point.PrimaryValue}%",
                    Pushout = 5,
                    InnerRadius = 50,
                    Fill = null
                };

                series.Add(pieSeries);
                AppUsageSeries = series;
                Labels = labels;

                // Update time range summary
                TimeRangeSummary = $"Usage data for {date.ToString("D")}";
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading dashboard data: {ex.Message}");
            }
        }

        private void LoadWeeklyData()
        {
            try
            {
                // Get start and end date for the week
                DateTime startOfWeek = _selectedDate.AddDays(-(int)_selectedDate.DayOfWeek);
                DateTime endOfWeek = startOfWeek.AddDays(6);

                // Dictionary to store daily usage totals
                var dailyTotals = new Dictionary<DateTime, TimeSpan>();
                var weeklyLabels = new List<string>();
                var weeklyValues = new List<double>();

                // Initialize days of the week
                for (int i = 0; i < 7; i++)
                {
                    var day = startOfWeek.AddDays(i);
                    dailyTotals[day] = TimeSpan.Zero;
                    weeklyLabels.Add(day.ToString("ddd"));
                }

                // Calculate average usage over past week
                TimeSpan weeklyTotal = TimeSpan.Zero;
                int daysWithUsage = 0;

                // For each day in the week
                for (int i = 0; i < 7; i++)
                {
                    var day = startOfWeek.AddDays(i);
                    var dayRecords = _databaseService.GetAggregatedRecordsForDate(day);
                    
                    // Skip system processes
                    dayRecords = dayRecords.Where(r => !IsSystemProcess(r.ProcessName)).ToList();
                    
                    // Sum up all app usage for the day
                    TimeSpan dayTotal = TimeSpan.Zero;
                    foreach (var record in dayRecords)
                    {
                        dayTotal += record.Duration;
                    }
                    
                    // Store daily total
                    dailyTotals[day] = dayTotal;
                    
                    // Add to weekly total if there was usage
                    if (dayTotal > TimeSpan.Zero)
                    {
                        weeklyTotal += dayTotal;
                        daysWithUsage++;
                    }
                    
                    // Add to weekly chart values (in hours)
                    weeklyValues.Add(Math.Round(dayTotal.TotalHours, 1));
                }

                // Calculate average (if we have days with usage)
                if (daysWithUsage > 0)
                {
                    AverageScreenTime = TimeSpan.FromTicks(weeklyTotal.Ticks / daysWithUsage);
                }
                else
                {
                    AverageScreenTime = TimeSpan.Zero;
                }

                // Create column series for weekly data
                var columnSeries = new ColumnSeries<double>
                {
                    Values = weeklyValues,
                    Stroke = null,
                    Fill = new SolidColorPaint(SKColors.DodgerBlue),
                    Name = "Daily Screen Time (hours)"
                };

                var series = new ObservableCollection<ISeries> { columnSeries };
                WeeklyUsageSeries = series;
                WeeklyLabels = weeklyLabels;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Error loading weekly data: {ex.Message}");
            }
        }

        private bool IsSystemProcess(string processName)
        {
            if (string.IsNullOrEmpty(processName)) return false;
            
            // Common Windows system process names we want to ignore
            string[] systemProcesses = {
                "explorer", "SearchHost", "ShellExperienceHost", "StartMenuExperienceHost",
                "ApplicationFrameHost", "SystemSettings", "TextInputHost", "WindowsTerminal",
                "cmd", "powershell", "pwsh", "conhost", "WinStore.App", "LockApp", "LogonUI",
                "fontdrvhost", "dwm", "csrss", "services", "svchost", "taskhostw", "ctfmon",
                "rundll32", "dllhost", "sihost", "taskmgr", "backgroundtaskhost", "smartscreen",
                "SecurityHealthService", "Registry", "MicrosoftEdgeUpdate", "WmiPrvSE", "spoolsv",
                "TabTip", "TabTip32", "SearchUI", "SearchApp", "RuntimeBroker", "SettingsSyncHost",
                "WUDFHost"
            };
            
            // Check if the processName is in our list
            return systemProcesses.Contains(processName.ToLower());
        }

        private string FormatTimeSpan(TimeSpan timeSpan)
        {
            if (timeSpan.TotalDays >= 1)
            {
                return $"{(int)timeSpan.TotalDays}d {timeSpan.Hours}h {timeSpan.Minutes}m";
            }
            else if (timeSpan.TotalHours >= 1)
            {
                return $"{(int)timeSpan.TotalHours}h {timeSpan.Minutes}m";
            }
            else if (timeSpan.TotalMinutes >= 1)
            {
                return $"{(int)timeSpan.TotalMinutes}m {timeSpan.Seconds}s";
            }
            else
            {
                return $"{timeSpan.Seconds}s";
            }
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }
} 