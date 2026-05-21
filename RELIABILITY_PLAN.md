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

## Progress Snapshot

Updated: May 21, 2026.

Current status:
- Build: `dotnet build ScreenTimeTracker.sln` passes with 0 warnings.
- Tests: `dotnet test ScreenTimeTracker.sln` passes with 47 tests.
- Release publish smoke: `dotnet publish ScreenTimeTracker.csproj -c Release --no-restore` passes.
- Vulnerability audit: `dotnet list package --vulnerable --include-transitive` reports no vulnerable packages from the current NuGet sources.
- SDKs installed locally: .NET SDK 9.0.314 and 10.0.300.
- Source-control safety: no tracked `*.pfx`, database files, or `TestResults/` artifacts were found.

Progress by phase:

| Phase | Status | Progress | Notes |
| --- | --- | ---: | --- |
| Phase 0: Baseline And Safety Gates | Mostly done | 90% | Build, test, release publish, package list, SDK list, vulnerable-package audit, and sensitive-file tracking checks are current. Manual lifecycle smoke execution remains. |
| Phase 1: Test Foundation Around Pure Logic | Strong start | 92% | 47 unit tests now cover filters, formatting, naming, slices, database behavior, startup policy, manifest/privacy policy, and tracking lifecycle seams. |
| Phase 2: Tracking Slice Contract | Mostly done | 90% | `UsageSlice`, service finalization, snapshot live records, stop, window change, idle enter/exit, suspend, resume, and midnight rollover are covered. Remaining work is production smoke verification. |
| Phase 3: Single Persistence Pipeline | In progress | 84% | Finalized slices are the persistence path, slice saves return typed persistence results, and exact duplicate intervals are ignored by code and database constraint. Explicit autosave behavior is still pending. |
| Phase 4: SQLite Schema, Migrations, And Connections | Mostly done | 88% | Temp-file DB tests cover schema, WAL mode, first-run state, report aggregation, retention cleanup, maintenance, migration replay, duplicate cleanup/indexing, wipe, corruption, and degraded state. Init/migration still use the initialization connection. |
| Phase 5: Startup, Tray, And Power Lifecycle | In progress | 55% | Hidden startup initialization and active/idle suspend finalization are fixed and unit-covered. Tray, packaged startup, and OS power smoke tests remain. |
| Phase 6: Aggregation And UI Data Consistency | Early | 30% | Stable app identity, active-duration double-counting, and basic usage-report aggregation are addressed. UI date/range aggregation and icon-key tests still need work. |
| Phase 7: Manifest, Privacy, And Packaging | Partial | 74% | Manifest/privacy guardrail tests cover no network capabilities, `runFullTrust`, startup/full-trust executable wiring, logo asset existence, and local-only README/PRIVACY claims. Release publish passes; installed MSIX smoke tests remain. |
| Phase 8: Dependency Upgrade | Not started | 0% | Intentionally gated until reliability coverage and smoke checks are stronger. |
| Phase 9: .NET 10 LTS Migration | Not started | 0% | .NET 10 SDK is installed, but app migration waits until dependency and packaging validation are ready. |

Ship readiness estimate:
- Reliability implementation: about 75%.
- Automated test coverage for risky seams: about 90%.
- Commit readiness: about 93%, assuming this is committed as a reliability-foundation checkpoint.
- Release readiness: about 42%, because tray/startup/power/installed-package smoke checks have not been run yet.

## Current Risk Snapshot

1. Typed persistence failures are logged, but the UI does not yet surface a durable high-level database health state.
2. Autosave remains intentionally disabled for open slices until an idempotent checkpoint key exists and is tested.
3. Tray hidden startup, packaged startup, display off/on, away mode, suspend/resume, and tray exit still need manual smoke verification.
4. Release notes need one final consistency pass before release.
5. App display names and icons can drift or appear broken if icon cache keys are based on transient or inconsistently normalized app names.

