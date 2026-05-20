# Screeny Reliability Execution Plan

This is a reliability-first plan for Screeny. It keeps the current WinUI surface familiar and focuses on the contracts that decide whether users can trust the recorded time: startup/lifecycle, finalized tracking slices, SQLite persistence, and local-only packaging.

## Execution Rules

- Keep UI changes limited to clearer state/error reporting needed for reliability.
- Do not upgrade packages until tests protect tracking, persistence, aggregation, and lifecycle behavior.
- Do not add telemetry, sync, analytics, or network behavior.
- Treat time records as private local data; logs must not expose full usage history.
- Every phase must end with `dotnet build`; once tests exist, every phase must also end with `dotnet test`.
- Prefer small changes with one user-visible reliability outcome per PR.
- Treat the .NET 10 move as a runtime/framework migration, not a routine package bump.

## Current Risk Snapshot

1. Startup/background launch depends on `MainWindow_Loaded`, but startup mode creates the window without activating it. If `Loaded` does not fire, tracking, tray setup, power notifications, and `StartTracking()` do not run.
2. Persistence has multiple write paths: `WindowTrackingService.RecordReadyForSave`, `MainWindow.SaveRecordsToDatabase`, stop, suspend, exit, autosave, idle, and window-change paths can all touch the same active slice.
3. `AppUsageRecord.Duration` can double count because the timer writes `_accumulatedDuration = now - StartTime` while `IsFocused` can still make the getter add live focus time again.
4. `ApplicationProcessingHelper` mutates `ProcessName` into a display label/window title. That mixes stable identity with UI naming and can fragment one app into many rows.
5. Fresh SQLite databases create the latest columns but leave `PRAGMA user_version` at `0`, then migrations try to add columns that already exist.
6. `DatabaseService` mixes a shared `_connection` with per-operation connections, has no busy timeout policy, and has a fake in-memory fallback that most write/read methods skip.
7. The manifest declares `internetClient` while README and PRIVACY promise no internet connection.
8. Root `.pfx` files exist in the workspace even though `.gitignore` correctly excludes certificates. Confirm they are not tracked before shipping.

Mitigated so far:
- Stable process identity no longer uses transient window titles.
- `internetClient` has been removed from the package manifest.
- `MainWindow` persistence now uses finalized `UsageSlice` events instead of grouped live-record saves.
- Current migrations check for existing columns before altering schema, and fresh schemas are marked with the latest `user_version` only after required columns exist.

## Target Architecture

```text
Win32 foreground/idle/power events
        |
        v
WindowTrackingService
  owns active slice state
  emits FinalizedUsageSlice exactly once
        |
        v
UsagePersistenceService or DatabaseService.SaveFinalizedSlice
  idempotent insert/update
  isolated SQLite connection per operation
        |
        v
SQLite app_usage rows
        |
        v
query/aggregation layer + live overlay for UI only
```

The main rule: live records may be displayed, but only finalized slices are stored. Autosave may checkpoint the current open slice only if it is explicitly idempotent.

## Phase 0: Baseline And Safety Gates

- Run `dotnet build`.
- Run `dotnet --list-sdks` and confirm the SDKs available on the development machine.
- Run `dotnet list package` and save the direct package snapshot in this plan or release notes.
- Run outdated/vulnerability package checks only as an audit artifact; do not hard-code target versions in this plan.
- Confirm `git status` after marking the repo safe for the current user if needed.
- Confirm whether `*.pfx`, `log.txt`, database files, `bin/`, and `obj/` are untracked.
- Write the manual smoke checklist before code changes.

Exit criteria:
- Build result known.
- Package baseline known.
- Sensitive/generated files accounted for.
- Manual reliability checklist ready.

## Phase 1: Test Foundation Around Pure Logic

- Add `ScreenTimeTracker.Tests`. Started on May 20, 2026 with MSTest and pure helper/model tests.
- Use MSTest or xUnit plus FluentAssertions; pick the option that builds cleanly with the WinUI project.
- Add tests for `TimeUtil`, `ProcessFilter`, and `ApplicationProcessingHelper`. Initial coverage uses `TimeFormatter`, `ProcessFilter`, and `ApplicationNameNormalizer` so tests run without launching or packaging WinUI.
- Add tests that lock down the intended difference between stable process identity and display name. Initial tests assert that stable process identity is normalized separately from window titles.
- Add tests for `AppUsageRecord` focus transitions using a controllable clock or a small test seam.
- Cover duration caps, idle anchoring, zero/negative intervals, and midnight boundaries.

Exit criteria:
- `dotnet test` runs without launching WinUI.
- Core time math and name normalization are covered before persistence refactors.

