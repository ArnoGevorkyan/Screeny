import sys
from pathlib import Path
src = Path(sys.argv[1])
main = (src / 'MainWindow.xaml.cs').read_text()
logic = (src / 'MainWindow.Logic.cs').read_text()
ui = (src / 'MainWindow.UI.cs').read_text()
def extract(text, signature):
    a = text.index('        ' + signature + '(')
    return text[a:text.index('\n        }', a) + len('\n        }')]
snippets = [
    extract(main, "private void HandlePowerSettingChange"),
    extract(main, 'private void TrayIcon_ExitClicked').replace('private void', 'public void', 1),
    extract(main, 'private async void OnResetDataRequested').replace('private async void', 'public async Task', 1),
    logic[logic.index('        [Flags]'):logic.index('        // Helper to retrieve current live records')].replace('private void SetTrackingRequested', 'public void SetTrackingRequested'),
    extract(logic, 'public void Dispose'),
    extract(ui, 'private void TrackingService_UsageSliceFinalized').replace('private void', 'public void', 1),
    extract(ui, 'private void ReportPersistenceHealth'),
]
Path(__file__).with_name('Extracted.cs').write_text('''using System.Diagnostics;
using ScreenTimeTracker;
using ScreenTimeTracker.Models;
using ScreenTimeTracker.Services;
using System.Collections.ObjectModel;

internal sealed class WindowHost
{
    private bool _disposed;
    private volatile int _usageGeneration;
    private readonly DatabaseService _databaseService;
    private readonly UsageSliceWriter _sliceWriter;
    public readonly WindowTrackingService _trackingService = new();
    public readonly MainViewModel _viewModel = new();
    private readonly ObservableCollection<AppUsageRecord> _usageRecords;
    private readonly FixtureTimer _updateTimer = new();
    private readonly AppWindow _appWindow = new();
    private readonly TrayIconHelper _trayIconHelper = new();
    public readonly FixtureDispatcher DispatcherQueue = new();
    public FrameworkElement Content { get; } = new();
    private object TrackingStatusButton { get; } = new();
    public WindowHost(DatabaseService db)
    {
        _databaseService = db;
        _sliceWriter = new UsageSliceWriter(db, ReportPersistenceHealth);
        _usageRecords = _viewModel.Records;
        _trackingService.UsageSliceFinalized += TrackingService_UsageSliceFinalized;
    }
    public bool IsDisposed => _disposed;
    public void Suspend() => SetTrackingPauseReason(TrackingPauseReason.Suspended, true);
    public void Resume() => SetTrackingPauseReason(TrackingPauseReason.Suspended, false);
    public void Display(byte state) => HandlePowerSettingChange(GuidConsoleDisplayState, state);
    public void Away(bool away) => HandlePowerSettingChange(GuidSystemAwayMode, away ? (byte)1 : (byte)0);
    private static readonly Guid GuidConsoleDisplayState = new("6FE69556-704A-47A0-8F24-C28D936FDA47");
    private static readonly Guid GuidSystemAwayMode = new("98A7F580-01F7-48AA-9C0F-44352C29E5C0");
    public UsageSliceWriter Writer => _sliceWriter;
    private void UpdateUsageChart(List<AppUsageRecord>? rows = null) { }
    private void UpdateSummaryTab() { }
    private void CleanupSystemProcesses() { }
    public readonly List<int> RefreshRows = new();
    private void DoChartRefresh() => RefreshRows.Add(_databaseService.GetRawRecordsForDateRange(new DateTime(2000, 1, 1), DateTime.Today.AddDays(1)).Count);
    private void UnregisterPowerNotifications() { }
    private void RestoreWindowProc() { }
    private void AppWindow_Closing(object? s, EventArgs e) { }
    private void TrackingService_UsageRecordUpdated(object? s, AppUsageRecord r) { }
    private void UpdateTimer_Tick(object? s, EventArgs e) { }
    private void MainWindow_Loaded(object? s, EventArgs e) { }
    private void TrayIcon_ShowClicked(object? s, EventArgs e) { }
    private void Activate() { }
    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(WindowHost)); }
''' + '\n'.join(snippets) + '\n}\n')
