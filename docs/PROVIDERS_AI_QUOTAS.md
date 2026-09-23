# AI Quota Provider Strategy

## Critical decision

The old WidgetOSD quota implementation is **not trusted and must not be ported**.

Cerith quota providers are implemented cleanly from zero.

## Reference hierarchy

For each provider compare:

1. official documented API/UI/CLI behavior when available;
2. official local application/CLI structured sources;
3. CodexBar;
4. OpenUsage;
5. TokenTracker;
6. other active open-source implementations;
7. our own sanitized live fixtures and observations.

Reference projects are evidence and implementation research, not unquestioned truth.

## Provider result contract

Every provider must make available:

```text
provider_id
account_identity_hash
source
auth_source
observed_at
metric_kind
window
reset_at
state
confidence
error_code
error_detail
```

Never persist raw credentials.

## Semantic distinctions

Never merge:

```text
quota
balance
credits
subscription
activity
unlimited
```

Examples:

- `$8.43 balance` is not `84.3% quota`;
- “Pro active” is not a usage percentage;
- message count is not necessarily rate-limit usage.

## Initial provider priority

### P0

- Claude
- Codex
- Antigravity

### P1

- GitHub Copilot
- Cursor
- Grok

### P2

- Gemini CLI if independently measurable
- OpenRouter
- Kimi
- other balance providers

## Conformance tests

Every quota provider must prove:

```text
absent values are never invented
remaining values stay in valid range
used/remaining are coherent
source reset timestamp is preserved
401/403 do not become 0%
timeouts do not fabricate values
stale cache exposes age
account change invalidates incompatible cache
source is identified
quota/balance/activity remain distinct
```

## Golden fixtures

```text
tests/Cerith.Telemetry.Tests/Fixtures/
  Claude/
  Codex/
  Antigravity/
  ...
```

Fixtures should be sanitized and include:

```text
origin
date
sanitization notes
expected normalized metrics
```

## Cross-validation

During provider development compare:

```text
Official UI/source
CodexBar
OpenUsage
Cerith
```

A mismatch is investigated.

Never average disagreeing sources.

## Cache rule

Cache identity includes:

```text
provider
account identity
metric window
source
observed_at
TTL
```

Account/source changes invalidate incompatible cache.

## UI contract

Prefer:

```text
Claude
5 h remaining: 62%
reset in 1h 42m
source: OAuth
```

over an ambiguous:

```text
Claude 62%
```
