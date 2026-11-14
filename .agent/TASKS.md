# Open tasks

Only open work is listed. Finished tasks live in git history and merged PRs. Task IDs continue the `CER-M<milestone>-<n>` series.

## CER-M4-019 — Refine the native surfaces

Status: open, no owner.

Tokens, custom title bar and state colours are on `main` (see `docs/UI_UX.md`). Remaining ideas, in order of value: split value and unit typographically; let a bar, ring or sparkline replace the readout's hairline instead of sitting under it; restyle the configuration dialog with the same tokens; a first motion token for value and state changes that respects reduced motion; high-contrast check.

## CER-M4-020 — Remaining M4 surfaces

Status: open, not started. Taskbar surface, tray indicators, simple graph, detail popup. Split into one task per surface when started. Reassess against the M4 exit list in `docs/ROADMAP.md` first; drop or defer anything that does not earn its place.

## Housekeeping

- Dependabot PR #72 (Windows App SDK 2.5.1 → 2.6.5): review and merge if CI is green.
- `docs/TESTING_QA.md` and `docs/PERFORMANCE.md` still contain dense per-run receipts; condense them to durable facts when next touched.
- Stale local worktrees and merged branches from M3/M4: remove once confirmed clean and pushed.
- Decide whether to enable branch protection on `main` (owner decision).
