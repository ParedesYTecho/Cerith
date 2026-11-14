# Open tasks

Only open work is listed. Finished tasks live in git history and merged PRs. Task IDs continue the `CER-M<milestone>-<n>` series.

## CER-M4-018 — Land the pending M4 surfaces

Status: open, no owner.

Five draft PRs from October hold finished-but-unmerged work, stacked on each other:

- #65 configurable sparkline, #66 overview/details split, #67 integration of both, #68 compact floating surface (contains #66 and #67), #69 `--theme` startup option (independent).

Goal: get this work onto `main` as one or two PRs based on current `main`. Start from the #68 branch (`codex/CER-M4-016-compact-floating`), which already contains the integrated overview, details and sparkline; add #69. Resolve `.agent/` conflicts by taking `main`. Build, test, run the app and look at it. Fix what is actually broken, record the 144 DPI sparkline limit if it cannot be reproduced, merge, then close the superseded drafts and delete their branches and worktrees.

## CER-M4-019 — Design tokens and visual identity for native surfaces

Status: open, no owner. Depends on CER-M4-018.

The current surfaces use default WinUI styling. Define the Cerith design tokens (typography, spacing, radius, state and metric colours, motion) from `docs/UI_UX.md` as native XAML resources and apply them to the existing components so the widget has a recognisable identity in light and dark. No new component framework.

## CER-M4-020 — Remaining M4 surfaces

Status: open, not started. Taskbar surface, tray indicators, simple graph, detail popup. Split into one task per surface when started. Reassess against the M4 exit list in `docs/ROADMAP.md` first; drop or defer anything that does not earn its place.

## Housekeeping

- Dependabot PR #72 (Windows App SDK 2.5.1 → 2.6.5): review after CER-M4-018 lands.
- Stale local worktrees and merged branches from M3/M4: remove once confirmed clean and pushed.
- Decide whether to enable branch protection on `main` (owner decision).
