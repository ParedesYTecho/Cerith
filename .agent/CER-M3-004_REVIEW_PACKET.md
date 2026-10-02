# CER-M3-004 — fresh independent hostile review packet

## Exact target and authority

Canonical main: `72b43b268b4992d0b0ea45b6e29067d10450274f`.
PR: [#47](https://github.com/ParedesYTecho/Cerith/pull/47), OPEN / DRAFT.
Integration branch: `codex/CER-M3-004-integration`.
Code and local/hosted validation checkpoint: `96eefc59b704e6d6a34b683c7be9cfcf9f0a704f` (101 ahead, zero behind main). Later packet/audit commits are documentary; review the exact final PR head, including those changes.

Resolve and pin PR HEAD before reviewing:

```powershell
gh pr view 47 --repo ParedesYTecho/Cerith --json headRefOid,isDraft,state,statusCheckRollup
git fetch origin
git rev-parse origin/main origin/codex/CER-M3-004-integration
git diff --stat origin/main...origin/codex/CER-M3-004-integration
```

Use a fresh, read-only context. This Sol execution owner's hostile self-review is not independent approval. Assume every implementation and claim may be wrong. Report findings with severity, exact file/line, trigger and consequence. Do not write code, perform auth/login/refresh operations, request secrets, change accounts, create paid activity, mark ready or merge. Candidate status is not M3 DONE or approval.

## Delta and preservation

The wave adds ten non-Codex provider implementations/registrations/tests to the completed Codex slice and existing publication path: source → normalized MetricSnapshot → MetricBus → IPC → MetricId consumer. Core, IPC, WidgetHost and ControlCenter production code are unchanged. No dependency/project-file change exists against canonical main. The large delta is predominantly provider-specific code and deterministic tests; do not equate its size or passing count with correctness.

All ten staged source trees match their preserved provider tips except Ark Agent Plan's one-line cancellable acquisition wait. Several integration test files contain later coexistence/lifecycle/fixture corrections; review those deltas too. Qoder International's temporary merge `ab32c47` was reverted by `9319474`; its runtime code/registration is absent, while its branch remains intact. Historical WORKLOG events were retained.

## Staged provider lineage

Branch prefix for every non-Codex row is `codex/CER-M3-004-`. Short integration SHAs resolve in the retained Git history.

| Provider | Branch suffix / preserved tip | Integration history |
| --- | --- | --- |
| Codex | canonical main; PR #43 squash `3c3db6dd1102ec3ed2d95b6234e5550df08ab34a` | Already canonical; production source unchanged in this wave |
| Cursor | cursor / `24ca7027e204fb1361cb79f573ddf132c9490994` | `19c5820`, registration `9bb860d` |
| Antigravity quota | antigravity / `8889e4b34da1445c2bafe08093c11dc8c63fd1e3` | Preserved source baseline `fdd8a6d` and correction `1f9b436`; registration `9bb860d` |
| ZCode | zcode / `0a8dca3555e6c523ab7c40f4ba1e4303c9b13df9` | merge `198d25b`, registration `bd86b07` |
| GitHub Copilot | copilot / `6c59c3437f8b23692bab8d338c67c3ca0941827f` | merge `71fdb49`, registration `114b855` |
| OpenCode Go | opencode-go / `d102d7d301d523acbf4c107c09e8ccd04229b157` | merge `1b49128`, registration `114b855` |
| Kiro | kiro / `ca2b2c388be9b608fa96d8fdc3228d8da6ab7aac` | merge `4d7f0ad`, registration `0853789` |
| Command Code | command-code / `3919521a3615e2a7ee72cefd811e3572e3bf4124` | merge `82da0b3`, registration `0853789` |
| Qoder CN | qoder-cn / `b2bc2f59018e63aad5938e92a3494b9ded5e4baf` | merge `88ea7ba`, registration `c4df8c1` |
| Ark Agent Plan | ark-agent / `0c9ba1c04b1899a502f5fc554a7dc8196a41dd5f` | merge `1d5cb48`, registration `0633524`, shutdown correction `96eefc5` |
| Ark Coding Plan | ark-coding-plan / `c5841bf2ee6140d61ee517db62d8b176a996ce21` | merge `ed1a6ec`, registration `d3cfc24` |

## Source, identity, auth and live limits

Exact field-level definitions and source links remain canonical in [PROVIDERS_AI_QUOTAS.md](../docs/PROVIDERS_AI_QUOTAS.md); do not replace them with this inventory.

| Provider | Product/source | Credential and refresh owner; identity | Actual live classification / limitation |
| --- | --- | --- | --- |
| Codex | Ordinary ChatGPT subscription usage; fixed chatgpt.com wham/usage | Official file auth read-only; Cerith never renews. Selected account/source/config fingerprint and response-owner checks. | Historical same-account production/UI/app-server validation retained; reset source_disagreement retained. No new live proof this run. |
| Cursor | Individual plan usage-summary plus separately gated Sand/Grok Bot quota; fixed Cursor hosts | Reads official desktop DB/config subject; no credential writes/refresh. Key/source subject changes invalidate generation/cache. | Live comparison NOT RUN / historical UI_ONLY. Silent startup after renewable expiry remains DEFERRED/UNRESOLVED; implementation staging is not full product acceptance. |
| Antigravity quota | Existing local language-server RetrieveUserQuotaSummary; internal observed RPC, not a public API | Official application owns auth; ephemeral loopback CSRF held in memory. Current-user process/listener/session/account checked. | Schema/identity observation only; full parser/version/backend-cache/UI comparison NOT RUN. Returned quota is Stale/Measured because backend observation time is unavailable. Separate from Shadow research. |
| ZCode | Selected Z.ai/BigModel coding plan; fixed regional HTTPS quota routes | Official local API-key store/selection; static-key reread only. Family/product/org/project/credential scope fingerprint; ambiguity rejected. | Real-account comparison NOT RUN. Numeric reset units unverified and remain unavailable. |
| GitHub Copilot | Official gh github.com user + copilot_internal/user; quota distinct from token billing/credits | gh owns credentials; Cerith clears token overrides and uses read-only auth/user/quota operations. Before/after account and response ID checks. Credential class/lifetime/renewal UNKNOWN. | LIVE_VERIFIED only for historically available remaining-percent observations against the same account UI. Missing per-bucket resets remain unavailable; no universal bucket/plan or renewal claim. |
| OpenCode Go | Go plan, opencode.ai/zen/go/v1/usage | Official static API key; selected environment-content/store fails closed without fallback. Owner/key/source change cancels pending work/publication. No refresh grant. | SETUP/LIVE pending; no MFA encountered. No new live validation. |
| Kiro | Official CLI ACP _kiro/account/getUsage | Official CLI owns auth; Cerith does not write it. Separate whoami owner observations bracket acquisition and cancel changed-owner publications. | LIVE_NOT_AVAILABLE_ON_THIS_HOST retained; silent renewal empirically unverified/deferred. No compatible signed-in local session. |
| Command Code | Fixed Command Code org/usage/subscription routes; quota separate from plan/status | Existing environment key or official auth-file owner; no renewal claim. Credential generation and authenticated org scope checked. | LIVE_NOT_AVAILABLE_ON_THIS_HOST retained; compatible signed-in local session absent. |
| Qoder CN | Official signed CN CLI control get_usage_info; regional product | CLI owns auth/any renewal; no Cerith auth storage. initialize/account_info before/after and usage.userId match. No cross-poll cache. | LIVE_NOT_AVAILABLE_ON_THIS_HOST retained. Positive installed signer and same-account official comparison unverified. Organization-package fields deliberately valueless. |
| Ark Agent Plan | Personal AFP windows via official usage plan plus registered GetAFPUsage daily action | Official pinned CLI owns SSO/STS renewal. Profile-pinned daily read, post-acquisition unforced usage identity/profile/region/project/auth/tier/subscription match. No cache. | LIVE_NOT_AVAILABLE_ON_THIS_HOST retained; compatible CLI/profile/session absent. No live rotation/concurrency proof added. CLI does not expose Retry-After. |
| Ark Coding Plan | Personal Coding Plan via official usage plan coding-plan, production env, pinned profile | Official CLI owns SSO renewal. auth whoami before/after; only sso accepted; HMAC process identity tag. Package manifest/hash checks are not independent publisher signing proof. | LIVE_NOT_AVAILABLE_ON_THIS_HOST retained; CLI absent. No live renewal/rotation or simultaneous Ark-client proof added. |

Absence of host access must not hide unresolved source semantics. Retained staged providers with explicit auth/live limitations need the reviewer's judgment; do not silently upgrade those limitations to PASS.

## Metric inventory and reset semantics

Braces below expand to exact IDs, not guessed buckets. The catalog totals 93 AI MetricIds (including Codex); Qoder International has no candidate IDs. Typed per-field source contracts are in the canonical provider document.

| Provider / count | Exact IDs or expansion | Units/direction/reset |
| --- | --- | --- |
| Codex / 6 | ai.codex.quota.{primary,secondary}.{remaining,reset,duration} | Remaining %, explicit reset_at, explicit TimeSpan duration; slots do not imply session/week. |
| Cursor / 8 | ai.cursor.quota.plan.{total.used_percent,auto.used_percent,api.used_percent,reset,duration}; ai.cursor.quota.grok_bot.period.{used_percent,reset,duration} | Direct used %, explicit source period/reset/duration. |
| Antigravity / 8 | ai.antigravity.quota.{3p-weekly,3p-5h,gemini-weekly,gemini-5h}.{remaining,reset} | Fraction ×100 remaining %, explicit zoned reset; elapsed reset invalidates bucket. |
| ZCode / 10 | ai.zcode.coding_plan.level; ai.zcode.coding_plan.{five_hour,weekly,tools}.{remaining_percent,reset_at,bucket_metadata} | Remaining =100−source used %. Explicit zoned reset; numeric unit unknown. Bounded declared JSON quota metadata contains type/unit/source bucket number/model names, not a future event/history schema. |
| Copilot / 13 | ai.github_copilot.billing.token_based; ai.github_copilot.quota.{chat,completions,premium_interactions}.{percent_remaining,reset_at,unlimited,has_quota} | Direct remaining %, booleans separate from admission, explicit per-bucket Unix reset only; missing stays missing. |
| OpenCode Go / 6 | ai.opencode-go.quota.{rolling,weekly,monthly}.{used,reset} | Direct used %, explicit-offset source reset; no balance/billing substitution. |
| Kiro / 4 | ai.kiro.credits.included.{remaining,reset}; ai.kiro.credits.{addon,bonus}.remaining | Decimal credits; included reset Unix seconds; addon/bonus do not inherit guessed resets. |
| Command Code / 6 | ai.commandcode.quota.{5h,weekly}.{used_percent,reset}; ai.commandcode.subscription.{status,plan} | used/cap ×100 %, source Unix milliseconds; plan/status strings are not quota. |
| Qoder CN / 10 | ai.qoder-cn.quota.{included,add_on}.{total,used,remaining}; ai.qoder-cn.plan.expires_at; ai.qoder-cn.org_resource_package.{cap,used,remaining} | Credits direct fields; expiry Unix milliseconds is not quota reset. Organization metrics remain unavailable. |
| Ark Agent / 16 | ai.ark.agent_plan.{5h,daily,weekly,monthly}.{used,total,used_percent,reset} | Decimal AFP, direct percent except daily derived Used/Quota ×100; quota zero gives unavailable percent; daily reset Unix milliseconds, other resets explicit offset. Over-quota daily percentage is not clamped. |
| Ark Coding / 6 | ai.ark-coding-plan.quota.{session,weekly,monthly}.{used,reset} | Direct used % (despite suffix used), explicit offset reset; elapsed reset invalidates both metrics. |

A passed reset never manufactures refill, zero used or 100% remaining. Unknown/missing/unavailable/error/stopped states never gain invented numeric values.

## Preserved blocked/deferred work

Current first-party versions/pins, two investigated avenues and exact unblock contracts are in [AI_QUOTA_REFERENCE_MATRIX.md](../docs/AI_QUOTA_REFERENCE_MATRIX.md#cer-m3-004-priority-blocker-recheck--2026-10-02).

| Provider | Disposition / branch tip | Blocker / what changes it |
| --- | --- | --- |
| Claude | EVIDENCE_BLOCKED; claude / 51afc1cc91b4b461c93715e9c324c918a1cbc6fc | Approved quiet renewable subscription usage bridge; official owner persistence/concurrency and stable account binding. Experimental SDK usage seam does not establish this contract. CLI/API OAuth is another product. Focused 18/18 pass. |
| Gemini CLI / Code Assist | EVIDENCE_BLOCKED; gemini / 9704da8c73ca494b55af013b2d431669266c5d79 | Enterprise scope remains distinct; consumer internal route and direct plaintext token writes do not establish supported acquisition/sole-writer persistence. Require product-appropriate official output/renewal bridge. Focused 25/25 pass. |
| Grok Build | EVIDENCE_BLOCKED; grok-build / d51fba3184b606c36b141e2f9c232c85ef03d0ff | Current source's no-API-key policy narrows blocker; require version-bound deployed session-only ACP, renewal-failure and stable-owner proof. Preserve auth_provenance_unavailable. Focused 12/12 pass. |
| Qoder International | DEFERRED_AFTER_M3_QUOTA_WAVE; qoder / 7a1a89e9f88ea1de57f5bf180f4e8bfd382c88fb | Unsupported desktop IPC auth/status, credit/usage; documented CLI /usage / SDK getUsageInfo are a different unintegrated route. Require documented IPC or separately justified low-dependency official SDK seam. |
| Kimi | DEFERRED_AFTER_M3_QUOTA_WAVE; kimi / 25630291296c7b8721036dc8ff7dd48a98aebb7d | Official Windows OAuth writer/reader locking/rotation not proven; require safe client-owned bridge or approved independent grant. Existing tests/evidence preserved. |
| Devin | DEFERRED_AFTER_M3_QUOTA_WAVE; devin / 1a8e169e2bf73f7650ed22c630cb01a63739eafb | Product/team and quota-source provenance unproven; GetPlanStatus alone does not establish Devin subscription quota. Existing work preserved. |

No HUMAN_GATE_PENDING was newly encountered. These branches were not imported or reset. Locally preserved blocked/deferred tips may have commits absent from remotes; inspect Git rather than assuming an upstream tracking branch exists.

## Lifecycle, cache and privacy boundaries to challenge

Providers use fixed endpoints/official commands, bounded bodies/process output and sanitized error codes. Ownership-generation implementations cancel changed-owner work/publication and keep stale ObservedAt; they must not resurrect another owner's values. Retry-After suppresses acquisition, not cache aging. Monotonic elapsed time protects TTL from wall-clock rollback. No universal auth/framework/history layer was added.

Qoder CN and Ark Agent Plan are sequential, uncached acquisitions with before/after checks, not continuously observable account transactions. Account/profile changes after the final check are an observation boundary; do not claim instantaneous revocation. Ark Agent's WaitAsync correction bounds service shutdown even if acquisition ignores cancellation; it does not prove forced termination of an arbitrary dependency. Existing CLI cleanup remains responsible for child termination. MetricBus v1 cannot retract immutable snapshots already queued before a transition; strict boundary consumers must reset.

No credential material, personal account payloads/screenshots/transcripts or private source was committed. This run performed package/version/source reads and deterministic checks only. It did not copy OAuth state, scrape cookies, login, run inference, buy/reset resources or add credential ownership. Review subprocess environment overrides, endpoint trust, auth class provenance, account tags and all ErrorCode creation paths.

## Validation commands and results

At 96eefc5, Release build: 0 warnings/errors. Direct dotnet test commands use the existing MTP projects and fail on nonzero exits; no empty-suite tolerance was used here.

```powershell
dotnet build Cerith.sln --configuration Release --no-restore --verbosity minimal
dotnet test tests/Cerith.Core.Tests/Cerith.Core.Tests.csproj --configuration Release --no-build --verbosity minimal
dotnet test tests/Cerith.Telemetry.Tests/Cerith.Telemetry.Tests.csproj --configuration Release --no-build --verbosity minimal
dotnet test tests/Cerith.Integration.Tests/Cerith.Integration.Tests.csproj --configuration Release --no-build --verbosity minimal
```

Results: Core 39/39, Telemetry 356/356, Integration 10/10 =405/405, zero skipped. Focused Ark Agent Plan 20/20; Ark Coding Plan combined selection 19 cases; preserved Claude 18/18, Gemini 25/25, Grok 12/12. The new non-cooperative shutdown regression failed before the one-line fix and passed afterward.

```powershell
dotnet test tests/Cerith.Telemetry.Tests/Cerith.Telemetry.Tests.csproj --configuration Release --no-build --filter 'FullyQualifiedName~HardwareAndAcceptedProvidersPublishTwiceWhileCursorRequestIsGated' --verbosity minimal
dotnet test tests/Cerith.Telemetry.Tests/Cerith.Telemetry.Tests.csproj --configuration Release --no-build --filter 'FullyQualifiedName~UnexpectedProviderFailureStaysIsolatedUntilBoundedShutdown' --verbosity minimal
```

Coexistence 1/1; supervisor fault-isolation selection 11/11. The latter tests the shared runner with injected provider failures, not eleven live-client faults. Full suite includes provider cancellation/backpressure/owner/reset/cache tests; it is not long-duration/live/concurrency proof.

At 96eefc5: [Build and Test](https://github.com/ParedesYTecho/Cerith/actions/runs/37005658373) SUCCESS; [Repository Quality](https://github.com/ParedesYTecho/Cerith/actions/runs/37005658310) SUCCESS; [CodeQL](https://github.com/ParedesYTecho/Cerith/actions/runs/37005658376) SKIPPED under D-013, not a successful security scan. Require green build/quality on the exact final PR head as well.

Hygiene: git diff --check PASS, 135 tracked filenames checked, no forbidden artifact suffixes. Bounded added-diff patterns found zero known credential/bearer/private-key/personal-path matches; eight synthetic .invalid fixture emails and zero others. This is not a comprehensive secret scan. Dependency diff is empty; no new license/dependency decision was needed. Graph update preserved the prior corpus and replaced two changed AST files: 3,289 nodes, 8,793 edges, 160 communities; zero missing source files, dangling endpoints or self-loops; no external model. Shared namespace coalescing is a known extractor limitation; documentation semantic extraction was not refreshed. Graph structure is navigation evidence only.

## Important files and exact review questions

Start with Program.cs, CodexQuotaProviderTests.cs and the provider source/test folders under src/Cerith.TelemetryService/Providers/AI and tests/Cerith.Telemetry.Tests/Providers/AI. Then read canonical provider/metric/privacy docs, task/state/handoff, this packet and the priority recheck. No durable architecture decision was changed; roadmap current-wave scope and temporary execution routing were reconciled without rewriting D-024 or historical events.

1. Do sources prove the named subscription product, account/team/region scope, used/remaining units and resets? Challenge API billing substitutions, absent fields, reset guesses and weakly supported metadata.
2. Are auth provenance and credential ownership actually established? Challenge Cursor expiry, Copilot unknown class, CLI renewal claims, executable/package trust, and simultaneous Ark client invocations. Lack of live access is not semantic proof.
3. Can account changes, slow/non-cooperative acquisition, backpressure, late responses, wall-clock rollback, reset expiry or Retry-After republish wrong-owner/invalid cached values? Trace actual code, not test names.
4. Does provider failure or shutdown threaten the service or another provider? Check constructor failures, child/stream drain, Stopped publication deadlines and the new Ark wait boundary.
5. Are tests meaningful and fail-closed? Check parser types/missing fields/body limits/HTTP classes/timeouts/identity/reset/cancellation plus integration fixture corrections; reject tests that merely mirror constants.
6. Is every file, type and dependency necessary? Check bounded declared ZCode metadata against D-024's no-hidden-history rule. Confirm no generic auth/provider framework, copied GPL code, secret/private artifact or new publication path.
7. Do documents distinguish staged implementation, unresolved auth, same-account live observations and candidate review? Confirm no 17/17 gate, M3 DONE, ready-to-merge or independent self-review claim.

Stop after reporting actionable findings or explicitly scoped absence of findings. Independent review and subsequent Advisor/user decision remain mandatory; PR #47 must remain draft and canonical main untouched.
