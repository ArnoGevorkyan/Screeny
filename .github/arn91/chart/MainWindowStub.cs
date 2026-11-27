using System.Collections.ObjectModel;
using LiveChartsCore.SkiaSharpView.WinUI;
using ScreenTimeTracker.Models;
using ScreenTimeTracker.Services;

namespace ScreenTimeTracker;

public sealed partial class MainWindow
{
    private readonly ObservableCollection<AppUsageRecord> _usageRecords = new();
    private readonly DatabaseService _databaseService;
    private readonly WindowTrackingService _trackingService;
    private readonly ChartState _viewModel = new();
    internal CartesianChart UsageChartLive { get; } = new();
    internal ViewElement ChartTimeValue { get; } = new();
    internal ObservableCollection<AppUsageRecord> AppList => _usageRecords;
    internal DateTime SelectedDate => _viewModel.SelectedDate;
    private readonly ViewElement DateDisplay = new(), SummaryTitle = new(), AveragePanel = new(),
        ViewModeLabel = new(), ViewModePanel = new();
    internal ViewElement? Content { get; set; }
    private readonly QueueStub DispatcherQueue = new();
    private bool _isReloading;
    private bool _isChartDirty;
    private bool _disposed;
    private int _autoSaveCycleCount;

    internal MainWindow(DatabaseService database, WindowTrackingService tracking)
    {
        _databaseService = database;
        _trackingService = tracking;
    }

    internal void Load(DateTime date)
    {
        _isReloading = true;
        LoadRecordsForDate(date);
        Assert.IsFalse(_isReloading);
    }
    internal void ReceiveTrackedVisit(AppUsageRecord record) => UpdateOrAddLiveRecord(record);
    internal void Render() => UpdateUsageChart();
    internal void PeriodicRedraw() => DoAutoSave();
    internal void Refresh()
    {
        _isChartDirty = true;
        DoChartRefresh();
        Assert.IsFalse(_isChartDirty);
    }
    private void UpdateDatePickerButtonText() { }
    internal ViewElement TotalScreenTime { get; } = new();
    internal ViewElement MostUsedApp { get; } = new();
    internal ViewElement MostUsedAppTime { get; } = new();
    internal ViewElement IdleRow { get; } = new();
    internal ViewElement IdleTimeValue { get; } = new();
    internal ViewElement DailyAverage { get; } = new();
    internal void LoadRange(DateTime start, DateTime end) => LoadRecordsForDateRange(start, end);
    private void ShowErrorDialog(string message) => throw new InvalidOperationException(message);
    private void CleanupSystemProcesses() { }

    internal sealed class ViewElement
    {
        private string? _text;
        public string? Text
        {
            get => _text;
            set
            {
                _text = value;
                NextText?.TrySetResult(value);
            }
        }
        public TaskCompletionSource<string?>? NextText { get; set; }
        public Microsoft.UI.Xaml.Visibility Visibility { get; set; }
        public object XamlRoot { get; } = new();
    }
    private sealed class QueueStub
    {
        public void TryEnqueue(Action callback) => callback();
        public bool TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority priority, Action callback)
        {
            callback();
            return true;
        }
    }
    private sealed class ChartState
    {
        public ChartViewMode CurrentChartViewMode { get; set; } = ChartViewMode.Hourly;
        public TimePeriod CurrentTimePeriod { get; set; } = TimePeriod.Daily;
        public DateTime SelectedDate { get; set; }
        public DateTime? SelectedEndDate { get; set; }
        public bool IsDateRangeSelected { get; set; }
        public ObservableCollection<AppUsageRecord> AggregatedRecords { get; } = [];
    }
}
