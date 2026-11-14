# State

Last rewritten: 2026-11-14.

## Where the project is

- M0 foundation, M1 hardware, M2 gaming and M3 AI telemetry/quotas are done (see `docs/ROADMAP.md`).
- **M4 — Widget Engine & Compact Surfaces is active.** On `main`: native WinUI WidgetHost with `MetricReadout`, `MetricBar`, `MetricRing`, shared exact-`MetricId` subscriptions, `MetricSparkline`, an overview page with a separate composition-details view, a compact floating mode with pin-on-top, a configuration dialog, persisted ordered composition (SQLite, non-secret layout only) and a `--theme=system|light|dark` startup option.
- The surfaces share Cerith design tokens (midnight/ivory palette, serif wordmark, state colours) and a custom title bar; see `docs/UI_UX.md`. Not started: motion, taskbar surface, tray indicators, simple graph, Home.
- The repository is public. CI runs Build and Test, Repository Quality and CodeQL on every PR. `main` has no branch protection.

## How we work now

The agent workflow was simplified on 2026-11-14: short state files, vendor-neutral model tiers, proportional verification, small PRs merged when green. `AGENTS.md` is the single source for process. The previous checkpoint logs (`HANDOFF.md`, `WORKLOG.md`) were removed; their content remains in git history and merged PRs.

## Known limits (do not block on these)

- Higher physical DPI (144+) and mixed-monitor transitions are verified only on a single monitor at 96/144 DPI. One sparkline range-text clipping case at 144 DPI is unresolved.
- WidgetHost idle cost is above the aspirational targets in `docs/PERFORMANCE.md`; most memory is the WinUI framework floor. General CPU non-regression is not established.
- Several AI quota providers remain evidence-blocked or deferred; dispositions are in `docs/PROVIDERS_AI_QUOTAS.md` and `docs/AI_TELEMETRY.md`.
- The product name and brand mark are provisional pending clearance.

## Next

See `.agent/TASKS.md`.
