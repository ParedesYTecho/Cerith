# Open tasks

Only open work is listed. Finished tasks live in git history and merged PRs. Task IDs continue the `CER-M<milestone>-<n>` series.

## CER-M6-004 — One day of real use (closes M5 and M6)

Status: open, **ready for the owner**; it blocks nothing. Needs the owner's machine; do not disturb their use.

Owner: run `./publish.ps1`, start `publish\Cerith.WidgetHost.exe`, turn on "Start with Windows" from the tray menu if wanted, and use the PC normally for a day, including at least one game.

Then record in `docs/PERFORMANCE.md`: history size per day and rows per hour, service CPU and memory, and the UI app's idle CPU and memory. Check that gaming sessions and AI events were recorded. Automatic detection (D-034) has not been seen with a real game: check that a played game (ideally also one with anti-cheat) appears as a session and that no desktop or video application does. Tune raw retention if the file is too large.

## CER-M7-001 — Dashboard history view

Status: open, **next task**. The query API is on `main` (`HistoryStore.ReadMetrics`, `ReadSeries` with automatic raw/minute/hour resolution, `ReadSessions`, `ReadAiEvents`). Build it as a second window in the UI app (D-033), opened from the tray and the widget; remove the empty `Cerith.ControlCenter` project in the same PR. First screen: pick a metric and a range (1 h, 6 h, 24 h, 7 d, 30 d), draw `ReadSeries` as a polyline with a min/max band and gaps where there are no Fresh points, show min/max/average, and list recent gaming sessions. Show a clear message when the history file does not exist yet. Functional before pretty; reuse the design tokens. A UI change needs a run with light and dark screenshots, launched without stealing focus.

## CER-M8-001 — Claude Code and Codex CLI local activity

Status: open, owner request of 2026-11-27 (more AI tools, not only OpenCode). After CER-M7-001 unless the owner reorders. Both tools write local session logs; `docs/AI_TELEMETRY.md` lists them (Claude Code evidence-blocked, Codex CLI deferred) because only one collector was needed to close M3, not because it is impossible. Read first-party formats and the MIT TokenTracker readers (adaptable with attribution, D-035); publish counters and model only, never prompts, responses, code or paths; synthetic fixtures; independent privacy review. Follow with other tools the owner uses.

## CER-M12-001 — Binary release notice bundle

Status: open, before any binary release. Complete `THIRD-PARTY-NOTICES.md` with the full licence texts and copyright lines of everything a publish folder contains, and check MPL files for the "Incompatible With Secondary Licenses" notice.

## M4 carried-forward limits

M4 is closed (D-033). These remain open and block nothing.

## CER-M4-019 — Refine the native surfaces

Status: open, no owner.

Tokens, custom title bar and state colours are on `main` (see `docs/UI_UX.md`). Remaining ideas, in order of value: restyle the configuration dialog with the same tokens; a first motion token for value and state changes that respects reduced motion; high-contrast check; with `--theme=light` on a dark system the three caption buttons are nearly invisible; the app takes focus when it starts, also at sign-in.

## CER-M4-020 — Tray menu check

Status: open, small. Click through the tray menu by hand once (Show/Hide, Compact/Expand, Start with Windows, Quit) and after an Explorer restart.

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