Mitigated so far:
- Stable process identity no longer uses transient window titles.
- `internetClient` has been removed from the package manifest.
- `MainWindow` persistence now uses finalized `UsageSlice` events instead of grouped live-record saves.
- Current migrations check for existing columns before altering schema, and fresh schemas are marked with the latest `user_version` only after required columns exist.
- The fake in-memory database fallback has been removed; initialization failures now leave `DatabaseService` in a visible degraded state.
- Hidden startup no longer depends on `MainWindow_Loaded`; `App.OnLaunched` explicitly initializes tracking, tray, and power setup before optional activation.
- Focused live duration updates no longer overwrite accumulated duration while `AppUsageRecord.Duration` also adds live focused time.
- Exact duplicate finalized intervals are cleaned up during initialization and protected by a partial unique index on finalized slices.
- Stable process identity is normalized separately from display names and window titles.
- Fresh databases opt into WAL journal mode and the behavior is covered by a temp-file database test.
- First-run checks, report reads, retention cleanup, maintenance, and wipe now use short-lived database connections.
- Package manifest and privacy docs are covered by tests that protect the local-only/no-network promise and required full-trust/startup wiring.
- Release publish output builds successfully with the current MSIX project settings.

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
- `dotnet test ScreenTimeTracker.sln` passes with 47 tests.
- `ProcessFilterTests` covers empty/system/normal process filtering.
- `TimeFormatterTests` covers compact duration formatting and duration cap formatting.
- `ApplicationNameNormalizerTests` covers architecture suffix cleanup and keeps window titles out of stable process identity.
- `UsageSliceTests` covers finalized interval creation, missing process names, zero-length intervals, application-name fallback, and duration caps.
- `DatabaseServiceTests` covers fresh schema versioning, WAL mode, first-run state, finalized slice persistence, exact duplicate cleanup plus unique finalized-slice indexing, usage-report aggregation, retention cleanup, maintenance, raw date-range reads, database wipe behavior, v0/v1/v2-like migration replay, corrupt-file recovery, and degraded-state behavior against temporary SQLite files.
- `DatabaseServiceTests` covers typed slice-save results for saved, duplicate ignored, and fatal degraded-state outcomes.
- `StartupLaunchPolicyTests` covers normal launch, hidden Windows startup launch, and first-run startup launch visibility decisions.
- `WindowTrackingServiceTests` covers suspend/resume/stop finalization of active and idle records without duplicate finalized slices, focused live updates that avoid accumulated-duration double counting, snapshot reads from open tracking records, midnight rollover finalization, foreground window-change finalization, and idle enter/exit transitions.
- `ManifestPrivacyTests` covers no package network capabilities, the expected `runFullTrust` capability, full-trust/startup executable wiring, referenced logo asset existence, and local-only/no-telemetry README/PRIVACY claims.
- Remaining Phase 1 work: continue replacing implicit live-overlay behavior with tested service/helper contracts where touched.

## Phase 2: Define The Tracking Slice Contract

- Introduce a small domain type for finalized slices, for example `UsageSlice`. Done: `UsageSlice` is immutable and covered by unit tests.
- Keep these fields stable: `ProcessName`, `ApplicationName`, `WindowTitle`, `StartTime`, `EndTime`, `Duration`, `Date`.
- Stop using mutable `AppUsageRecord` as both live UI object and persistence object. In progress: persistence now receives `UsageSlice`; live UI still uses `AppUsageRecord`.
- Make `WindowTrackingService` the only owner of active slice lifecycle. In progress: all service finalization paths now route through one `FinalizeRecord` helper.
- Make `GetRecords()` return snapshots, not live mutable references. Done: callers receive frozen `AppUsageRecord` snapshots that cannot mutate the service's open records.
- Ensure every finalized slice has `EndTime > StartTime`, bounded duration, and a deterministic date.

Exit criteria:
- One codepath finalizes a foreground slice.
- One codepath finalizes an idle slice.
- Tests prove stop, window change, idle enter/exit, suspend, resume, and midnight rollover emit expected slices. Covered by tests.

## Phase 3: Single Persistence Pipeline

