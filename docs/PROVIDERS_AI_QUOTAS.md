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

### CER-M3-001 contract audit

This is a documentary acquisition/normalization contract, not an existing DTO or a reason to change Core/IPC. Evidence and upstream revisions live in [AI_QUOTA_REFERENCE_MATRIX.md](AI_QUOTA_REFERENCE_MATRIX.md); implementation order is D-023. No AI provider is implemented.

The original fields are necessary but insufficient to distinguish multiple quota buckets, units and cache policy. Define them as follows and add only the four metadata entries below:

| Field | Meaning / constraint |
| --- | --- |
| `provider_id` | Stable provider name; never an account or model identity. |
| `account_identity_hash` | Private installation-scoped pseudonym of provider + authenticated user + selected workspace/organization. Not an email hash, token hash, installation path or public identifier. Unknown identity cannot reuse another account's cache. |
| `source` | Versioned adapter/source label and evidence class (documented, official-observed, external, local observation). No URL query, account identifier or credentials in public snapshot Source. |
| `auth_source` | Mechanism only, e.g. official CLI file, OS credential store, official app local RPC; never credential content or personal path. |
| `observed_at` | UTC time at which this response was acquired; a source event uses its event timestamp. Republishing cache preserves the original timestamp. Not reset time or cache-load time. |
| `metric_kind` | Distinguishes quota, balance, credits, subscription and activity. Explicit unlimited applies only to the dimension the source actually declares unlimited. |
| `window` | Original slot/key plus reported duration and model/group scope when supplied. Unknown duration/rolling-vs-fixed stays unknown. Never infer five hours or seven days from primary/secondary alone. |
| `reset_at` | Optional authoritative source timestamp, converted to DateTimeOffset preserving the instant. Never synthesize it from duration, billing date or local midnight. |
| `state` / `confidence` | Exactly the existing MetricSnapshot rules in [METRICS.md](METRICS.md). Measured means directly reported or exact unit/complement normalization of reported data; it does not certify upstream accuracy or endpoint stability. Missing values have Unknown confidence. |
| `error_code` | Short allowlisted code; no server body or exception text. |
| `error_detail` | Optional fixed, sanitized local diagnostic category. Not sent in v1 IPC; never a raw provider message. |
| **`value` / `unit` (added)** | Typed normalized value and explicit unit; bounded remaining quota percent is 0–100, fraction is 0–1, credits are not assumed USD. A source spend-overrun ratio may exceed 100 and must remain a separate semantic metric, not be clamped into remaining quota. Null is absence, never zero. |
| **`limit_id` (added)** | Source quota bucket/metered feature, separate from provider and window. Missing is unknown; additional buckets must not overwrite the ordinary Codex bucket. |
| **`fresh_until` / `expires_at` (added)** | Local policy timestamps: when a cached value becomes stale and when it becomes unusable. They do not change observed_at or source reset. |

The additions are two field pairs and two individual fields; they specify metadata only. Core snapshots already carry value, timestamp, state, confidence and source. Reset/duration can be separate MetricIds without a new wire protocol. Account/auth/bucket/cache metadata remains acquisition-internal for this first slice.

Identity strategy: HMAC-SHA-256 over a length-delimited tuple `(provider, stable user subject, selected account/workspace)` with an installation key protected by Windows credential facilities. Keep the pseudonym out of ordinary logs/history/IPC; normalized cache is in-memory only for CER-M3-002. Reinstallation/key rotation invalidates continuity. Use only the minimum subject/workspace claims in memory; decoding an official local token is identity partitioning, not independent signature validation or permission to contact arbitrary URLs. Compare response account and user identifiers when present and reread the auth owner before publication; mismatches/unknown ownership discard the response. If a source cannot supply a stable identity, no cross-session cache is allowed. Never hash the access/refresh token as account identity: rotation is not account switching.

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

## CER-M3-002 — Codex first vertical slice (design only)

D-023 selects a single read-only acquisition lifecycle in TelemetryService for the ordinary ChatGPT-backed Codex bucket. Use `GET https://chatgpt.com/backend-api/wham/usage`, an **official-client-observed, undocumented backend endpoint**, with the existing official local access token and selected account header held only in memory. The public official app-server schema is the semantic cross-check, not a production dependency on this chat's tools. The choice and rejected alternatives are substantiated in the reference matrix.

