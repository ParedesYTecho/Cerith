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
