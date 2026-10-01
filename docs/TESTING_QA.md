# Testing & QA

## Verdict vocabulary

Use:

```text
PASS
FAIL
BLOCKED
NOT RUN
```

Avoid “looks fine”.

## Layers

### Unit

- metric normalization;
- expressions/rules later;
- provider parsing;
- cache logic.

### Contract

- provider conformance;
- IPC schemas;
- plugin contracts later.

### Integration

- provider → MetricBus;
- MetricBus → WidgetHost;
- process reconnect;
- history later.

### UI

- states;
- DPI;
- keyboard;
- reduced motion;
- layout.

### Performance

- idle CPU;
- memory;
- provider wakeups;
- overlay impact.

## Provider QA

Parsing tests are not enough.

AI quota providers require live/independent validation where possible.

When testing worker failures with `TimeProvider`, allow `PeriodicTimer` construction to finish before injecting a timestamp fault. Throwing from `CreateTimer` during construction can trigger a partial-construction disposal/finalizer crash on .NET 10.0.12; this was reproduced without Cerith code and independently surfaced by the Antigravity and ZCode test fixtures. Keep the correction test-only and preserve production cadence.

## Diagnostic validation sessions (future)

A Test / Diagnostic Session is evidence-generation tooling, not a substitute for deterministic tests.

When implemented, a session should make it possible to reconstruct what Cerith observed over a bounded run:

- timestamped metric samples with MetricId, value when present, state, confidence and source;
- sanitized error codes and relevant provider/runtime lifecycle transitions;
- sampling/poll cadence and version information needed to interpret the run;
- enough target/session identity to align samples without logging unnecessary private process metadata;
- an explicit start and stop boundary.

For cross-tool validation, compare against an independently captured reference trace when useful. Align clocks/windows and normalize units before comparing. Record both agreement and disagreement, and never assume the reference application is automatically correct. A meaningful mismatch should trigger source/semantic investigation rather than being hidden by tolerance or post-processing.

Diagnostic captures must remain reproducible and inspectable. They complement unit, contract and integration tests; they do not make hardware-dependent CI mandatory.

## Visual changes

Capture representative states for meaningful UI work.

## Multi-monitor

Eventually test:

- primary/secondary;
- mixed DPI;
- unplug/replug;
- negative coordinates;
- taskbar variations.

## Definition of done

A feature is done when:

```text
contract implemented
tests added
error behavior defined
diagnostics available
performance impact understood when relevant
docs updated if public behavior changed
```
