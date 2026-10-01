# AI Quota Provider Strategy

## Critical decision

The old WidgetOSD quota implementation is **not trusted and must not be ported**.

Cerith quota providers are implemented cleanly from zero.

Broader AI activity/token/cost telemetry is specified separately in `AI_TELEMETRY.md`. Quota remains a distinct semantic contract even when the same external reference application displays both.

D-024 expands M3 beyond the original post-Codex sequential plan. The current reference target is the 17-provider usage-limit catalog recorded in `AI_TELEMETRY.md`; every provider still requires source/identity/semantics validation before its metrics are accepted.

Default setup policy: first reuse a safe existing official local authorization when possible. If that cannot provide the desired quota, one explicit provider setup/authorization is acceptable. Once configured, ordinary Cerith restarts must not require repeated setup. Credential/refresh ownership remains provider-specific and must be documented; silent renewal is acceptable only when Cerith legitimately owns renewable authorization or an official client owns and maintains the session.

This broader policy does **not** retroactively change CER-M3-002 Codex: its accepted production path remains read-only and non-renewing. A provider-specific reconnect after genuine expiration/revocation is different from asking the user to repeat configuration on every Cerith startup.

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

This subsection records the **historical CER-M3-001 contract audit** before provider implementation. It is a documentary acquisition/normalization contract, not an existing DTO or a reason to change Core/IPC. Evidence and upstream revisions live in [AI_QUOTA_REFERENCE_MATRIX.md](AI_QUOTA_REFERENCE_MATRIX.md). At the time of that audit no AI provider was implemented; CER-M3-002 subsequently implemented Codex, and D-024 now governs future expansion.

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

Provider disposal cancels in-flight polling/publication and clears cached quota values and auth references; no normalized cache is written to disk.

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

## CER-M3-002 — Codex first vertical slice (DONE; merged via PR #43)

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

Reset is a reported future boundary, not proof of a sliding-window algorithm, local midnight or an automatic refill. A supplied valid reset already elapsed at acquisition makes that window family Unavailable/reset_elapsed pending a new valid observation. Crossing reset invalidates cached family values; never publish an assumed 100%. If reset_after_seconds disagrees with reset_at, preserve reset_at and flag source_disagreement; do not silently reconstruct it from the countdown. The implementation allows only request round-trip duration plus one second for countdown quantization; larger disagreement remains flagged for investigation, never averaged.

A malformed reset_after_seconds does not replace or erase a valid reset_at; the reset metric retains that expiry boundary and reports invalid_reset_after.

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

MetricBus v1 has bounded subscriber queues but no retract or acknowledgement. A snapshot accepted into a queue before the provider observes an account change can still be read afterward; it is a pre-transition publication. A blocked publication and any in-flight response are canceled after the change is observed, and no old-owner result is queued afterward. Consumers that require a hard account boundary must reset their subscription when the selected owner changes.

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

Automated conformance and live validation for CER-M3-002 are complete. The same-account/workspace near-simultaneous official usage-UI comparison remains required and was completed. The second live implementation-level corroboration was a bounded, read-only terminal call to the already-installed official Codex app-server `account/rateLimits/read`, compared with one Cerith acquisition for the same selected account. The official UI and app-server matched the reported ordinary-window duration, used/remaining direction (within documented display/integer rounding) and `reset_at`. The `reset_after_seconds` versus `reset_at` disagreement remains visible as `source_disagreement`; do not average values or silently choose a side.

The current Gengchou, TokenTracker and AI Limits public Codex implementations were inspected as separate codebases. Each, and the official app-server path, ultimately reads the same `/wham/usage` upstream; none is an independent upstream oracle. No third-party quota-monitor software needs to be installed or run merely to repeat that same upstream read when an already-installed official terminal surface can safely provide equivalent implementation-level corroboration. This approach and exception are specific to CER-M3-002 and do not change future Claude or Antigravity acceptance gates. Keep all disagreements and their provenance explicit. Record only sanitized timestamps/version and comparison evidence. Source or identity mismatch blocks acceptance until explained. Never generate paid workload, consume reset credits or refresh credentials on Cerith's behalf. A screenshot containing private data is not a fixture. The M3-001 HTTP 200 probe establishes access/shape on one Windows host only; it does not satisfy this live cross-validation gate or validate all plans/auth modes.

## CER-M3-004 — Cursor first integration wave

Cursor FINAL PASS commit 24ca7027e204fb1361cb79f573ddf132c9490994 and Antigravity FINAL PASS 8889e4b34da1445c2bafe08093c11dc8c63fd1e3 are preserved in the staging history; no canonical-main merge. The provider uses fixed HTTPS routes observed in Cursor client behavior, without a public API stability guarantee: https://cursor.com/api/usage-summary, https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandAccessStatus, and https://api2.cursor.sh/aiserver.v1.DashboardService/GetSandUsageStatus.