- Replace `MainWindow.SaveRecordsToDatabase()` grouping writes with one persistence handler for finalized slices. Done: `MainWindow` subscribes to `UsageSliceFinalized` and calls `DatabaseService.SaveSliceWithResult`.
- Remove duplicate writes from stop/suspend/exit/autosave paths. Done: open-slice autosave writes and stop/suspend grouping writes were removed.
- Decide whether autosave is:
  - disabled for open slices, or
  - an idempotent checkpoint keyed by an active slice id.
  Current decision: disabled for open slices until an idempotent checkpoint key exists.
- Make persistence return a typed result: saved, duplicate ignored, retryable failure, fatal failure. In progress: `SaveSliceWithResult()` returns typed outcomes; saved, duplicate ignored, and fatal outcomes are tested. Duplicate ignored is enforced both by pre-insert lookup and a partial unique index for finalized intervals. Retryable busy/locked behavior is classified but not directly tested.
- Surface only high-level failure state in UI/logs, not private app history.

Exit criteria:
- A finalized slice is stored exactly once. Covered for exact duplicate finalized intervals and legacy duplicate cleanup.
- Stop, suspend, tray exit, and window change cannot duplicate the same interval.
- Autosave behavior is explicitly tested.

## Phase 4: SQLite Schema, Migrations, And Connections

- Add a test database path/options constructor to `DatabaseService`. Done: tests create disposable temp-file SQLite databases without in-memory fallback.
- Use one connection strategy: create a new SQLite connection per operation through a factory. Mostly done: write/read/cleanup/maintenance/wipe operations now use short-lived connections through the shared factory/open helper; init and migrations still use the initialization connection.
- Add busy timeout in the connection string or immediately after opening. Done: opened connections set `PRAGMA busy_timeout = 5000`.
- Enable WAL for temp-file and production databases. Done: initialization sets `PRAGMA journal_mode = WAL`, and the fresh-database test verifies the journal mode.
- Make schema creation idempotent and set `PRAGMA user_version` to the latest version on fresh databases. In progress: latest version is set only after required v2 columns exist.
- Make every migration check column/index existence before altering schema. Done for current v1/v2 column migrations.
- Remove the fake in-memory fallback or implement it fully. Done: initialization failures now enter degraded state instead of pretending an in-memory DB is durable.
- Test corrupt database handling with a disposable file. Done: corrupt DB files are moved aside and replaced with a fresh schema.
- Add database tests. In progress: fresh DB, WAL mode, first-run state, `SaveSlice`, duplicate finalized interval cleanup/indexing, usage-report aggregation, retention cleanup, maintenance, raw range read, wipe, migration replay, corrupt recovery, and degraded-state behavior are covered.
- Fix maintenance SQL hazards. Done: `CleanupExpiredRecords` no longer runs `VACUUM` inside an active transaction.

Exit criteria:
- Fresh DB reaches expected `user_version`. Covered by tests.
- Existing v0/v1/v2-like databases migrate repeatedly without errors. Covered by tests.
- Save/read/range/wipe/corruption tests pass against temp files.

## Phase 5: Startup, Tray, And Power Lifecycle

- Split service startup from visual window loading. Done: `MainWindow.EnsureStartupInitialized()` owns one-time startup services.
- Add an explicit app startup method that runs for both activated and hidden startup launches. Done: `App.OnLaunched` calls it immediately after window creation.
- Do not rely on `MainWindow_Loaded` to start tracking in background mode. Done: `Loaded` now only calls the same guarded startup path and handles delayed visual icon refresh.
- Initialize tracking before optional visual setup when launched from Windows startup. Done: `StartTracking()` runs before `Activate()` is skipped or called.
- Verify tray icon behavior when the window starts hidden.
- Wire display off/on, away mode, suspend, resume, and tray exit to the tracking slice contract. In progress: suspend/stop finalizes both active and idle open records through one helper.
- Avoid duplicate timer subscriptions during startup. Done: `SetUpUiElements()` no longer re-attaches `UpdateTimer_Tick`; the constructor owns timer wiring.

Exit criteria:
- Normal launch starts tracking. Build-covered; still needs app smoke verification.
- Windows startup/background launch starts tracking. Code path fixed; still needs packaged/startup smoke verification.
- Tray show/exit/reset works after hidden launch.
- Sleep/resume and display off/on do not stretch active or idle time. Active/idle finalization is unit-covered; OS signal smoke testing remains.

