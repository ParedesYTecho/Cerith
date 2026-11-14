# Open tasks

Only open work is listed. Finished tasks live in git history and merged PRs. Task IDs continue the `CER-M<milestone>-<n>` series.

## CER-M4-019 — Design tokens and visual identity for native surfaces

Status: open, no owner.

The current surfaces use default WinUI styling. Define the Cerith design tokens (typography, spacing, radius, state and metric colours, motion) from `docs/UI_UX.md` as native XAML resources and apply them to the existing components so the widget has a recognisable identity in light and dark. No new component framework.

## CER-M4-020 — Remaining M4 surfaces

Status: open, not started. Taskbar surface, tray indicators, simple graph, detail popup. Split into one task per surface when started. Reassess against the M4 exit list in `docs/ROADMAP.md` first; drop or defer anything that does not earn its place.

## Housekeeping

- Dependabot PR #72 (Windows App SDK 2.5.1 → 2.6.5): review and merge if CI is green.
- `docs/TESTING_QA.md`, `docs/PERFORMANCE.md` and the component sections of `docs/UI_UX.md` still contain dense per-run receipts; condense them to durable facts when next touched.
- Stale local worktrees and merged branches from M3/M4: remove once confirmed clean and pushed.
- Decide whether to enable branch protection on `main` (owner decision).
