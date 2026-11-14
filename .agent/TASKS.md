# Open tasks

Only open work is listed. Finished tasks live in git history and merged PRs. Task IDs continue the `CER-M<milestone>-<n>` series.

## CER-M5-001 — History store: design and first vertical slice

Status: open, no owner. **Next task.** Tier: Strong for the design, Standard for the implementation.

Read `docs/ARCHITECTURE.md`, `docs/METRICS.md`, `docs/PERFORMANCE.md`, D-028, and `src/Cerith.Core` plus `src/Cerith.TelemetryService/Program.cs` first. Look at how comparable local tools store time series before choosing a schema (for example the SQLite layouts used by Home Assistant's recorder and by TokenTracker; see `docs/REFERENCE_PROJECTS.md`).

Decide and record in `DECISIONS.md` and `ARCHITECTURE.md`: the schema (metric dictionary, numeric samples with state and confidence, how non-numeric values are handled), write batching and durability (WAL, flush interval, what a crash can lose), retention and downsampling tiers, the file location, and how readers query (direct read-only file access versus a pipe request).

Then ship the smallest slice that proves it: TelemetryService records every valued snapshot from `MetricBus` into the store with batched writes, retention runs, and a test reads a range back. No UI. Measure store growth per hour and added service CPU, and record both in `docs/PERFORMANCE.md`.

Constraints: no new dependency unless the design shows `winsqlite3.dll` cannot do it; never record a value that was not published; do not block or slow the bus (bounded queue, drop-and-count on overflow, never silently).

## CER-M5-002 — Gaming sessions

Status: open, after CER-M5-001. Record a session per PresentMon target: start, end, target label, and summary statistics computed from recorded samples. Follow D-022 for identity and privacy.

## CER-M5-003 — AI usage events and quota history

Status: open, after CER-M5-001. Quota snapshots are already metrics and will be recorded by M5-001. Add what a time series cannot express: per-activity token events from the OpenCode collector, de-duplicated across polls.

## CER-M6-001 — ControlCenter history view

Status: open, after CER-M5-001. ControlCenter is an empty project today. First screen: pick a metric and a time range, see a chart from the history store. Functional before pretty; reuse the design tokens.

## M4 finishing checks

These do not block M5.

## CER-M4-019 — Refine the native surfaces

Status: open, no owner.

Tokens, custom title bar and state colours are on `main` (see `docs/UI_UX.md`). Remaining ideas, in order of value: restyle the configuration dialog with the same tokens; a first motion token for value and state changes that respects reduced motion; high-contrast check.

## CER-M4-020 — Tray menu check and app icon

Status: open, small. The tray icon and window-state persistence are on `main`. Still to do: click through the tray menu by hand once (Show/Hide, Compact/Expand, Quit) and after an Explorer restart; give the executable a Cerith icon so the tray does not show the generic application icon.

## CER-M4-021 — Sparkline range text at 144 DPI

Status: open. M4 exit criterion 1. At physical 144 DPI, minimum window size and a large value, the sparkline's wrapped range text was partially clipped. First reproduce it (Windows display scale 150% on one monitor is enough); then fix the cause or record why it is not a product defect. Do not close this without a 144 DPI run.

## CER-M4-022 — M4 exit checks

Status: open, not started. Exit criteria 4 and 5: tokens in the configuration dialog, high-contrast check, idle CPU/memory measured against `docs/PERFORMANCE.md`.

## Housekeeping

- Dependabot PR #72 (Windows App SDK 2.5.1 → 2.6.5): review and merge if CI is green.
- `docs/TESTING_QA.md` and `docs/PERFORMANCE.md` still contain dense per-run receipts; condense them to durable facts when next touched.
- `docs/ARCHITECTURE.md` and `docs/TESTING_QA.md` still name specific models and reviewers from October; remove those names when condensing.
- Eleven local-only `codex/CER-M3-004-*` research branches exist on the owner's machine; they are not on GitHub.