Initial capability is standard ChatGPT file auth under the effective official `CODEX_HOME`; no scan of other users, WSL profiles, browser cookies or backups. Bound the file read and response to 1 MiB. Read only the saved access token, selected account and minimum subject/routing claims. Read-only means no login, refresh, logout, reset-credit consumption, auth writes or copied auth directories. Expired/rejected credentials require the official client to repair its own session. Keyring-only/ephemeral auth, missing identity and nonstandard/region-restricted/FedRAMP routing are explicit unsupported capabilities until separately validated; do not force a credential-store migration or send such accounts to the standard host. Disable redirects and arbitrary configured endpoint overrides. Use TLS and a fixed source host. These limits are intentional, not universal provider support claims.

### Minimum MetricIds

`primary` and `secondary` refer to the source slots **of the ordinary Codex bucket**, never all models or every ChatGPT feature. The raw top-level rate_limit is bound to the ordinary `codex` bucket by the pinned official client mapping, not by guessing a missing field; additional_rate_limits use their own metered feature. No fixed session/week naming or dynamically imported external metric ID. A source duration change changes the window identity, invalidates prior cached family values and is visible through the duration metric.

| MetricId | Value kind / unit / semantic type | Meaning and exact input |
| --- | --- | --- |
| `ai.codex.quota.primary.remaining` | double / % / quota_remaining | `100 - rate_limit.primary_window.used_percent`. Exact complement only for a finite used percentage in 0–100. |
| `ai.codex.quota.secondary.remaining` | double / % / quota_remaining | Same complement of `secondary_window.used_percent`. |
| `ai.codex.quota.primary.reset` | DateTimeOffset / no unit / timestamp | `primary_window.reset_at`, Unix **seconds**, UTC instant. |
| `ai.codex.quota.secondary.reset` | DateTimeOffset / no unit / timestamp | Corresponding secondary reset. |
| `ai.codex.quota.primary.duration` | TimeSpan / no unit / duration | Positive integral `primary_window.limit_window_seconds`. No minutes default. |
| `ai.codex.quota.secondary.duration` | TimeSpan / no unit / duration | Corresponding secondary duration. |

All six use snapshot Source `Codex.Usage.ReadOnly.v1`; measured valid fields are Fresh/Measured. A verified source percentage is sufficient for its complement; an absolute token/message denominator is neither provided nor invented. Zero remaining is real exhaustion, not Error. No numeric clamping, no slot swapping, no fallback 300/10080 minutes, no extrapolation of consumption. Optional fields are independent: absent reset/duration is Unavailable/Unknown for that metric, while a valid percentage can remain Fresh. A malformed supplied field is Error/Unknown for that metric. An absent window makes its family Unavailable/Unknown. Invalid container/schema makes affected families Error/schema_invalid. Invalid percent is Error/invalid_quota; invalid timestamp or duration uses invalid_reset / invalid_duration. Do not reuse a previous field to fill a missing field in a newly successful response.

Reset is a reported future boundary, not proof of a sliding-window algorithm, local midnight or an automatic refill. A supplied valid reset already elapsed at acquisition makes that window family Unavailable/reset_elapsed pending a new valid observation. Crossing reset invalidates cached family values; never publish an assumed 100%. If reset_after_seconds disagrees with reset_at, preserve reset_at and flag source_disagreement; do not silently reconstruct it from the countdown. Small countdown differences due to request latency must be investigated as timing, not averaged.

`plan_type` is subscription metadata; credits.has_credits, credits.balance and credits.unlimited concern credits only. Additional metered buckets, spend controls, usage activity and reset-credit inventories are recorded as out-of-slice capabilities, not exported as these six metrics. Remaining percentage alone does **not** promise a request can run: `rate_limit.allowed`, `limit_reached`, spend-control restrictions and additional limits can block usage. This slice is informational quota telemetry, not admission control. In particular credits.unlimited cannot yield quota Unlimited.

### Refresh, state and cache policy

These are Cerith design defaults, **not upstream polling guarantees**. One request feeds all metrics. Poll once per 60 seconds while enabled; deadline 10 seconds; no immediate retry loop. Record successful acquisition time once for the response. Freshness TTL 120 seconds; hard expiry 15 minutes or known reset, whichever comes first. Both are measured from original acquisition. Use monotonic elapsed time for cadence/TTL/deadline; retain UTC observed_at/reset_at for source instants and age display. Test wall-clock jumps and conservatively invalidate if a clock correction makes reset/age uncertain. Cache key includes provider, private identity, source version, ordinary bucket, slot and reported duration. Only normalized in-memory values are cached; no raw payload, token or private identity is persisted.

