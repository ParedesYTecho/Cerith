# Universal Metric Contract

## Principle

Every measurement in Cerith must be representable through one core metric model.

## Metric IDs

Use hierarchical strings.

Examples:

```text
cpu.total.usage
cpu.package.temperature
gpu.0.temperature.core
gpu.0.power.board
ram.used.bytes
game.fps.display
game.frametime.p99

ai.claude.session.remaining
ai.claude.weekly.remaining
ai.codex.session.remaining
ai.antigravity.gemini.weekly.remaining
```

Do not use a closed enum because plugins need to add metrics without recompiling Core.

## Descriptor

Conceptual fields:

```text
id
display_name
description
semantic_type
unit
value_type
range if meaningful
tags
provider_id
```

## Snapshot

Conceptual fields:

```text
metric_id
value
observed_at
state
source
confidence
age
metadata
error
```

## Required states

```text
measured
estimated
stale
unavailable
stopped
error
unlimited
```

Rules:

- zero is a real value;
- missing is not zero;
- unlimited is not 100%;
- an HTTP error is not 0%;
- stale exposes age;
- estimated stays visibly estimated when relevant.

## Semantic types

Examples:

```text
percentage
temperature
power
frequency
rate
duration
timestamp
bytes
count
currency
quota_remaining
quota_used
balance
subscription_status
activity
boolean
text
```

A currency balance must not be rendered as generic percentage quota unless a real denominator exists.

## Derived metrics

Derived metrics are first-class and record their inputs.

Example:

```text
gpu.vram.usage_percent =
gpu.vram.used / gpu.vram.total * 100
```

## Sampling

Sampling cadence belongs to provider/refresh policy, never to individual widgets.

One sample can feed:

- text;
- chart;
- rule;
- history;
- multiple widgets.
