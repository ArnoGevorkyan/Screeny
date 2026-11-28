# Build and test Screeny

## Build on Windows

Use Windows 11 x64 for local development. Screeny is a WinUI 3 desktop app, so a Linux build doesn't replace a Windows build.

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). For the exact SDK version CI uses, check the [Windows build workflow](../.github/workflows/windows-build.yml). The [app project](../ScreenTimeTracker.csproj) declares the target framework, Windows App SDK, and Windows SDK build tools.
2. From the repository root, run the same x64 build command as CI in PowerShell:

   ```powershell
   dotnet build ScreenTimeTracker.sln -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false -p:AppxPackageSigningEnabled=false
   ```

   Pull requests to `master` run this command on a Windows runner. It restores packages and builds the [solution](../ScreenTimeTracker.sln) without generating or signing an MSIX. It doesn't install or launch the app.

## Run the core tests

Run the portable suite from the repository root with the .NET 10 SDK:

```sh
dotnet test ScreenTimeTracker.Core.Tests/ScreenTimeTracker.Core.Tests.csproj
```

CI runs this command in the same Windows job after the x64 build. The tests compile the tracking and storage sources without WinUI. They check finalized-only writes, duplicate offers after reopening the ledger, date-range totals, and retry order after a single SQLite write lock. Each test uses synthetic records and a temporary database.

The suite does not check Windows foreground hooks, idle or power messages, the UI or tray, packaged startup, or sustained SQLite contention.

If you have the optional local project in the [ignored](../.gitignore) `ScreenTimeTracker.Tests/` directory, also run `dotnet test ScreenTimeTracker.Tests/ScreenTimeTracker.Tests.csproj`. CI does not run it.

## Check packaged behavior

For local packaged debugging, use Visual Studio 2026 with the WinUI application development workload and enable Windows Developer Mode. Open the solution, select `x64`, and choose the **ScreenTimeTracker (MsixPackage)** [launch profile](../Properties/launchSettings.json).

The default signing configuration expects a certificate that isn't in the repo. Use a separate test package and follow [Microsoft's MSIX signing guide](https://learn.microsoft.com/en-us/windows/msix/package/sign-app-package-using-signtool). Leave the Store identity in [Package.appxmanifest](../Package.appxmanifest) unchanged. The CI build doesn't produce a signed package or submit anything to the Store.

Use a disposable Windows profile for checks that can affect saved history. For the paths your change touches, check normal and Windows startup, tracking across app changes, date ranges, deletion, tray show and exit, and suspend and resume. Check packaged launch separately from the build.

For a selected-history read failure, verify that a UI Automation listener receives `LiveRegionChanged` from `HistoryStatus` with the retry text already set. Reading the text and its polite setting is not an event check. Test Narrator speech separately.

## Inspect dependencies

Run these commands from the repository root to list direct references, check for updates, or audit direct and transitive dependencies:

```powershell
dotnet package list --project ScreenTimeTracker.csproj
dotnet package list --project ScreenTimeTracker.csproj --outdated
dotnet package list --project ScreenTimeTracker.csproj --vulnerable --include-transitive
```

See the [repository guidelines](../AGENTS.md) for testing and change requirements.
