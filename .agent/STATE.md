# State

Last rewritten: 2026-11-14.

## Where the project is

- M0 foundation, M1 hardware, M2 gaming and M3 AI telemetry/quotas are done (see `docs/ROADMAP.md`).
- **M4 — Widget Engine & Compact Surfaces is active.** On `main`: native WinUI WidgetHost with `MetricReadout`, `MetricBar`, `MetricRing`, shared exact-`MetricId` subscriptions, `MetricSparkline`, an overview page with a separate composition-details view, a compact floating mode with pin-on-top, a configuration dialog, persisted ordered composition (SQLite, non-secret layout only) and a `--theme=system|light|dark` startup option.
- The surfaces share Cerith design tokens (midnight/ivory palette, serif wordmark, state colours) and a custom title bar; see `docs/UI_UX.md`. A tray icon (show/hide, compact/expand, quit) keeps it reachable in the background, and window position, size and compact state are restored on start. Not started: motion, Home.
- The repository is public. CI runs Build and Test, Repository Quality and CodeQL on every PR. `main` is protected by the Protect Main ruleset (PR only, squash, `build` and `hygiene` required).
- M4 has explicit exit criteria in `docs/ROADMAP.md` (D-026).

## How we work now

The agent workflow was simplified on 2026-11-14 (D-027) and checked with a clean session on a Standard-tier model: short state files, vendor-neutral model tiers, proportional verification, small PRs merged when green. `AGENTS.md` is the single source for process. The previous checkpoint logs (`HANDOFF.md`, `WORKLOG.md`) were removed; their content remains in git history and merged PRs.

## Known limits (do not block on these)

- **Open defect candidate:** sparkline range text was clipped at physical 144 DPI, minimum size, with a large value. No 144 DPI display has been available since. It is an M4 exit criterion (CER-M4-021).
- Mixed-monitor transitions are unverified. Known low-severity case: leaving compact mode after dragging the window to a monitor with a different scale restores the old pixel size.
- A layout that contains a sparkline cannot be read by builds older than November 2026; the file is preserved, not lost.
- WidgetHost idle cost is above the aspirational targets in `docs/PERFORMANCE.md`; most memory is the WinUI framework floor. General CPU non-regression is not established.
- Several AI quota providers remain evidence-blocked or deferred; dispositions are in `docs/PROVIDERS_AI_QUOTAS.md` and `docs/AI_TELEMETRY.md`.
- The product name and brand mark are provisional pending clearance.

## Direction (owner, 2026-11-14)

Finish Cerith as a complete, reliable, distributable app. Function matters more than looks for now: collect verifiable telemetry, record it locally, query and analyse it. The current visual design is provisional; do not spend effort on redesign. The roadmap was resequenced accordingly (D-028): history and recording come next.

Do not disturb the owner's use of the machine: never steal focus, change system settings or close running apps. Launch the widget only with `--secondary-display`.

Day-to-day work is led by the Standard tier, autonomously, milestone by milestone. The strategic tier is used only for planning and architecture (`MODEL_ROUTING.md`). The strategic review of the project's foundations is closed (D-029).

## Next

M5 history store is on `main` (D-030): TelemetryService records every published snapshot (see `docs/ARCHITECTURE.md`, History store). Gaming sessions are recorded too (D-031). Next: `CER-M5-003` AI usage events, then `CER-M6-001` in `.agent/TASKS.md`.
