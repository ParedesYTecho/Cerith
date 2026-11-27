# Open tasks

Only open work is listed. Finished tasks live in git history and merged PRs. Task IDs continue the `CER-M<milestone>-<n>` series.

## CER-M6-001 — App starts and supervises the service

Status: open, **next task**. Tier: Standard.

The UI app (`Cerith.WidgetHost`) starts `Cerith.TelemetryService` with no console window when the metric pipe is absent, and starts it again if it exits unexpectedly (with back-off; never more than one). Both processes are single-instance (a second launch of the UI app brings the existing window forward instead of opening another). Quitting from the tray leaves the service running only if start with Windows is on; otherwise it stops it. Locate the service executable next to the app or from a `--service-path` option for development. Tests for the pure parts (path resolution, back-off); run the app for the rest.

## CER-M6-002 — Automatic game detection

Status: open, after CER-M6-001. Tier: Strong for the design, Standard for the code.

Replace the fixed `--pid` with tracking of the foreground process when it presents frames through PresentMon, switching target when the foreground game changes and releasing it when the game exits. Keep `--pid` as an override. Read D-020 and D-022 first: one target at a time, label only (no PID, path or title in metrics or logs), honest states when nothing is presenting. Desktop apps that present occasionally (browsers, Explorer) must not create sessions; decide and document the rule. Recorded sessions (D-031) then appear without user action.

## CER-M6-003 — Start with Windows, icon, publish layout

Status: open, after CER-M6-001. Tier: Standard.

A per-user start-with-Windows toggle in the tray menu (HKCU Run key or a startup task; off by default). A Cerith `.ico` for both executables and the tray, derived from the existing brand mark in `docs/assets`. A `dotnet publish` layout where both executables and their files sit in one folder and the app finds the service there. When the PresentMon service is not installed, the gaming metrics already say so; also show a one-line hint with the official download link in the widget's details view.

## CER-M6-004 — One day of real use (closes M5 and M6)

Status: open, after CER-M6-001 to 003. Needs the owner's machine; do not disturb their use.

Run the published app for a day of normal use. Record in `docs/PERFORMANCE.md`: history size per day and rows per hour, service CPU and memory, and the UI app's idle CPU and memory. Check that gaming sessions and AI events were recorded. Tune raw retention if the file is too large.

## CER-M7-001 — Dashboard history view

Status: open, after M6. The query API is on `main` (`HistoryStore.ReadMetrics`, `ReadSeries` with automatic raw/minute/hour resolution, `ReadSessions`, `ReadAiEvents`). Build it as a second window in the UI app (D-033), opened from the tray and the widget; remove the empty `Cerith.ControlCenter` project in the same PR. First screen: pick a metric and a range (1 h, 6 h, 24 h, 7 d, 30 d), draw `ReadSeries` as a polyline with a min/max band and gaps where there are no Fresh points, show min/max/average, and list recent gaming sessions. Show a clear message when the history file does not exist yet. Functional before pretty; reuse the design tokens. A UI change needs a run with light and dark screenshots, launched without stealing focus.

## M4 carried-forward limits

M4 is closed (D-033). These remain open and block nothing.

## CER-M4-019 — Refine the native surfaces

Status: open, no owner.

Tokens, custom title bar and state colours are on `main` (see `docs/UI_UX.md`). Remaining ideas, in order of value: restyle the configuration dialog with the same tokens; a first motion token for value and state changes that respects reduced motion; high-contrast check.

## CER-M4-020 — Tray menu check and app icon

Status: open, small. The tray icon and window-state persistence are on `main`. Still to do: click through the tray menu by hand once (Show/Hide, Compact/Expand, Quit) and after an Explorer restart; give the executable a Cerith icon so the tray does not show the generic application icon.

## CER-M4-021 — Sparkline range text at 144 DPI

Status: open. M4 exit criterion 1. At physical 144 DPI, minimum window size and a large value, the sparkline's wrapped range text was partially clipped. First reproduce it (Windows display scale 150% on one monitor is enough); then fix the cause or record why it is not a product defect. Do not close this without a 144 DPI run.

## Housekeeping

- Dependabot PR #85 (Windows App SDK 2.5.1 → 2.7.0, supersedes #72): merge if CI is green and the widget still starts; close #72.
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
