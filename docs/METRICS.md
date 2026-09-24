# Universal Metric Contract

## Principle

Every measurement in Cerith must be representable through one core metric model.

## Metric IDs

Metric IDs are immutable, open-ended strings. Preserve their exact text and compare them ordinally and case-sensitively. Reject null, empty, whitespace-only, and leading/trailing whitespace; do not lowercase IDs or impose a speculative grammar.

Use hierarchical strings as a convention:

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

Do not use a closed enum because metrics need to be addable without recompiling Core.

## Descriptor

Descriptors are provider-independent. A descriptor identifies the semantic metric; concrete source information belongs to each snapshot.

Current fields:

```text
Id
DisplayName
Description?
SemanticType
Unit?
ValueKind
```

## Registry

`MetricRegistry` keeps one in-memory `MetricDescriptor` per `MetricId`. The first registration wins; duplicate IDs do not replace it. Lookup returns the stored descriptor, and catalog enumeration returns a read-only point-in-time snapshot. Operations are thread-safe. The registry stores descriptors only, not measurements or snapshots.

## Bus

`MetricBus` fans out snapshots to multiple independent subscribers by exact `MetricId`. A subscription receives only publications made after activation. Each subscriber has a bounded buffer; a full buffer applies backpressure rather than silently dropping snapshots. Cancellation or disposal ends a subscription, and disposing the bus completes active readers. The bus has no replay, latest-value cache, or dependency on `MetricRegistry`.

## Snapshot

Snapshots are immutable. `Value` is optional and uses a typed value kind, not an arbitrary object or JSON value. Supported kinds are `double`, `long`, `decimal`, `bool`, `string`, `DateTimeOffset`, and `TimeSpan`.

```text
MetricId
Value?
ObservedAt: DateTimeOffset
State: Fresh | Stale | Unavailable | Stopped | Error | Unlimited
Confidence: Unknown | Measured | Estimated
Source?
ErrorCode? (sanitized code only)
```

`MetricState` describes availability/freshness; `MetricConfidence` describes whether the value is measured or estimated. These dimensions are independent, so stale values retain either measured or estimated confidence.

Age is derived as `reference_time - ObservedAt`; it is not stored in the snapshot. A stale snapshot retains its observation timestamp so callers can determine its age.

## Value and state rules

- `Fresh` and `Stale` require a value.
- `Unavailable`, `Stopped`, and `Error` contain no value.
- `Unlimited` contains no value; the state represents the absence of a finite limit.
- A snapshot without a usable value has `Unknown` confidence.
- Zero is a real value; absence is represented separately.
- Unlimited is not 100%, and an error is not 0%.
- Estimated remains visible when the value becomes stale.
- Error details use a short sanitized code, never an exception or raw provider message.

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
