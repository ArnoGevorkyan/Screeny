# Repository Guidelines

<a name="project-structure--module-organization"></a>

## Project structure

Start with the [architecture guide](docs/architecture.md) for the tracking, storage, and UI entry points. App icons, splash images, and Store assets are in `Assets/`.

Prefer reliability and data correctness over visual churn.

## Build, test, and development commands

Use the [development guide](docs/development.md) for build and test commands, packaging limits, and dependency audits. Keep the solution x64-only for the app. The local test project may build as Any CPU. Do not upgrade packages before reliability tests cover the affected paths.

## Coding style & naming conventions

Use nullable-aware C# with four-space indentation. Prefer clear PascalCase for public types, methods, and properties; use `_camelCase` for private fields as already seen in `MainWindow` and services.

Keep changes scoped and follow existing WinUI patterns. Avoid WPF APIs. Prefer structured APIs and parameterized SQLite commands over string-built data handling. No formatter or lint command is configured, so match surrounding code.

Use `ProcessName` as stable application identity. Use `ApplicationName` and `WindowTitle` for friendly display text. Do not replace stable process identity with transient window titles.

## Reliability rules

- Persist only finalized usage slices. Live UI records may be displayed, but open mutable records should not be written as durable history.
- `WindowTrackingService` owns active slice lifecycle. Finalized intervals should flow through `UsageSliceFinalized`.
- Avoid duplicate writes from stop, suspend, exit, autosave, idle, and window-change paths.
- Open-slice autosave is disabled until an idempotent checkpoint key exists.
- Every finalized slice must have `EndTime > StartTime`, bounded duration, and deterministic `Date`.
- Keep stored records and live overlays separate until query or aggregation time.
- Treat sleep, display off, away mode, idle, resume, reset, and tray exit as lifecycle-risk areas.

## Testing guidelines

For tracking, persistence, aggregation, migration, or lifecycle changes:

- Add focused tests first when the code can be tested without launching WinUI.
- Prefer small pure helpers or domain types when extracting testable behavior from WinUI code.
- Suggested test names: `MethodName_State_ExpectedBehavior`.
- Run the [Windows x64 solution build](docs/development.md#build-on-windows) and the [core tests](docs/development.md#run-the-core-tests). If the optional local test project is present, run it too.
- Follow the [manual Windows checks](docs/development.md#check-packaged-behavior) for the affected workflows.

Do not broaden the UI or MVVM architecture just to make tests easier. Extract only the smallest stable contract needed for the reliability change.

## AI-assisted workflow

For substantial work with Codex, follow the gstack sequence. Clarify the goal and constraints, keep a short checklist, implement within scope, review reliability and privacy, test, and report the changes, verification, and remaining risk. Review for data loss and duplicate counting. Avoid UI redesign unless requested.

Use the relevant skills when available locally in `.agents/skills/`:

- `plan-eng-review`: use before broad reliability architecture changes.
- `investigate`: use for tracking, persistence, startup, or lifecycle bugs.
- `review`: use before landing code.
- `cso`: use for privacy, capabilities, certificates, and local-only checks.
- `ship`: use only after build, tests, and relevant smoke checks pass.

Prefer project-specific guidance in this file over generic .NET skill advice when they conflict. Use the ignored `RELIABILITY_PLAN.md` only when it exists locally.

## Commit & pull request guidelines

Commit history uses concise imperative summaries, for example `Add reliability test foundation` or `Persist finalized usage slices`.

Keep commits narrow and describe the user-visible or architectural outcome. Pull requests should include:

- Short summary.
- Testing performed.
- Affected screens or workflows.
- Screenshots for visible UI changes.
- Any database schema, privacy, packaging, startup, or lifecycle behavior changes.

Do not include generated output, logs, local databases, certificates, `bin/`, `obj/`, or test result artifacts.

## Security & configuration tips

Screeny’s privacy promise is local-only tracking. Do not add network access, telemetry, analytics, cloud sync, or remote backup without updating `PRIVACY.md`, `README.md`, `Package.appxmanifest`, and this file.

Keep signing certificates, logs, database files, `bin/`, `obj/`, `TestResults/`, and coverage outputs out of source control. `runFullTrust` is expected for desktop foreground-window tracking; `internetClient` should stay absent unless a deliberate network feature is added and documented.
