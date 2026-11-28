# How Screeny tracks and displays usage

## Startup and tracking

[App.xaml.cs](../App.xaml.cs) initializes SQLite and creates the window. Windows startup can leave the window hidden, but [MainWindow.xaml.cs](../MainWindow.xaml.cs) still initializes tracking and the notification area icon. [MainWindow.Logic.cs](../MainWindow.Logic.cs) combines the user's tracking choice with temporary pause reasons such as sleep and display off.

[WindowTrackingService](../Services/WindowTrackingService.cs) observes the foreground window and owns the open usage record. It publishes snapshots for live display. When an interval ends, the tracker emits a `UsageSliceFinalized` event with a [UsageSlice](../Models/UsageSlice.cs).

## Local history

[MainWindow.UI.cs](../MainWindow.UI.cs) sends finalized slices to [UsageSliceWriter](../Services/UsageSliceWriter.cs). The writer queues slices in memory and retries when SQLite returns a retryable error, such as a busy database. Open records are never written as history.

[DatabaseService](../Services/DatabaseService.cs) owns the SQLite schema, migrations, and history queries. Each saved row holds the process name, application name, interval times, and duration. The tracker reads the foreground window title, but the SQLite insert doesn't store it.

The database path in code is `%LOCALAPPDATA%\ScreenTimeTracker\screentime.db`. Windows can redirect that path for packaged installs. Usage history stays on the device.

## Dashboard

[MainWindow.xaml](../MainWindow.xaml) defines the dashboard. [MainViewModel.cs](../MainViewModel.cs) holds the selected dates, tracking status, and commands. The [date picker](../Helpers/DatePickerPopup.cs) supplies a selected day or preset range. The window updates the summaries and charts with [ChartHelper](../Helpers/ChartHelper.cs). When a view includes live usage, Screeny combines it with stored history at read time.

Hiding or minimizing the window suspends dashboard reads, redraws, and icon retries, not foreground tracking or queued history writes. Showing it again reloads the selected period once, even when tracking is paused. Short finalized visits count in the headline totals and active-day average even when the daily list filters out an app row.

See the [repository guidelines](../AGENTS.md#reliability-rules) before changing tracking, storage, or aggregation.