## Phase 6: Aggregation And UI Data Consistency

- Keep stored records and live overlays separate until the final query/aggregation step.
- Test daily, weekly, custom range, today-with-live, historic-without-live, and midnight rollover views.
- Remove 5-minute filtering from core aggregation unless it is a deliberate product rule. If kept, document it in UI/docs.
- Ensure idle/away rows are included or excluded consistently across summary, list, and charts.
- Keep `ProcessName` stable and use separate display fields for friendly names/window titles.
- Keep app icon cache keys stable across case changes, architecture suffixes, window-title changes, and missing process names.
- Add automated tests for app naming/icon-key behavior; reserve only Win32 image extraction itself for integration or smoke testing because it depends on live windows and shell APIs.

Exit criteria:
- Daily totals match stored intervals plus the current live slice once.
- Historic views do not change because of live tracking.
- App names do not fragment because of transient window titles.

## Phase 7: Manifest, Privacy, And Packaging

- Remove `internetClient` if no network access is needed. Done, and covered by manifest guardrail tests.
- Confirm `README.md`, `PRIVACY.md`, and `Package.appxmanifest` agree. Done for local-only/no-network claims with automated tests.
- Confirm signing certificate files are ignored and not committed.
- Validate MSIX/package launch after lifecycle changes. Release publish passes; installed launch smoke remains.
- Keep `runFullTrust` only because foreground-window tracking needs desktop integration. Covered by manifest guardrail tests.

Exit criteria:
- Package capabilities match the local-only privacy promise. Covered by tests.
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

## GSTACK REVIEW REPORT

| Review | Trigger | Why | Runs | Status | Findings |
|--------|---------|-----|------|--------|----------|
| CEO Review | `/plan-ceo-review` | Scope & strategy | 0 | Not run | Not needed for this reliability-only checkpoint. |
| Codex Review | `/codex review` | Independent 2nd opinion | 0 | Not run | Save for pre-commit or PR review. |
| Eng Review | `/plan-eng-review` | Architecture & tests (required) | 1 | Clear with sequencing notes | 0 blocking issues, 0 critical gaps. Idempotent exact-interval persistence is now implemented; keep smoke tests before dependency/.NET migration. |
| Design Review | `/plan-design-review` | UI/UX gaps | 0 | Not run | Not needed; current work avoids UI redesign. |
| DX Review | `/plan-devex-review` | Developer experience gaps | 0 | Not run | Not needed for app reliability work. |

- **UNRESOLVED:** 0 blocking engineering decisions.
- **VERDICT:** ENG CLEARED - continue implementation, but do not start autosave, dependency upgrades, or .NET 10 migration before smoke checks.

Eng review details:
- **Scope Challenge:** scope accepted as-is. The plan remains reliability-first with no UI redesign, no telemetry, and no package upgrades yet.
- **Architecture Review:** 0 blocking issues. The service-owned finalized-slice architecture is correct; the next architectural step is an idempotency key or unique interval constraint before any open-slice autosave.
- **Code Quality Review:** 0 blocking issues. The current test seams are acceptable because they are `UNIT_TEST`-scoped and avoid WinUI launch.
- **Test Review:** 1 remaining high-priority gap. Manual smoke tests for tray/startup/power/package behavior still need to run before release readiness improves.
- **Performance Review:** 0 blocking issues. Keep live overlay aggregation under watch in Phase 6, especially database reads from timer-driven UI refresh paths.
- **NOT in scope:** UI redesign, productivity scoring, cloud sync, dependency upgrades, .NET 10 app migration, and broad MVVM rewrite.
- **What already exists:** `WindowTrackingService` owns active/idle/suspend/midnight flow, `DatabaseService` already has parameterized SQL and temp-file tests, `TrayIconHelper` and power notifications already exist and should be smoke-tested rather than replaced.
- **Failure modes:** typed persistence can still silently log failures instead of showing user state; packaged hidden startup and power events still require manual smoke verification.
- **Parallelization:** sequential implementation is preferred right now because Phase 3/4 persistence and Phase 6 aggregation touch shared database/model/service modules.