The read-only auth path reads the current user's Cursor `state.vscdb` `cursorAuth/accessToken` and optional `.cursor/cli-config.json` `authInfo.authId`, verifies their normalized subject, and holds the session material only in memory. It does not refresh, write, or copy credentials. Plan quota comes only from the individual plan's direct used percentages; team usage, on-demand usage, and currency amounts are excluded. Grok Bot quota is a separate period and is read only when its access/grant status permits it; a zero included limit does not create a quota value.

Silent-startup auth acceptance is DEFERRED/UNRESOLVED, distinct from live UI comparison. The official [Cursor download page](https://cursor.com/download) redirected to the versioned Windows x64 user installer [Cursor 3.22.12](https://downloads.cursor.com/production/3a92974361033b2051526321308c2740fe5912c5/win32/x64/user-setup/CursorUserSetup-x64-3.22.12.exe). Product version was 3.22.12; Authenticode status was Valid for Anysphere, Inc.; artifact SHA-256 was 0B76176E54FCD9B51302968AEEBCE3EE708E93DA61071ECC2FCD50F79C1C3E79. The installer was not run or installed. A versioned official-upstream innoextract 1.10-dev Windows build, verified against its [official SHA-256 manifest](https://constexpr.org/innoextract/files/snapshots/innoextract-1.10-dev-2025-01-07/SHA256SUMS) (package digest 8c472a72b944e9848f75c4fb1f47b4907f0a7924ad226cac1beb51d31621abd2), advertises support through Inno Setup 6.3.3, detected this payload as Inno Setup 6.4.0.1, and failed parsing its setup headers. No auth/session code was extracted; this trace establishes neither lifetime/renewability of state.vscdb cursorAuth/accessToken nor a supported owner-renewal operation. First-party [CLI authentication](https://prod.cursor.com/docs/cli/reference/authentication) and [ACP](https://prod.cursor.com/docs/cli/acp) docs do not establish the desktop credential lifecycle. No user session, credential, auth state, or client execution was involved. Do not claim silent Cerith-only startup after renewable-token expiry until evidence shows either a nonrenewable long-lived desktop credential (with exceptional-expiry reconnect) or a safe supported official-owner renewal path. The synthetic RenewableTokenReplacementIsReadSilentlyOnTheNextPoll test proves reread/recovery only, not automatic renewal. A subsequent sanitized observation found the official browser UI accessible but no local Cursor desktop database/config; classify it as UI_ONLY. This proves neither same-account quota equality nor credential lifetime/renewal. The separate live UI/quota comparison remains NOT RUN. Cursor remains deferred from full product acceptance, but that provider-specific gate does not block other exact FINAL PASS imports.

| MetricId | Kind / unit / semantic type | Source meaning |
| --- | --- | --- |
| `ai.cursor.quota.plan.total.used_percent` | double / % / quota_used | Direct `individualUsage.plan.totalPercentUsed`. |
| `ai.cursor.quota.plan.auto.used_percent` | double / % / quota_used | Direct `individualUsage.plan.autoPercentUsed`. |
| `ai.cursor.quota.plan.api.used_percent` | double / % / quota_used | Direct `individualUsage.plan.apiPercentUsed`. |
| `ai.cursor.quota.plan.reset` | DateTimeOffset / timestamp | Explicit `billingCycleEnd`. |
| `ai.cursor.quota.plan.duration` | TimeSpan / duration | Explicit billing-cycle end minus start. |
| `ai.cursor.quota.grok_bot.period.used_percent` | double / % / quota_used | Reported Sand usage percentage. |
| `ai.cursor.quota.grok_bot.period.reset` | DateTimeOffset / timestamp | Reported Sand period reset. |
| `ai.cursor.quota.grok_bot.period.duration` | TimeSpan / duration | Reported Sand period end minus start. |

Percentages remain used percentages in the finite 0–100 range; they are never inverted into remaining capacity. Missing values publish valueless `Unavailable`, malformed values publish sanitized `Error`, and an elapsed reset invalidates that quota family. The provider bounds polling to 60 seconds, requests to 10 seconds, response bodies to 64 KiB, and cached values to 15 minutes.

Deterministic tests cover parser and descriptor semantics, synthetic local auth, identity changes, missing auth without network access, HTTP/auth failures, cancellation, timeouts, cache expiry, and bounded publication/shutdown. No same-account Cursor UI comparison or live quota request was performed. The offline tests and provider FINAL PASS do not establish live-account agreement.

## CER-M3-004 — ZCode coding-plan quota

The accepted ZCode implementation is staged with its original provider commits preserved. The provider reads the locally selected coding-plan family and configured API-key identity without writing credentials, then issues bounded read-only requests to the fixed Z.ai or BigModel quota endpoint. Ambiguous provider/account selection is rejected; credentials are not renewed or persisted by Cerith. No real-account quota comparison is claimed.

| MetricId | Kind / unit / semantic type | Source meaning |
| --- | --- | --- |
| `ai.zcode.coding_plan.level` | string / none / plan_id | Selected plan level. |
| `ai.zcode.coding_plan.five_hour.remaining_percent` | double / % / quota_remaining | `TOKENS_LIMIT` 5-hour bucket; `100 - percentage` where the source percentage is used. |
| `ai.zcode.coding_plan.five_hour.reset_at` | DateTimeOffset / timestamp | Explicitly zoned reset timestamp for that bucket. |
| `ai.zcode.coding_plan.five_hour.bucket_metadata` | string / JSON / quota_bucket_metadata | Source type/unit, actual bucket `number`, and bounded model details. |
| `ai.zcode.coding_plan.weekly.remaining_percent` | double / % / quota_remaining | `TOKENS_LIMIT` weekly bucket; `100 - percentage` where the source percentage is used. |
| `ai.zcode.coding_plan.weekly.reset_at` | DateTimeOffset / timestamp | Explicitly zoned reset timestamp for that bucket. |
| `ai.zcode.coding_plan.weekly.bucket_metadata` | string / JSON / quota_bucket_metadata | Source type/unit, actual bucket `number`, and bounded model details. |
| `ai.zcode.coding_plan.tools.remaining_percent` | double / % / quota_remaining | `TIME_LIMIT` tools bucket; `100 - percentage` where the source percentage is used. |
| `ai.zcode.coding_plan.tools.reset_at` | DateTimeOffset / timestamp | Explicitly zoned reset timestamp for that bucket. |
| `ai.zcode.coding_plan.tools.bucket_metadata` | string / JSON / quota_bucket_metadata | Source type/unit, actual bucket `number`, and bounded model details. |

Buckets are selected by source type, unit, and documented number where the family requires it; the parser does not infer reset units or rewrite the source bucket number. A numeric reset value is Unavailable because its unit is unverified. Invalid or ambiguous source values fail closed to valueless Error; absent fields are Unavailable.

## CER-M3-004 — GitHub Copilot quota

The accepted provider reuses the official GitHub CLI owner for bounded read-only authentication-status, user-identity, and Copilot quota requests, and accepts the github.com account route only. It checks that the authenticated identity is unchanged around acquisition. A sanitized prior HTTP 200 preserved that identity; this does not establish credential lifetime or renewal. Renewal class remains UNKNOWN, and no current same-account quota comparison is claimed.

| MetricId | Kind / unit / semantic type | Source meaning |
| --- | --- | --- |
| `ai.github_copilot.billing.token_based` | boolean / none / billing_mode | Direct `token_based_billing` indicator. |
| `ai.github_copilot.quota.chat.percent_remaining` | double / % / quota_percent_remaining | Direct chat `percent_remaining`; no amount is inferred. |
| `ai.github_copilot.quota.chat.reset_at` | DateTimeOffset / timestamp | Direct chat `quota_reset_at`. |
| `ai.github_copilot.quota.chat.unlimited` | boolean / none / quota_unlimited | Direct chat `unlimited` flag. |
| `ai.github_copilot.quota.chat.has_quota` | boolean / none / quota_source_flag | Raw chat `has_quota`; not an admission decision. |
| `ai.github_copilot.quota.completions.percent_remaining` | double / % / quota_percent_remaining | Direct completions `percent_remaining`; no amount is inferred. |
| `ai.github_copilot.quota.completions.reset_at` | DateTimeOffset / timestamp | Direct completions `quota_reset_at`. |
| `ai.github_copilot.quota.completions.unlimited` | boolean / none / quota_unlimited | Direct completions `unlimited` flag. |
| `ai.github_copilot.quota.completions.has_quota` | boolean / none / quota_source_flag | Raw completions `has_quota`; not an admission decision. |
| `ai.github_copilot.quota.premium_interactions.percent_remaining` | double / % / quota_percent_remaining | Direct premium-interactions `percent_remaining`; no amount is inferred. |
| `ai.github_copilot.quota.premium_interactions.reset_at` | DateTimeOffset / timestamp | Direct premium-interactions `quota_reset_at`. |
| `ai.github_copilot.quota.premium_interactions.unlimited` | boolean / none / quota_unlimited | Direct premium-interactions `unlimited` flag. |
| `ai.github_copilot.quota.premium_interactions.has_quota` | boolean / none / quota_source_flag | Raw premium-interactions `has_quota`; not an admission decision. |

`token_based_billing`, `quota_remaining`, and `credits_used` retain separate source meanings. Entitlement and legacy premium-request fields also remain distinct; amount fields are parser-only until their units and semantics are verified. `has_quota` is not authoritative under token-based billing. No credential-renewal class or silent future renewal is claimed.

## CER-M3-004 — OpenCode Go quota

The accepted provider reads the official OpenCode Go static API-key owner and makes bounded read-only requests to `https://opencode.ai/zen/go/v1/usage`. A non-empty `OPENCODE_AUTH_CONTENT` is the selected source; otherwise the reader uses `XDG_DATA_HOME/opencode/auth.json` or its built-in user-profile store. An invalid selected source fails closed without falling back to an older store. Only the API-key auth type is supported. A source or key change clears incompatible cache and cancels in-flight acquisition and pending publication. Setup and live-account validation remain pending; no MFA was encountered.

| MetricId | Kind / unit / semantic type | Source meaning |
| --- | --- | --- |
| `ai.opencode-go.quota.rolling.used` | double / % / quota_used | Direct rolling-window `usage.rolling.percent`; used percentage. |
| `ai.opencode-go.quota.weekly.used` | double / % / quota_used | Direct weekly `usage.weekly.percent`; used percentage. |
| `ai.opencode-go.quota.monthly.used` | double / % / quota_used | Direct monthly `usage.monthly.percent`; used percentage. |
| `ai.opencode-go.quota.rolling.reset` | DateTimeOffset / timestamp | Explicit-offset rolling `resetsAt`. |
| `ai.opencode-go.quota.weekly.reset` | DateTimeOffset / timestamp | Explicit-offset weekly `resetsAt`. |
| `ai.opencode-go.quota.monthly.reset` | DateTimeOffset / timestamp | Explicit-offset monthly `resetsAt`. |

Used percentages are not inverted into remaining capacity. Missing values remain Unavailable; malformed values fail independently as valueless Error. Balance, billing, and service-status fields do not become quota metrics.

## CER-M3-004 — Antigravity first integration wave

The accepted provider consumes the local Antigravity language server's observed `RetrieveUserQuotaSummary` response after verifying the current-user process, session generation, listener, and account owner. It does not treat this internal HTTPS loopback RPC as a supported public API. A sanitized bounded local-RPC observation recognized four quota buckets and a stable before/after status identity. This establishes source RPC shape and identity only; production parser-to-MetricSnapshot agreement, installed-version validation, backend-cache behavior, and UI comparison remain unverified, so full live validation remains NOT RUN.

| MetricId | Kind / unit / semantic type | Exact source field |
| --- | --- | --- |
| `ai.antigravity.quota.3p-weekly.remaining` | double / % / quota_remaining | `response.groups[].buckets[]` where `bucketId` is `3p-weekly`; `remainingFraction × 100`. |
| `ai.antigravity.quota.3p-5h.remaining` | double / % / quota_remaining | Same `remainingFraction` mapping for `3p-5h`. |
| `ai.antigravity.quota.gemini-weekly.remaining` | double / % / quota_remaining | Same mapping for `gemini-weekly`. |
| `ai.antigravity.quota.gemini-5h.remaining` | double / % / quota_remaining | Same mapping for `gemini-5h`. |
| `ai.antigravity.quota.3p-weekly.reset` | DateTimeOffset / timestamp | `resetTime` for `3p-weekly`. |
| `ai.antigravity.quota.3p-5h.reset` | DateTimeOffset / timestamp | `resetTime` for `3p-5h`. |
| `ai.antigravity.quota.gemini-weekly.reset` | DateTimeOffset / timestamp | `resetTime` for `gemini-weekly`. |
| `ai.antigravity.quota.gemini-5h.reset` | DateTimeOffset / timestamp | `resetTime` for `gemini-5h`. |

`remainingFraction` must be finite and in 0–1; conversion to percent is exact. Missing fractions/resets are Unavailable; malformed values are valueless Error. `resetTime` must include an explicit UTC or numeric offset. An elapsed reset invalidates both remaining and reset values for that bucket. Each bucket is keyed independently; do not clone a shared group value into per-model quotas. Valid returned quota values are Stale/Measured because the local RPC exposes no authoritative backend observation time and may serve backend-cached data. Do not relabel them Fresh.

The provider checks ownership every five seconds, polls every five minutes, bounds each source request to eight seconds and the response to 1 MiB, and expires cache after 15 minutes or at reset. Profile-verification uncertainty remains distinct from a positively observed account/session change. During positive Retry-After when ownership cannot be reverified, cached and pending quota publications are cleared; an immutable MetricBus sample already buffered before the transition cannot be retracted.

Live validation remains NOT RUN. Before claiming it, verify the actual installed Antigravity version, current response schema, and backend-cache freshness behavior against the official local language server.

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
