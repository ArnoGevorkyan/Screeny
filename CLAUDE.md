# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Screeny is a privacy-focused Windows screen time tracker built with WinUI 3 and the Windows App SDK. It tracks which applications are in focus and provides visualizations of usage patterns. All data stays local - no internet connection required.

## Build and Development Commands

### Building
```bash
dotnet build
```

### Running the Application
```bash
dotnet run
```

### Publishing (for distribution)
```bash
dotnet publish -c Release
```

## Architecture

### Core Components

**MainWindow** - Split across multiple files:
- `MainWindow.xaml.cs` - Main window logic, event handling, and initialization
- `MainWindow.Logic.cs` - Core business logic and data processing
- `MainWindow.UI.cs` - UI-specific methods and chart updates

**Services Layer:**
- `WindowTrackingService` - Tracks active windows using Win32 APIs and WinEvent hooks
- `DatabaseService` - SQLite database operations and data aggregation for storing usage records
- `IconLoader` - Handles loading and caching of application icons

**Models:**
- `AppUsageRecord` - Core data model representing time spent in applications
- `ChartViewMode` - Enum for hourly vs daily chart views
- `TimePeriod` - Enum for different time period selections

**Key Patterns:**
- Uses ObservableCollection for data binding to UI
- MVVM pattern with MainViewModel
- Async/await for UI operations to prevent blocking
- Win32 P/Invoke for system integration
- Tray icon support for background operation

### Data Flow

1. `WindowTrackingService` monitors active windows via Win32 hooks
2. Usage data is stored in SQLite via `DatabaseService`
3. `DatabaseService` processes and aggregates raw data for chart display
4. UI updates through data binding to ObservableCollections
5. Charts refresh on a timer to balance performance and accuracy

### Database

- SQLite database stored in `%LocalAppData%/ScreenTimeTracker/screentime.db`
- Handles database corruption with automatic recovery
- In-memory fallback if database fails to initialize

## Key Technologies

- **.NET 8** with Windows target framework
- **WinUI 3** for modern Windows UI
- **SQLite** for local data storage
- **LiveCharts** and **ScottPlot** for data visualization
- **Win32 APIs** for window tracking and system integration

## Development Notes

- The application targets Windows 11+ (minimum Windows 10 22000)
- Uses self-contained deployment with Windows App SDK
- Packaged as MSIX for Microsoft Store distribution
- Requires x64 architecture
- Icons are loaded asynchronously to prevent UI blocking
- Power management integration to pause tracking during sleep

## Code Cleanup Guidelines

**COMPLEXITY WARNING SYSTEM** - Always check these before making ANY change:

### 🚨 STOP IMMEDIATELY IF:
- Creating new files for a cleanup task
- Adding more code than removing (patches over bugs)
- Modifying working code that isn't causing actual problems
- Need to change >3 files for one logical fix
- Spending >10 minutes on a "simple" cleanup
- Creating abstractions where none existed before

### ⚠️ WARNING SIGNS:
- Thinking "this would be better if I also..."
- Solving problems that don't actually exist  
- Making changes "for consistency" without real benefit
- Adding helper methods to "organize" working code

### ✅ GOOD CHANGES:
- Deleting dead/commented code
- Fixing actual performance problems
- Removing code that serves no purpose
- Simple, obvious fixes with clear user benefit

### 🎯 GOLDEN RULE:
**If you have to think hard about whether the change is worth it, it's not.**

The goal is to SUBTRACT problems, not add solutions to non-problems.