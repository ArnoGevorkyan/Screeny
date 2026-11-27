# Build and test Screeny

## Build on Windows

Use Windows 11 x64 for local development. Screeny is a WinUI 3 desktop app, so a Linux build doesn't replace a Windows build.

1. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0). For the exact SDK version CI uses, check the [Windows build workflow](../.github/workflows/windows-build.yml). The [app project](../ScreenTimeTracker.csproj) declares the target framework, Windows App SDK, and Windows SDK build tools.
2. From the repository root, run the same x64 build command as CI in PowerShell:

   ```powershell
   dotnet build ScreenTimeTracker.sln -p:Platform=x64 -p:GenerateAppxPackageOnBuild=false -p:AppxPackageSigningEnabled=false
   ```

   Pull requests to `master` run this command on a Windows runner. It restores packages and builds the [solution](../ScreenTimeTracker.sln) without generating or signing an MSIX. It doesn't install or launch the app.

## Run the local tests

A clean checkout has no test project. The `ScreenTimeTracker.Tests/` directory is [ignored](../.gitignore) and isn't part of the solution or CI. If your checkout has the local MSTest project, run:

```powershell
dotnet test ScreenTimeTracker.Tests/ScreenTimeTracker.Tests.csproj
```

These tests cover helper and model behavior without launching WinUI. They can't verify window bindings, tray interaction, power events, or packaged startup.

## Check packaged behavior

For local packaged debugging, use Visual Studio 2026 with the WinUI application development workload and enable Windows Developer Mode. Open the solution, select `x64`, and choose the **ScreenTimeTracker (MsixPackage)** [launch profile](../Properties/launchSettings.json).

The default signing configuration expects a certificate that isn't in the repo. Use a separate test package and follow [Microsoft's MSIX signing guide](https://learn.microsoft.com/en-us/windows/msix/package/sign-app-package-using-signtool). Leave the Store identity in [Package.appxmanifest](../Package.appxmanifest) unchanged. The CI build doesn't produce a signed package or submit anything to the Store.

Use a disposable Windows profile for checks that can affect saved history. For the paths your change touches, check normal and Windows startup, tracking across app changes, date ranges, deletion, tray show and exit, and suspend and resume. Check packaged launch separately from the build.

## Inspect dependencies

Run these commands from the repository root to list direct references, check for updates, or audit direct and transitive dependencies:

```powershell
dotnet package list --project ScreenTimeTracker.csproj
dotnet package list --project ScreenTimeTracker.csproj --outdated
dotnet package list --project ScreenTimeTracker.csproj --vulnerable --include-transitive
```

See the [repository guidelines](../AGENTS.md) for testing and change requirements.
