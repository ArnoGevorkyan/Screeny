using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ScreenTimeTracker.Models;
using ScreenTimeTracker.Helpers;

namespace ScreenTimeTracker
{
    /// <summary>
    /// Minimal ViewModel that will progressively absorb state from <see cref="MainWindow"/>.
    /// For now it only exposes the records collection and the selected date plus two placeholder
    /// commands that will get wired up later. Keeping it small ensures the project compiles at
    /// every incremental refactor step.
    /// </summary>
    public sealed class MainViewModel : INotifyPropertyChanged
    {
        // Domain collection to which the UI ListView/Chart will bind
        public ObservableCollection<AppUsageRecord> Records { get; } = new();

        // Aggregated (merged) records used for summary/chart views
        public ObservableCollection<AppUsageRecord> AggregatedRecords { get; } = new();

        private DateTime _selectedDate = DateTime.Today;
        public DateTime SelectedDate
        {
            get => _selectedDate;
            set => SetProperty(ref _selectedDate, value);
        }

        private DateTime? _selectedEndDate;
        public DateTime? SelectedEndDate
        {
            get => _selectedEndDate;
            set => SetProperty(ref _selectedEndDate, value);
        }

        private bool _isDateRangeSelected;
        public bool IsDateRangeSelected
        {
            get => _isDateRangeSelected;
            set => SetProperty(ref _isDateRangeSelected, value);
        }

        private TimePeriod _currentTimePeriod = TimePeriod.Daily;
        public TimePeriod CurrentTimePeriod
        {
            get => _currentTimePeriod;
            set => SetProperty(ref _currentTimePeriod, value);
        }

        private ChartViewMode _currentChartViewMode = ChartViewMode.Hourly;
        public ChartViewMode CurrentChartViewMode
        {
            get => _currentChartViewMode;
            set => SetProperty(ref _currentChartViewMode, value);
        }

        private bool _isTracking;
        public bool IsTracking
        {
            get => _isTracking;
            set => SetProperty(ref _isTracking, value);
        }

        private string _chartTotalTimeDisplay = "0h 0m 0s";
        public string ChartTotalTimeDisplay
        {
            get => _chartTotalTimeDisplay;
            private set => SetProperty(ref _chartTotalTimeDisplay, value);
        }

        private string _totalScreenTimeDisplay = "0h 0m";
        public string TotalScreenTimeDisplay
        {
            get => _totalScreenTimeDisplay;
            private set => SetProperty(ref _totalScreenTimeDisplay, value);
        }

        private string _mostUsedAppName = "None";
        public string MostUsedAppName
        {
            get => _mostUsedAppName;
            private set => SetProperty(ref _mostUsedAppName, value);
        }

        private string _mostUsedAppDurationDisplay = "0h 0m";
        public string MostUsedAppDurationDisplay
        {
            get => _mostUsedAppDurationDisplay;
            private set => SetProperty(ref _mostUsedAppDurationDisplay, value);
        }

        private string _idleDurationDisplay = "0h 0m";
        public string IdleDurationDisplay
        {
            get => _idleDurationDisplay;
            private set => SetProperty(ref _idleDurationDisplay, value);
        }

        private string _dailyAverageDisplay = "0h 0m";
        public string DailyAverageDisplay
        {
            get => _dailyAverageDisplay;
            private set => SetProperty(ref _dailyAverageDisplay, value);
        }

        private string _summaryTitle = "Daily Screen Time Summary";
        public string SummaryTitle
        {
            get => _summaryTitle;
            set => SetProperty(ref _summaryTitle, value);
        }

        private bool _isAverageVisible;
        public bool IsAverageVisible
        {
            get => _isAverageVisible;
            set => SetProperty(ref _isAverageVisible, value);
        }

        private string _dateDisplayText = DateTime.Today.ToString("MMM d, yyyy");
        public string DateDisplayText
        {
            get => _dateDisplayText;
            set => SetProperty(ref _dateDisplayText, value);
        }

