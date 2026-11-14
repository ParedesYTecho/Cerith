# Open tasks

Only open work is listed. Finished tasks live in git history and merged PRs. Task IDs continue the `CER-M<milestone>-<n>` series.

## CER-M5-004 — Observe one full day of history

Status: open, small. Let the service run for a day on the owner's machine (do not disturb their use); record file size, row rate and service CPU in `docs/PERFORMANCE.md`, and tune raw retention if the file is too large.

## CER-M5-002 — Gaming sessions

Status: open, ready (M5-001 merged). Record a session per PresentMon target: start, end, target label, and summary statistics computed from recorded samples. Follow D-022 for identity and privacy.

## CER-M5-003 — AI usage events and quota history

Status: open, ready. Quota snapshots are already metrics and are recorded by the history store (D-030). Add what a time series cannot express: per-activity token events from the OpenCode collector, de-duplicated across polls.

## CER-M6-001 — ControlCenter history view

Status: open, ready. Read the history store through `HistoryStore.OpenReader` (D-030). ControlCenter is an empty project today. First screen: pick a metric and a time range, see a chart from the history store. Functional before pretty; reuse the design tokens.

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

## Owner decisions pending

These need the owner; do not act on them. Continue with other work meanwhile.

- Enable GitHub secret scanning, push protection and Dependabot alerts on the repository (free for public repositories; currently disabled).
- Clear the product name and brand mark (provisional).
- Click through the tray menu once (CER-M4-020) and run the widget at 150% display scale (CER-M4-021); both need the owner's desktop.

## Strategic (reserved, see `MODEL_ROUTING.md`)

Nothing pending. Add an item here when a milestone needs replanning or a cross-cutting architecture decision.