Current test baseline:
- `dotnet test ScreenTimeTracker.sln` passes with 12 tests.
- `ProcessFilterTests` covers empty/system/normal process filtering.
- `TimeFormatterTests` covers compact duration formatting and duration cap formatting.
- `ApplicationNameNormalizerTests` covers architecture suffix cleanup and keeps window titles out of stable process identity.
- `UsageSliceTests` covers finalized interval creation, missing process names, zero-length intervals, application-name fallback, and duration caps.
- Remaining Phase 1 work: add `AppUsageRecord` focus-transition coverage or replace its duration logic with tested finalized-slice/live-overlay helpers.

## Phase 2: Define The Tracking Slice Contract

- Introduce a small domain type for finalized slices, for example `UsageSlice`. Done: `UsageSlice` is immutable and covered by unit tests.
- Keep these fields stable: `ProcessName`, `ApplicationName`, `WindowTitle`, `StartTime`, `EndTime`, `Duration`, `Date`.
- Stop using mutable `AppUsageRecord` as both live UI object and persistence object. In progress: persistence now receives `UsageSlice`; live UI still uses `AppUsageRecord`.
- Make `WindowTrackingService` the only owner of active slice lifecycle. In progress: all service finalization paths now route through one `FinalizeRecord` helper.
- Make `GetRecords()` return snapshots, not live mutable references.
- Ensure every finalized slice has `EndTime > StartTime`, bounded duration, and a deterministic date.

Exit criteria:
- One codepath finalizes a foreground slice.
- One codepath finalizes an idle slice.
- Tests prove stop, window change, idle enter/exit, suspend, resume, and midnight rollover emit expected slices.

## Phase 3: Single Persistence Pipeline

- Replace `MainWindow.SaveRecordsToDatabase()` grouping writes with one persistence handler for finalized slices. Done: `MainWindow` subscribes to `UsageSliceFinalized` and calls `DatabaseService.SaveSlice`.
- Remove duplicate writes from stop/suspend/exit/autosave paths. Done: open-slice autosave writes and stop/suspend grouping writes were removed.
- Decide whether autosave is:
  - disabled for open slices, or
  - an idempotent checkpoint keyed by an active slice id.
  Current decision: disabled for open slices until an idempotent checkpoint key exists.
- Make persistence return a typed result: saved, duplicate ignored, retryable failure, fatal failure.
- Surface only high-level failure state in UI/logs, not private app history.

Exit criteria:
- A finalized slice is stored exactly once.
- Stop, suspend, tray exit, and window change cannot duplicate the same interval.
- Autosave behavior is explicitly tested.

## Phase 4: SQLite Schema, Migrations, And Connections

- Add a test database path/options constructor to `DatabaseService`.
- Use one connection strategy: create a new SQLite connection per operation through a factory. In progress: write/read operations now use a shared connection factory/open helper where touched; constructor still keeps the connection string holder.
- Add busy timeout in the connection string or immediately after opening. Done: opened connections set `PRAGMA busy_timeout = 5000`.
- Decide whether to enable WAL. If enabled, test it with the app's read/write pattern.
- Make schema creation idempotent and set `PRAGMA user_version` to the latest version on fresh databases. In progress: latest version is set only after required v2 columns exist.
- Make every migration check column/index existence before altering schema. Done for current v1/v2 column migrations.
- Remove the fake in-memory fallback or implement it fully. Preferred: fail visibly into a read-only/degraded state rather than pretend writes succeeded.
- Test corrupt database handling with a disposable file.
- Fix maintenance SQL hazards. Done: `CleanupExpiredRecords` no longer runs `VACUUM` inside an active transaction.

Exit criteria:
- Fresh DB reaches expected `user_version`.
- Existing v0/v1/v2-like databases migrate repeatedly without errors.
- Save/read/range/wipe/corruption tests pass against temp files.

## Phase 5: Startup, Tray, And Power Lifecycle

- Split service startup from visual window loading.
- Add an explicit app startup method that runs for both activated and hidden startup launches.
- Do not rely on `MainWindow_Loaded` to start tracking in background mode.
- Initialize tracking before optional visual setup when launched from Windows startup.
- Verify tray icon behavior when the window starts hidden.
- Wire display off/on, away mode, suspend, resume, and tray exit to the tracking slice contract.

Exit criteria:
- Normal launch starts tracking.
- Windows startup/background launch starts tracking.
- Tray show/exit/reset works after hidden launch.
- Sleep/resume and display off/on do not stretch active time.

## Phase 6: Aggregation And UI Data Consistency

- Keep stored records and live overlays separate until the final query/aggregation step.
- Test daily, weekly, custom range, today-with-live, historic-without-live, and midnight rollover views.
- Remove 5-minute filtering from core aggregation unless it is a deliberate product rule. If kept, document it in UI/docs.
- Ensure idle/away rows are included or excluded consistently across summary, list, and charts.
- Keep `ProcessName` stable and use separate display fields for friendly names/window titles.

