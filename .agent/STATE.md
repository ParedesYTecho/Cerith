# State

Last rewritten: 2026-11-14.

## Where the project is

Last strategic review: 2026-11-27 (D-033).

- Done: M0 foundation, M1 hardware, M2 gaming, M3 AI telemetry and quotas, M4 widget engine (closed with known limits), M5 history implementation (store, gaming sessions, AI events, read API).
- **Active: M6 — Always-on app.** The app now starts, restarts and stops the service itself and both are single-instance. Gaming still only works with a `--pid` given at start-up, so history records no sessions in daily use. M6 makes Cerith start and run by itself and detect games automatically. Its exit includes the day-long measurement that also closes M5.
- Then M7 dashboard (a window in the UI app; the empty ControlCenter project goes away), M8 telemetry breadth, M9 overlay, M10 rules, M11 visual identity, M12 distribution (`docs/ROADMAP.md`).
- Architecture: two user-level processes, TelemetryService (headless, records history) and the WinUI app (tray, widget, future dashboard). Not elevated, no Windows service, no driver (`docs/ARCHITECTURE.md`, Processes).
- The repository is public. CI runs Build and Test, Repository Quality and CodeQL on every PR. `main` is protected by the Protect Main ruleset (PR only, squash, `build` and `hygiene` required).

## How we work now

The agent workflow was simplified on 2026-11-14 (D-027) and checked with a clean session on a Standard-tier model: short state files, vendor-neutral model tiers, proportional verification, small PRs merged when green. `AGENTS.md` is the single source for process. The previous checkpoint logs (`HANDOFF.md`, `WORKLOG.md`) were removed; their content remains in git history and merged PRs.

## Known limits (do not block on these)

- **Open defect candidate:** sparkline range text was clipped at physical 144 DPI, minimum size, with a large value. No 144 DPI display has been available since. Carried forward from M4 (CER-M4-021).
- Mixed-monitor transitions are unverified. Known low-severity case: leaving compact mode after dragging the window to a monitor with a different scale restores the old pixel size.
- A layout that contains a sparkline cannot be read by builds older than November 2026; the file is preserved, not lost.
- WidgetHost idle cost is above the aspirational targets in `docs/PERFORMANCE.md`; most memory is the WinUI framework floor. General CPU non-regression is not established.
- Several AI quota providers remain evidence-blocked or deferred; dispositions are in `docs/PROVIDERS_AI_QUOTAS.md` and `docs/AI_TELEMETRY.md`.
- The product name and brand mark are provisional pending clearance.

## Direction (owner, 2026-11-14)

Finish Cerith as a complete, reliable, distributable app. Function matters more than looks for now: collect verifiable telemetry, record it locally, query and analyse it. The current visual design is provisional; do not spend effort on redesign. The roadmap was resequenced for this (D-028, revised by D-033): make Cerith run by itself, then the dashboard.

Do not disturb the owner's use of the machine: never steal focus, change system settings or close running apps. Launch the widget only with `--secondary-display`. Never turn on start-with-Windows on the owner's machine during testing; only the owner switches it on.

Day-to-day work is led by the Standard tier, autonomously, milestone by milestone. The strategic tier is used only for planning and architecture (`MODEL_ROUTING.md`). Strategic reviews: foundations (D-029), always-on app before dashboard (D-033).

## Next

`CER-M6-002` in `.agent/TASKS.md`, then the rest of M6 in order.
