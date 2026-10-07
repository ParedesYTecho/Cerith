# Cerith

> Working name. Final trademark/domain clearance is intentionally deferred until before public release.

Cerith is a Windows-first, local-first telemetry platform that combines:

- hardware monitoring;
- gaming telemetry and frame analysis;
- AI quota monitoring;
- desktop/taskbar/game-overlay widgets;
- historical telemetry;
- rules, alerts and contextual profiles;
- a highly customizable visual editor;
- a future plugin ecosystem.

Cerith is **not** “another monitoring widget”.

The product goal is:

> **Any metric. Any design. Anywhere on your desktop.**

## Product principles

1. **Truth before appearance.** Never invent telemetry.
2. **Native and lightweight.** The monitor must not become the workload.
3. **Simple by default, deep by choice.**
4. **Metrics are independent from widgets.**
5. **Providers are replaceable adapters.**
6. **UI/UX quality is a first-class feature.**
7. **Local-first and privacy-conscious.**
8. **Open source by default, official identity protected separately.**
9. **Public-ready from the first commit.**
10. **Zero AI slop. Every file, dependency and abstraction must justify its existence.**

## Start here

Read:

- `docs/PRODUCT_SPEC.md`
- `docs/ARCHITECTURE.md`
- `docs/ROADMAP.md`
- `docs/METRICS.md`
- `docs/PROVIDERS_AI_QUOTAS.md`
- `docs/UI_UX.md`
- `CODE_QUALITY.md`
- `SECURITY.md`
- `AGENTS.md`
- `DEVELOPMENT_WORKFLOW.md`

## Legacy policy

The previous WidgetOSD is **not** the starting codebase.

Its AI quota implementation is explicitly considered unreliable and must not be ported.

Legacy material may only be consulted for independently revalidated lessons, sanitized fixtures, edge cases or sensor mappings.

See `docs/LEGACY_POLICY.md`.

## Initial technical direction

```text
C#
.NET 10
WinUI 3
Windows App SDK
Windows Composition
LibreHardwareMonitor
PresentMon
SQLite
Win32 / DirectComposition only where necessary
Optional RTSS integration later
```

No Electron or browser runtime in the always-on monitoring path.

## Current milestone

`M4 — Widget Engine & Compact Surfaces` — ACTIVE / INCOMPLETE.

M0 through M3 are DONE. CER-M4-001 through CER-M4-005 establish the first native surface, performance attribution/isolation, reconnect coordination and MetricId binding contract. M5 remains NOT STARTED.

Current task truth lives in `.agent/STATE.md` and `.agent/TASKS.md`; `docs/ROADMAP.md` defines milestone scope.