Exit criteria:
- Daily totals match stored intervals plus the current live slice once.
- Historic views do not change because of live tracking.
- App names do not fragment because of transient window titles.

## Phase 7: Manifest, Privacy, And Packaging

- Remove `internetClient` if no network access is needed.
- Confirm `README.md`, `PRIVACY.md`, and `Package.appxmanifest` agree.
- Confirm signing certificate files are ignored and not committed.
- Validate MSIX/package launch after lifecycle changes.
- Keep `runFullTrust` only because foreground-window tracking needs desktop integration.

Exit criteria:
- Package capabilities match the local-only privacy promise.
- Packaged launch and startup task behavior are manually verified.

## Phase 8: Dependency Upgrade

- Re-run `dotnet list package --outdated` and `dotnet list package --vulnerable --include-transitive`.
- Upgrade in small groups: SQLite first, Windows App SDK second, charting packages last.
- After each group run `dotnet build`, `dotnet test`, and the relevant smoke tests.
- Validate packaged launch after Windows App SDK changes.
- Do not keep release-candidate charting packages if stable versions are compatible.

Exit criteria:
- Dependency changes are protected by tests.
- Startup, tracking, persistence, and package launch still pass.

## Phase 9: .NET 10 LTS Migration

- Confirm the current Microsoft support status before starting. As of this plan, .NET 10 is the active LTS line and the local machine has a .NET 10 SDK installed.
- Upgrade the project target from `net8.0-windows10.0.22621.0` to the compatible .NET 10 Windows target after Phase 8 passes.
- Keep `SupportedOSPlatformVersion` unchanged unless there is a specific Windows API reason to raise it.
- Update package versions required for .NET 10 compatibility, especially Windows App SDK, SQLite, charting, and build tooling.
- Update developer prerequisites in `README.md` and `AGENTS.md`.
- Run `dotnet clean`, `dotnet restore`, `dotnet build`, and `dotnet test`.
- Validate MSIX packaging, self-contained deployment, signing, launch, startup task, tray behavior, suspend/resume, reset data, and date/range views.
- Review compiler/analyzer warnings introduced by the newer SDK and fix only warnings that indicate correctness, packaging, or lifecycle risk.

Exit criteria:
- `TargetFramework` is on .NET 10.
- Package/runtime dependencies are compatible with the .NET 10 target.
- Packaged launch and Windows startup behavior pass after migration.
- README and contributor docs describe the new .NET SDK requirement.

## Existing Code To Reuse

- `WindowTrackingService` already centralizes foreground, idle, suspend/resume, and midnight events. Reuse it, but make its outputs immutable finalized slices.
- `DatabaseService` already has parameterized SQL and range queries. Reuse the SQL surface after fixing schema versioning and connection lifetime.
- `ProcessFilter`, `TimeUtil`, and `ApplicationProcessingHelper` are small enough to cover first with unit tests.
- `TrayIconHelper` and power notification plumbing should stay, but their save/stop effects need to route through the single lifecycle contract.
- `MainViewModel` already holds observable state; do not broaden the UI refactor during reliability work.

## NOT In Scope

- Full UI redesign.
- New dashboard concepts, productivity scores, or guilt language.
- Cloud sync, telemetry, analytics, or remote backup.
- Broad MVVM rewrite.
- New app-detail, export, filtering, retention, or settings features before reliability is done.
- Package upgrades before tests and smoke checks protect the risky paths.

## Manual Smoke Checklist

- Normal launch starts tracking.
- Windows startup/background launch starts tracking without showing the main window.
- Tray icon appears, shows the window, resets data, and exits cleanly.
- Switching active windows creates one finalized slice per interval.
- Idle longer than the threshold does not count as active foreground time.
- Active media playback does not incorrectly create idle time.
- Display off/on does not inflate time.
- Suspend/resume does not inflate time.
- Tray exit saves the current slice once.
- Reset data clears records and resumes tracking from zero.
- Today, yesterday, last 7 days, and custom range views match stored data.
- Midnight rollover closes the prior day and starts the new day.
- Packaged launch and startup task launch work.

## Success Criteria

- `dotnet build` passes.
- `dotnet test` passes.
- Fresh and migrated SQLite databases reach the expected schema version.
- A finalized tracking slice is saved exactly once across window change, stop, suspend, resume, autosave, reset, and exit.
- Startup/background tracking is verified.
- Manual lifecycle smoke tests pass.
- Manifest capabilities match the local-only privacy policy.
- The app targets .NET 10 LTS after reliability tests and dependency compatibility are in place.
- The UI remains visually familiar except for reliability/error-state fixes.
