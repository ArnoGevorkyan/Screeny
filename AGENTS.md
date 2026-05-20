# Repository Guidelines

## Project Structure & Module Organization

Screeny is a privacy-focused WinUI 3 desktop app targeting `.NET 8` and Windows App SDK. Core files live at the repository root:

- `App.xaml` / `App.xaml.cs`: application startup, logging, startup-mode detection, and global exception handling.
- `MainWindow.xaml`, `MainWindow.xaml.cs`, `MainWindow.Logic.cs`, `MainWindow.UI.cs`: UI, lifecycle, tracking coordination, charts, tray behavior, and power notifications.
- `Services/`: active-window tracking, SQLite persistence, and icon loading.
- `Models/`: usage records, finalized usage slices, enums, filters, and observable base types.
- `Helpers/`: charting, Win32, tray, date picker, window utilities, name normalization, and time formatting.
- `ScreenTimeTracker.Tests/`: MSTest coverage for pure helpers and domain contracts that can run without launching WinUI.
- `Assets/`: app icons, splash images, and Store assets.
- `.agents/skills/`: optional local agent skills installed for this repo.

Follow `RELIABILITY_PLAN.md` for reliability sequencing. Prefer reliability and data correctness over visual churn.

## Build, Test, And Development Commands

- `dotnet build ScreenTimeTracker.sln`: restores and builds the app plus test project.
- `dotnet test ScreenTimeTracker.sln`: runs the MSTest suite without launching WinUI.
- `dotnet build ScreenTimeTracker.csproj`: builds only the WinUI app.
- `dotnet run --project ScreenTimeTracker.csproj`: launches the app locally when Windows desktop prerequisites are available.
- `dotnet publish ScreenTimeTracker.csproj -c Release`: creates release output for distribution.
- `dotnet list package`: prints direct NuGet package references.
- `dotnet list package --outdated`: package audit only; do not upgrade before reliability tests protect the risky paths.
- `dotnet list package --vulnerable --include-transitive`: dependency vulnerability audit.

The app project is x64-only and uses MSIX packaging in `ScreenTimeTracker.csproj` and `Package.appxmanifest`. The test project may build as Any CPU internally, but the solution should remain x64-only for the app.

## Coding Style & Naming Conventions

Use nullable-aware C# with four-space indentation. Prefer clear PascalCase for public types, methods, and properties; use `_camelCase` for private fields as already seen in `MainWindow` and services.

Keep changes scoped and follow existing WinUI patterns. Avoid WPF APIs. Prefer structured APIs and parameterized SQLite commands over string-built data handling. No formatter or lint command is configured, so match surrounding code.

Use `ProcessName` as stable application identity. Use `ApplicationName` and `WindowTitle` for friendly display text. Do not replace stable process identity with transient window titles.

## Reliability Rules

- Persist only finalized usage slices. Live UI records may be displayed, but open mutable records should not be written as durable history.
- `WindowTrackingService` owns active slice lifecycle. Finalized intervals should flow through `UsageSliceFinalized`.
- Avoid duplicate writes from stop, suspend, exit, autosave, idle, and window-change paths.
- Open-slice autosave is disabled until an idempotent checkpoint key exists.
- Every finalized slice must have `EndTime > StartTime`, bounded duration, and deterministic `Date`.
- Keep stored records and live overlays separate until query or aggregation time.
- Treat sleep, display off, away mode, idle, resume, reset, and tray exit as lifecycle-risk areas.

## Testing Guidelines

Tests currently use MSTest in `ScreenTimeTracker.Tests`. The suite covers pure helper/model logic such as `ProcessFilter`, `TimeFormatter`, `ApplicationNameNormalizer`, and `UsageSlice`.

For tracking, persistence, aggregation, migration, or lifecycle changes:

- Add focused tests first when the code can be tested without launching WinUI.
- Prefer small pure helpers or domain types when extracting testable behavior from WinUI code.
- Suggested test names: `MethodName_State_ExpectedBehavior`.
- At minimum, run `dotnet build ScreenTimeTracker.sln` and `dotnet test ScreenTimeTracker.sln`.
- Manually verify startup, active-window tracking, date ranges, reset data, tray behavior, suspend/resume, and packaged launch when relevant.

Do not broaden the UI or MVVM architecture just to make tests easier. Extract only the smallest stable contract needed for the reliability change.

## AI-Assisted Workflow

Use a gstack-style workflow with Codex for substantial work:

1. Think: clarify the user-visible goal and constraints.
2. Plan: update a small checklist before broad changes.
3. Build: keep edits scoped and avoid UI redesign unless requested.
4. Review: check for reliability, data loss, duplicate counting, privacy, and lifecycle regressions.
5. Test: run `dotnet build ScreenTimeTracker.sln` and `dotnet test ScreenTimeTracker.sln`; manually verify relevant WinUI/tray/startup flows.
6. Ship: summarize changes, verification, and remaining risk.

Relevant gstack skills for this repo:

- `plan-eng-review`: use before broad reliability architecture changes.
- `investigate`: use for tracking, persistence, startup, or lifecycle bugs.
- `review`: use before landing code.
- `cso`: use for privacy, capabilities, certificates, and local-only checks.
- `ship`: use only after build, tests, and relevant smoke checks pass.

Prefer project-specific guidance in this file and `RELIABILITY_PLAN.md` over generic .NET skill advice when they conflict.

## Commit & Pull Request Guidelines

Commit history uses concise imperative summaries, for example `Add reliability test foundation` or `Persist finalized usage slices`.

Keep commits narrow and describe the user-visible or architectural outcome. Pull requests should include:

- Short summary.
- Testing performed.
- Affected screens or workflows.
- Screenshots for visible UI changes.
- Any database schema, privacy, packaging, startup, or lifecycle behavior changes.

Do not include generated output, logs, local databases, certificates, `bin/`, `obj/`, or test result artifacts.

## Security & Configuration Tips

Screeny’s privacy promise is local-only tracking. Do not add network access, telemetry, analytics, cloud sync, or remote backup without updating `PRIVACY.md`, `README.md`, `Package.appxmanifest`, and this file.

Keep signing certificates, logs, database files, `bin/`, `obj/`, `TestResults/`, and coverage outputs out of source control. `runFullTrust` is expected for desktop foreground-window tracking; `internetClient` should stay absent unless a deliberate network feature is added and documented.
