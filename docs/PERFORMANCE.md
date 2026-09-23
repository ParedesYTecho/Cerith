# Performance Budget

## Principle

Performance is a feature.

Cerith should not noticeably consume the resources it monitors.

## Initial aspirational budgets

```text
idle CPU: approximately 0%, measured over sustained interval
minimal always-on memory: < 50 MB target
common full runtime: preferably well below 100 MB
gaming overhead: negligible
startup: near-instant
```

These are targets, not claims.

## Cadence

Different sources require different refresh rates.

```text
frame telemetry        high frequency
hardware temperatures  medium
AI quota               low
billing/subscription   very low
```

Never poll AI quota endpoints every second.

## Rendering

Prefer compositor-driven updates.

Avoid rebuilding large trees for numeric changes.

## History

Batch/downsample.

Never synchronously persist every high-frequency frame sample to SQLite.

## Benchmark scenarios

Maintain repeatable cases:

- idle desktop;
- overlay active;
- editor open;
- many providers;
- many widgets;
- high metric subscription count.

Track regressions.
