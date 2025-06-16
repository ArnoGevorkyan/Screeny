using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using ScreenTimeTracker.Models;

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

        public ICommand StartTrackingCommand { get; }
        public ICommand StopTrackingCommand { get; }
        public ICommand ToggleTrackingCommand { get; }
        public ICommand PickDateCommand { get; }
        public ICommand ToggleViewModeCommand { get; }

        public MainViewModel()
        {
            StartTrackingCommand = new RelayCommand(_ => OnStartTrackingRequested?.Invoke(this, EventArgs.Empty));
            StopTrackingCommand  = new RelayCommand(_ => OnStopTrackingRequested?.Invoke(this, EventArgs.Empty));

            ToggleTrackingCommand = new RelayCommand(_ => OnToggleTrackingRequested?.Invoke(this, EventArgs.Empty));
            PickDateCommand      = new RelayCommand(_ => OnPickDateRequested?.Invoke(this, EventArgs.Empty));
            ToggleViewModeCommand = new RelayCommand(_ => OnToggleViewModeRequested?.Invoke(this, EventArgs.Empty));
        }

        // Events raised when commands fire – MainWindow will subscribe for now
        public event EventHandler? OnStartTrackingRequested;
        public event EventHandler? OnStopTrackingRequested;
        public event EventHandler? OnToggleTrackingRequested;
        public event EventHandler? OnPickDateRequested;
        public event EventHandler? OnToggleViewModeRequested;

        #region INotifyPropertyChanged
        public event PropertyChangedEventHandler? PropertyChanged;
        private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (!Equals(field, value))
            {
                field = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
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