        private bool _isViewModePanelVisible = true;
        public bool IsViewModePanelVisible
        {
            get => _isViewModePanelVisible;
            set => SetProperty(ref _isViewModePanelVisible, value);
        }

        private bool _isIdleRowVisible;
        public bool IsIdleRowVisible
        {
            get => _isIdleRowVisible;
            private set => SetProperty(ref _isIdleRowVisible, value);
        }

        private Microsoft.UI.Xaml.Media.Imaging.BitmapImage? _mostUsedAppIcon;
        public Microsoft.UI.Xaml.Media.Imaging.BitmapImage? MostUsedAppIcon
        {
            get => _mostUsedAppIcon;
            private set
            {
                if (SetProperty(ref _mostUsedAppIcon, value))
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasMostUsedIcon)));
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasNoMostUsedIcon)));
                }
            }
        }

        public bool HasMostUsedIcon => MostUsedAppIcon != null;
        public bool HasNoMostUsedIcon => MostUsedAppIcon == null;

        public ICommand StartTrackingCommand { get; }
        public ICommand EndTrackingCommand { get; }
        public ICommand ToggleTrackingCommand { get; }
        public ICommand PickDateCommand { get; }
        public ICommand ToggleViewModeCommand { get; }

        public MainViewModel()
        {
            StartTrackingCommand = new RelayCommand(_ => OnStartTrackingRequested?.Invoke(this, EventArgs.Empty));
            EndTrackingCommand  = new RelayCommand(_ => OnEndTrackingRequested?.Invoke(this, EventArgs.Empty));

            ToggleTrackingCommand = new RelayCommand(_ => OnToggleTrackingRequested?.Invoke(this, EventArgs.Empty));
            PickDateCommand      = new RelayCommand(_ => OnPickDateRequested?.Invoke(this, EventArgs.Empty));
            ToggleViewModeCommand = new RelayCommand(_ => OnToggleViewModeRequested?.Invoke(this, EventArgs.Empty));
        }

        // Events raised when commands fire – MainWindow will subscribe for now
        public event EventHandler? OnStartTrackingRequested;
        public event EventHandler? OnEndTrackingRequested;
        public event EventHandler? OnToggleTrackingRequested;
        public event EventHandler? OnPickDateRequested;
        public event EventHandler? OnToggleViewModeRequested;

        /// <summary>
        /// Updates the summary and chart display strings. This replaces direct UI manipulation in MainWindow.
        /// </summary>
        public void UpdateSummary(TimeSpan total, TimeSpan idle, string mostUsedName, TimeSpan mostUsedDuration, TimeSpan? dailyAverage = null, Microsoft.UI.Xaml.Media.Imaging.BitmapImage? mostUsedAppIcon = null)
        {
            TotalScreenTimeDisplay       = TimeUtil.FormatTimeSpan(total);
            ChartTotalTimeDisplay        = TimeUtil.FormatTimeSpan(total);
            IdleDurationDisplay          = TimeUtil.FormatTimeSpan(idle);
            IsIdleRowVisible             = idle.TotalSeconds >= 5;
            MostUsedAppName              = mostUsedName;
            MostUsedAppDurationDisplay   = TimeUtil.FormatTimeSpan(mostUsedDuration);
            DailyAverageDisplay          = dailyAverage.HasValue ? TimeUtil.FormatTimeSpan(dailyAverage.Value) : "";
            MostUsedAppIcon              = mostUsedAppIcon;
        }

        public void SetChartTotalTime(TimeSpan duration)
        {
            ChartTotalTimeDisplay = TimeUtil.FormatTimeSpan(duration);
        }

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (!Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
                return true;
            }

            return false;
        }
        #endregion

        /// <summary>
        /// Very small ICommand implementation to keep dependencies minimal during refactor.
        /// </summary>
        private sealed class RelayCommand : ICommand
        {
            private readonly Action<object?> _execute;
            private readonly Func<object?, bool>? _canExecute;
            public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
            {
                _execute = execute;
                _canExecute = canExecute;
            }
            public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
            public void Execute(object? parameter) => _execute(parameter);
            public event EventHandler? CanExecuteChanged;
            public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        }
    }
} 