| Outcome | Publication and recovery |
| --- | --- |
| Fresh valid response | Publish each valid field Fresh/Measured; evict cached fields replaced by missing/invalid data and publish their truthful valueless state. A later timeout cannot resurrect the older evicted field. |
| Offline, timeout, HTTP 5xx | With eligible same-owner cache, immediately Stale/Measured with original observed_at and sanitized offline / timeout / upstream_unavailable code. Otherwise Unavailable for known offline, Error for timeout/5xx. |
| Age >= 120 seconds | Stale/Measured even before another response; age must remain inspectable. |
| Age >= 15 minutes or reset crossed | Valueless Unavailable/cache_expired or reset_elapsed; no refill assumption. |
| HTTP 401/403 | Immediately discard account cache and publish valueless Error/auth_required or auth_forbidden. Suspend requests until official credentials change or explicit user retry; do not rotate tokens or try another account. |
| HTTP 429 | Eligible cache becomes Stale/rate_limited; otherwise Error/rate_limited. Honor valid Retry-After (seconds or HTTP date). Without it use 60, 120, 240… seconds capped at 15 minutes with bounded jitter. A server delay longer than that cap is still respected. |
| Other HTTP failure / redirects / oversized response | Valueless Error/http_failure, redirect_rejected or response_too_large. No raw body logging or alternate-host credential forwarding. |
| Schema/invalid data | Affected metrics Error; no stale masking of a successful but invalid response. Other valid independent fields remain valid. |
| Owner/workspace/source change or removal | Clear all prior families immediately, cancel obsolete request, discard late response; publish new-owner Unavailable until success. Same-owner token rotation rereads the credential without claiming a new account. |
| Disabled/shutdown | Stopped/Unknown without values; clear in-memory cache. |

Errors/backoff are acquisition policy, not widget polling. Do not claim durable account metadata already exists on the v1 wire. WidgetHost continues to depend only on MetricId.

### Tests required before implementation acceptance

Create hand-built sanitized fixtures with origin URL/revision, capture date, synthetic identities and sanitization notes. No live account percentage, identifier, token, email, header or raw payload enters Git. Required cases:

- Typical two-window response; primary-only weekly duration; secondary-only; neither; swapped/unknown durations; additional metered bucket with different limits; supported ordinary bucket isolated from model buckets.
- Null/missing individual fields and containers; zero/100 used; decimals; negative/>100/NaN/infinite/text used; invalid or overflowing Unix seconds/duration; unknown additive fields; invalid JSON and oversized response.
- Epoch seconds versus milliseconds, UTC conversion, missing reset, already elapsed reset, boundary crossing and countdown disagreement. No computed midnight/refill; reset is preserved exactly.
- Credits-only, credits unlimited, plan-only, activity-only and denied allowed flag alongside positive remaining: none becomes numeric quota/unlimited or evidence that requests are admitted.
- Normalization + descriptor/state conformance through the existing MetricRegistry/MetricBus/Named Pipe tests; widgets subscribe to the six IDs only. Exact complement is coherent and all absence/error states are valueless/Unknown.
- Controllable-time tests for 60-second cadence, 120-second Fresh/Stale boundary, 15-minute expiry, known reset expiry, forward/backward clock jumps, offline with/without cache, recovery, valid→missing/invalid→timeout without resurrection, no fabricated observation time and no disk cache.
- Fake transport/credential input tests for 401/403 suppression, no refresh/login/auth writes, timeout cancellation, 429 Retry-After/backoff, TLS host/redirect restriction, response bound and no secret-bearing diagnostics. A harness recording requests must prove only the permitted GET occurs.
- Account A→B, workspace change, auth removal, same-owner token rotation, owner change while request is in flight, response/local identity disagreement and late completions; no A value may reach B's subscription or cache. Scope/source/duration changes invalidate incompatible families.
- Source disagreement is a failure to investigate, not a tolerance that averages two percentages. Fixtures exercise percentage/reset/identity disagreement with explicit sanitized outcome.

Parser tests alone do not accept CER-M3-002. Live validation must compare the same account/workspace and near-simultaneous observation with the official Codex `/status` or official account usage UI, then one independently implemented monitor as corroboration. Match reported duration/bucket/used-vs-remaining/reset, account for UI rounding (at most its displayed precision), record timestamps/version and sanitized values only. Prefer a quiet account and repeat once after genuine official-client activity or a natural reset; never generate a paid workload just to test, buy/consume a reset or refresh credentials on Cerith's behalf. Source or identity mismatch blocks acceptance until explained. A screenshot containing private data is not a fixture. The M3-001 HTTP 200 probe establishes access/shape on one Windows host only; it does not satisfy this future live cross-validation gate or validate all plans/auth modes.

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
