# AI Telemetry

Status: Canonical product/acquisition direction for AI observability beyond the quota-only contract.

## Purpose

Cerith's AI capability is broader than subscription quota, but it must keep different semantics separate.

```text
AI telemetry
├─ quota / usage limits
├─ local token/model activity
├─ derived cost
├─ project/session attribution
└─ service status (future/optional)
```

Quota normalization and auth/failure rules live in `PROVIDERS_AI_QUOTAS.md`. This document owns the broader local AI-tool catalog, acquisition principles and cross-milestone product direction.

## Reference snapshots

Current reference revisions used for the product direction:

- TokenTracker `a8520617ece3f15a07c342e2c7909eb311234522` (v1.1.3 release commit);
- Gengchou `4d02b9d1f85e806c92c4defc8e8c4c822467b6f3`;
- AI Limits `a42bbd043070538ff5c4161b7eda952720173345`.

Exact provider semantics can decay independently of these revisions; revalidate implementation-significant behavior.

## Current breadth targets

### Local AI activity: 42-tool reference catalog

TokenTracker's pinned reference advertises and implements a 42-product activity catalog. The catalog is evidence that broad coverage is practical, but it is **not** 42 homogeneous local adapters and it is not a claim of Windows runtime acceptance in Cerith. It mixes passive readers, managed hooks, local databases, remote queries and shared parser families:

1. Claude Code
2. Codex CLI
3. AStudio
4. Cursor
5. Gemini CLI
6. Antigravity
7. Kiro
8. OpenCode
9. OpenClaw
10. Every Code
11. Hermes Agent
12. GitHub Copilot
13. Kimi Code
14. CodeBuddy
15. WorkBuddy
16. Grok Build
17. oh-my-pi
18. OmO
19. pi
20. Dots
21. Prime Agent
22. Craft Agents
23. Reasonix
24. Kilo CLI
25. Kilo Code
26. Roo Code
27. Zed Agent
28. Goose
29. Droid
30. Mimo Code
31. ZCode
32. Qoder
33. AnythingLLM Desktop
34. Claude Science
35. DeepSeek Harness
36. TRAE Work CN
37. LM Studio
38. Unsloth Studio
39. Devin CLI
40. Cline
41. MiniMax Code
42. Command Code

This is a **coverage target and research catalog**, not a claim that Cerith already supports all 42. Each collector enters only after its source, fields, platform behavior, privacy boundary, deduplication and semantics are verified.

Reference-specific caveats that matter before task decomposition:

| Reference item | Acquisition/platform caveat |
| --- | --- |
| Cursor | Activity includes a remote usage query authenticated from local state; it is not purely passive local-log telemetry. |
| TRAE Work CN | Uses a remote internal API path and is opt-in in the reference; network/auth/privacy are part of acceptance. |
| Claude Science | Reference Windows support goes through WSL for its inspected local database path. |
| Devin CLI | Reference Windows activity acquisition also relies on WSL for the inspected path. |
| Dots | Reuses the `pi` acquisition/parser family rather than defining an independent installation/parser. |
| GitHub Copilot | Combines multiple sources (current SQLite usage, legacy baseline and OTEL paths); overlap/dedup is part of the contract. |
| Qoder CN | Regional variant with separate paths/endpoints; do not collapse it into international Qoder. |
| Shared parser families | Codex-family, Claude-family, pi/OMP-family and OpenCode-family implementations reduce repeated mechanics but do not erase product-specific semantics. |

The 42 catalog entries below use TokenTracker v1.1.3 at [commit a8520617ece3f15a07c342e2c7909eb311234522](https://github.com/xiufengsun/TokenTracker/commit/a8520617ece3f15a07c342e2c7909eb311234522) as research evidence, not Cerith authority. TT paths/lines refer to that pin. Windows statements describe documented/reference paths, not host product presence.

| Target | Acquisition type | Evidence source | Windows | Direct fields | Privacy | Main semantic risk | M3.B status | Reason | Evidence to revisit |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| Claude Code | SessionEnd plus local JSONL | TT src/lib/rollout.js:4484-4510, 4780-4815; Anthropic [hooks](https://code.claude.com/docs/en/hooks) | Native ref path; WSL too | Model, input/output/cache/reasoning, time, message/request IDs, project grouping; no source cost | Transcript contains content; scalar projection required | v2.1.42 issue reports placeholder output counts; final persisted usage is version-sensitive | EVIDENCE_BLOCKED | Supported JSONL output semantics unresolved | Versioned authoritative semantics and sanitized update/subagent/dedup/content-free fixtures |
| Codex CLI | Passive rollout JSONL; optional notify hook | TT src/lib/codex-token-usage.js:3-19; OpenAI [recorder](https://github.com/openai/codex/blob/main/codex-rs/rollout/src/recorder.rs) | Native Windows and WSL | Model, input/output/cache/reasoning/total, time, thread/turn/response IDs, workspace; no native cost | Logs contain conversation and tool content | Cumulative snapshots, compaction, forks and copied roots may duplicate counts | DEFERRED_AFTER_M3 | Plausible local source; breadth follows OpenCode | Release-pinned reset, fork/compaction and Windows/WSL dedup fixtures |
| AStudio | Notify hook plus local sessions | TT README.md:240,366; src/commands/init.js:681-686; no verified first-party versioned identity/schema | Windows unknown | Hook time/source and optional cwd; model/tokens/cost/identity unverified | Hook/session content boundary unclear | Product identity, schema and platform unresolved | EVIDENCE_BLOCKED | No target-specific source/privacy contract | First-party identity/schema/Windows path and sanitized token/model/time/dedup fixture |
| Cursor | Local auth state plus remote usage query | TT src/lib/cursor-config.js:10-34,45-74; src/lib/rollout.js:6918-6952; first-party [Admin API](https://prod.cursor.com/docs/account/teams/admin-api) is team-scoped | Native auth path | Model/date, input/output/cache/total, reference-priced cost; no event ID/project | Authenticated remote request using local state | User-scope and safe supported auth contract unresolved | EVIDENCE_BLOCKED | No verified user-scope read-only contract | First-party account/auth scope, stable fields/identity and privacy contract |
| Gemini CLI | SessionEnd-triggered session file | TT src/lib/gemini-config.js; src/lib/rollout.js:222-236,1206-1235,2786-2855; first-party [types](https://github.com/google-gemini/gemini-cli/blob/main/packages/core/src/services/chatRecordingTypes.ts#L14-L23) describe JSONL/patches | Native Windows 11 supported | Model, input/output/cache/thought/tool/total, time, session/message IDs, project hash | Sessions contain messages/tool data | Reference file shape conflicts with current JSONL/patch format | EVIDENCE_BLOCKED | Reader shape differs from current documented format | Pinned schema and patch/rewind, bucket, dedup and content-free fixtures |
| Antigravity | Transcript JSONL plus SQLite/protobuf | TT src/lib/rollout.js:20253-20345,21199-21280; decoder; first-party [hooks](https://antigravity.google/docs/hooks) cover transcript/model, not DB counters | Windows product; DB path unverified | Model/time/conversation ID; opaque counters or estimates; no confirmed project/cost | Transcript and DB content-bearing | Reverse-engineered fields and estimated counts lack producer contract | EVIDENCE_BLOCKED | Token schema and native source path unverified | Versioned path/schema and sanitized DB/transcript linkage/content-free fixture |
| Kiro | Hybrid SQLite and session JSON/JSONL | TT src/lib/rollout.js:7030-7100,7240-7275; first-party [session docs](https://kiro.dev/docs/cli/chat/session-management/) confirm storage, not token fields | Windows CLI documented; ref DB path speculative; WSL too | Model, IDs/time/workspace; v2 tokens estimated from characters; no native token cost; credits separate | Stored prompts/tool results | Estimated tokens, multiple stores and uncertain locator/identity | EVIDENCE_BLOCKED | No direct token contract or confirmed native path | Authoritative fields/estimate semantics, Windows path and cross-store dedup fixture |
| OpenCode | Passive local SQLite | First-party v1.17.9 schema/path sources are linked in the OpenCode section below; Cerith [PR #50](https://github.com/ParedesYTecho/Cerith/pull/50), merged at 8dfd5f56aa63355bd0d952d774a5d84af750afa2 | Native data root or explicit override; channel DBs not guessed | Five direct token fields, model/provider IDs, completion time; no total/cost/session/project/row ID | JSON contains content; fixed scalar projection only | Internal version-sensitive schema; only 1.17.9 accepted | IMPLEMENTED_ACCEPTED | First passive/local slice through MetricSnapshot → MetricBus → Named Pipe → MetricId consumer | Requalify source version and privacy projection before widening versions |
| OpenClaw | Plugin-triggered local transcript scan | TT src/lib/openclaw-hook.js; src/lib/rollout.js:1529-1575; current first-party [session docs](https://github.com/openclaw/openclaw/blob/main/docs/concepts/agent.md#L252-L258) describe active SQLite | Ref defaults WSL; native override | Session/agent, model/time, transcript usage; project/cost unclear | Plugin accesses conversations; JSONL content-bearing | Pinned active-JSONL assumption conflicts with current SQLite | EVIDENCE_BLOCKED | Active store and plugin/scan ownership unresolved | Supported SQLite schema, scalar fields, Windows/WSL discovery and dedup fixture |
| Every Code | TOML notify hook; shared Codex normalization | TT README.md:283; src/lib/codex-token-usage.js:3-19; src/lib/rollout.js:4749-4756; first-party [notify contract](https://github.com/just-every/code/blob/main/code-rs/config.md#L642-L651) includes prompt/assistant messages | Ref claims Windows/WSL; first-party path unverified | Possible model/time/token/cache/session fields; hook sees prompt/reply | Hook crosses content boundary | Shared normalization does not establish target schema or passive route | EVIDENCE_BLOCKED | Safe metadata-only acquisition unestablished | Versioned passive schema/path or metadata-only hook with dedup/privacy evidence |
| Hermes Agent | Passive SQLite | TT src/lib/rollout.js:7357-7380,7415-7475; first-party [storage guide](https://github.com/NousResearch/hermes-agent/blob/main/website/docs/developer-guide/session-storage.md#L195-L232) defines Windows storage/per-model rows | Native Windows path documented | Reference session/model/time, input/output/cache/reasoning/count; no per-model ledger/cost | DB has messages; selected query metadata-only | Session totals may combine model/task usage; profiles have separate roots | EVIDENCE_BLOCKED | Model attribution/profile root unresolved | Per-model schema, profile-root contract, sanitized reconciliation fixture |
| GitHub Copilot | SQLite usage, OTEL JSONL, legacy baseline | TT src/lib/rollout.js:16996-17040,17815-17830,18712-18727; GitHub [CLI config docs](https://docs.github.com/en/copilot/reference/copilot-cli-reference/cli-config-dir-reference) | Native profile path; WSL optional for OTEL | Model, token/cache/reasoning, time, event/session IDs; no selected cost/project | Selected query scalar; wider stores may contain content | SQLite, legacy and OTEL overlap/reset IDs | DEFERRED_AFTER_M3 | Multiple plausible local sources/dedup; later breadth | Pinned schemas and baseline/OTEL/reindex/content-free fixtures |
| Kimi Code | Passive session wire JSONL | TT src/lib/rollout.js:9403-9422; first-party [session guide](https://moonshotai.github.io/kimi-code/en/guides/sessions.html) describes request traces | Native Windows product/ref path | Step-end input/output/cache, model alias, time/agent | Wire includes tool schemas, params and MCP listings | No first-party versioned token event semantics | EVIDENCE_BLOCKED | Usage contract/privacy projection not established | Versioned usage schema and sanitized Windows field/ID/content fixture |
| CodeBuddy | Passive project/session JSONL; legacy IDE-log fallback separate | TT src/lib/rollout.js:9635-9664; first-party [hook docs](https://www.codebuddy.cn/docs/cli/hooks) | Native ref profile path; CLI/IDE differ | Model, prompt/completion/cache/reasoning, time/session; prompt includes cache | JSONL/hooks may contain prompt/tool content | Claude-like shape but product-specific cache semantics | DEFERRED_AFTER_M3 | Plausible passive source; later breadth | Versioned CLI/IDE path and cache/subagent/content-free fixtures |
| WorkBuddy | Passive project JSONL plus SQLite summary | TT src/lib/rollout.js:10347-10382; Tencent [Windows install guide](https://www.workbuddy.ai/docs/workbuddy/From-Beginner-to-Expert-Guide/Installation-Win-Guide) | Native ref profile path; WSL separate | Assistant/function-call usage, nested agents, model/time/cache; SQLite used/size is context/credits | JSONL content-bearing | Cache-inclusive prompt, function calls and nesting differ from CodeBuddy | DEFERRED_AFTER_M3 | Plausible local source; later parser breadth | Versioned usage ownership/nesting/cache/content-free fixtures |
| Grok Build | Local session/events and hooks | TT src/lib/rollout.js:19237-19255,19399-19410; test/grok-parser.test.js; first-party activity contract not established | Ref native profile path; official path/schema unknown | Model/time and input/output/reasoning/cache where present; cost unknown | Session/hook may expose content | totalTokens may describe context, not per-turn usage | EVIDENCE_BLOCKED | Event schema/token semantics unresolved | First-party versioned contract, Windows path and per-turn/context-total fixture |
| oh-my-pi | Passive JSONL; optional notification | TT src/lib/rollout.js:11079-11161, 15803-15813; first-party [session](https://github.com/can1357/oh-my-pi/blob/main/docs/session.md) and [RPC](https://github.com/can1357/oh-my-pi/blob/main/docs/rpc.md#L758) | Native Windows .omp path | Model, input/output/cache/total/reasoningTokens, time/entry/cwd; estimated cost | JSONL contains conversation/tools | Pi envelope shared, schema/reasoning differs | DEFERRED_AFTER_M3 | Plausible passive source; later breadth | Versioned usage and shared-root/co-install dedup fixture |
| OmO | Passive JSONL including nested agents | TT src/lib/rollout.js:11264-11345,15438-15475; first-party [README](https://github.com/code-yeongyu/oh-my-openagent) | Native Windows path | Model/provider, input/output/cache/total/reasoning, time/entry/cwd | Transcript content-bearing | Reasoning included in output unlike OMP; subagents can duplicate | DEFERRED_AFTER_M3 | Plausible source; product-specific breadth later | Versioned reasoning and parent/subagent ownership fixture |
| pi | Passive local JSONL | TT src/lib/rollout.js:15831-15842, 16129-16183; first-party [format](https://pi.dev/docs/latest/session-format), [types](https://pi.dev/docs/latest/message-types), [Windows](https://pi.dev/docs/latest/windows) | Native Windows documented | Model/provider, input/output/cache/total, time/entry/cwd; reasoning schema-sensitive | Session files include system/user/tool text | Docs say reasoning included in output; pinned parser treats reasoningTokens additive; Pi/OMP share override | DEFERRED_AFTER_M3 | Plausible source; later breadth | Versioned reasoning/cache and shared-root ownership/dedup fixture |
| Dots | TT README.md:260; src/lib/rollout.js:15831-15845; no distinct reader | First-party [OpenAI Dots](https://chatgpt.com/features/dots/) is cloud-hosted | No local Windows path established | Pi fields only if same provider; no independent Dots fields | Official product is remote, not local metadata | Product identity/source mismatch | EVIDENCE_BLOCKED | Catalog name not tied to reference Pi provider | First-party identity/schema link or documented Windows export/API with privacy/dedup |
| Prime Agent | Passive session JSONL | TT src/lib/rollout.js:15925-15960; first-party [format](https://github.com/PrimeIntellect-ai/prime-agent/blob/main/packages/coding-agent/docs/session-format.md#L211-L219), [child attribution](https://github.com/PrimeIntellect-ai/prime-agent/blob/main/packages/coding-agent/docs/session-format.md#L357-L423) | Native ref path; WSL too | Model/provider, time, IDs, cwd, tokens/cache/cost | JSONL has prompts/replies/tools | Parent usage may include child usage | EVIDENCE_BLOCKED | Usage ownership unresolved | Sanitized fork/child ownership, dedup and Windows fixture |
| Craft Agents | Passive first-line JSONL header | TT src/lib/rollout.js:16484-16524; first-party [repository](https://github.com/craft-ai-agents/craft-agents-oss) has local/remote modes | Native reference path; Windows app | Header model/connection/time/input/output/cache/total/workspace; no cost/reasoning | Later JSONL contains content; header only read | Header schema not versioned; remote changes storage | EVIDENCE_BLOCKED | Local/remote contract not authoritative | Versioned header and cumulative-update fixtures across Windows/custom/remote roots |
| Reasonix | Reference scans JSONL telemetry sidecars | TT src/lib/rollout.js:16602-16640; first-party [v1 architecture](https://github.com/esengine/DeepSeek-Reasonix/blob/v1/docs/ARCHITECTURE.md#L311-L315) names a different path | Windows product; ref layout unconfirmed | Reference claims model/token/cache/time; stable identity/cost unknown | Metadata-only is reference claim, not producer contract | Product/version/path mismatch; cache inclusion ambiguous | EVIDENCE_BLOCKED | Sidecar not tied to supported Windows schema | Supported path/schema and cache/request/reset fixture |
| Kilo CLI | Local SQLite via OpenCode-derived reader | TT README.md:255; src/commands/sync.js:1529-1547; src/lib/rollout.js:4916-4970; first-party [history docs](https://kilo.ai/docs/code-with-ai/agents/session-history#find-the-local-database) | Conflicting native paths: reference AppData vs docs user data directory | Model/provider, input/output/cache/reasoning, IDs/update time; project/cost unclear | DB includes chat history | Path conflict and parser reuse does not prove schema | EVIDENCE_BLOCKED | Locator/field semantics unresolved | Versioned Windows path/schema and sanitized token/cwd/identity fixture |
| Kilo Code | Passive task ui_messages JSON | TT src/lib/rollout.js:11349-11456 | Native path; WSL optional | Provider/protocol, input/output/cache, time/task ID; model not reliably per turn | Content-bearing JSON projected to scalars | Rewrites/placeholders/timestamp dedup affect counts | DEFERRED_AFTER_M3 | Plausible source with known limits; later breadth | Versioned completion/rewrite/deletion and content-free fixture |
| Roo Code | Task JSON plus conversation history; separate parser | TT src/lib/rollout.js:11470-11520 | Native VS Code root; WSL optional | Provider, input/output/cache, time/task ID; model from latest history; cost/project unclear | Both files content-bearing | Latest model tag may misattribute earlier turns | DEFERRED_AFTER_M3 | Plausible source; later breadth | Versioned model-switch/rewrite/dedup and content-free fixture |
| Zed Agent | SQLite threads DB | TT src/lib/rollout.js:12179-12215; first-party [DB source](https://github.com/zed-industries/zed/blob/main/crates/agent/src/db.rs#L2281-L2305) | Native LocalAppData path | Provider/model, input/output/cache, thread/update time; no cost/reasoning | Compressed BLOB has messages; scalar projection possible | Row update time not request time; cumulative fallback schema-sensitive | DEFERRED_AFTER_M3 | First-party storage/usage support plausible breadth | Versioned JSON/zstd, cumulative/watermark and content-free fixtures |
| Goose | Passive SQLite session rows | TT src/lib/rollout.js:14191-14230; first-party [logs guide](https://github.com/aaif-goose/goose/blob/main/documentation/docs/guides/logs.md#L201-L229), [ledger](https://github.com/aaif-goose/goose/blob/main/crates/goose/src/session/session_manager.rs#L957-L1022) | First-party Windows path differs from reference | Session/model/time, cumulative/last-turn input/output/total; no cache/project/ledger | DB contains messages; query metadata-only | Path conflict; coarse totals misattribute model changes; reasoning inferred | EVIDENCE_BLOCKED | Locator and usage granularity conflict | Supported path and per-call model/cache/cost reconciliation fixture |
| Droid | Reference reads cumulative settings JSON sidecar, not transcript | TT src/lib/rollout.js:14497-14532; first-party [settings](https://docs.factory.com/droid-cli/settings#where-settings-live), [SDK](https://docs.factory.com/sdk/typescript#sessions) | Settings path known; usage sidecar path unknown | Model/provider, cumulative input/output/cache/thinking, mtime/file session ID; no cost/project | Metadata-shaped reference; separate API is remote/auth | Reset and stable identity undocumented | EVIDENCE_BLOCKED | No first-party sidecar/path contract | Versioned schema, reset/dedup and Windows path; keep API separate |
| Mimo Code | Passive SQLite with OpenCode-derived filtering | TT README.md:268; src/commands/sync.js:1551-1569; src/lib/rollout.js:5235-5263; first-party [DB source](https://github.com/XiaomiMiMo/MiMo-Code/blob/6babeb0b98f9b4818bddf04a4331edfee04dbf85/packages/cli/src/storage/db.ts#L31-L43) | Product supports Windows; reference root/override differs | Model/provider, input/output/total/reasoning/cache, time/session/cwd/cost | Message parts content-bearing | Windows locator and Claude mirror dedup unresolved | EVIDENCE_BLOCKED | Effective DB root/filter unresolved | Supported path/override and sanitized provider/dedup/cost/content fixture |
| ZCode | Passive SQLite with dual ledgers | TT src/lib/rollout.js:5265-5305,5503-5512; src/lib/install-resolver.js:52-77 | Native Windows path/product | Model/provider, input/output/reasoning/cache, time/request/session/directory; estimated cost | Legacy rows content; usage rows scalar | Unversioned ledgers, custom providers/subagents; fork claim reference-only | EVIDENCE_BLOCKED | No first-party ledger/dedup contract | Pinned fixtures for both ledgers, routing/subagents and content exclusion |
| Qoder | Passive SQLite token_info/model/time/project query | TT src/lib/rollout.js:5786-5834,5868-5885,5912-5935; first-party [Windows install](https://docs.qoder.com/qoder/install-windows) confirms product, not schema | Native reference path | Model, prompt/cache/completion, IDs/time/project; no reasoning/native cost | Selected query omits content | Private schema/cache unverified; Qoder CN distinct | EVIDENCE_BLOCKED | Contract unresolved; regional targets must not collapse | Separate versioned schemas for international/CN, cache and dedup |
| AnythingLLM Desktop | Passive SQLite | TT src/lib/rollout.js:13518-13613; first-party [schema](https://github.com/Mintplex-Labs/anything-llm/blob/a72104c5348cea06dd2dbec284cff30bed859608/server/prisma/schema.prisma), [usage fix](https://github.com/Mintplex-Labs/anything-llm/pull/6150) | Native path/fixture and Windows install | Model, input/output/total, time,row/chat; no provider/cache/project; reasoning is derived | Rows contain content; query projects scalars | Shipped cumulative fix needs qualification; residual is not direct reasoning | DEFERRED_AFTER_M3 | Plausible Windows scalar source; later breadth | Named release and sanitized per-message/update/dedup fixture |
| Claude Science | Passive SQLite frames | TT src/lib/rollout.js:6547-6581,6666-6721; first-party [overview](https://claude.com/docs/claude-science/overview) lists Windows 11, not native DB schema | Reference requires WSL/UNC; native path unknown | Frame model/source, input/output/cache/aux,time,parent IDs; no project/direct cost | DB contains content; selected query metadata-only | Cache-inclusive input and helper accounting/model skew | EVIDENCE_BLOCKED | Product Windows support does not prove reader path/schema | Supported Windows schema/path plus cache/aux/parent-dedup fixture |
| DeepSeek Harness | Passive JSONL/zstd | TT src/lib/rollout.js:22314-22348; first-party [persistence](https://github.com/deepseek-ai/deepseek-harness/blob/master/packages/session/session-persistence-jsonl/README.md#L237-L251), [session contract](https://github.com/deepseek-ai/deepseek-harness/blob/master/docs/subsystems/session.md#L801-L865) | Reference native/WSL probes; official Windows root unclear | Model/time/sequence/session/project key/usage; provider/cost unclear | Session content scanned then projected | Failed assistant-attempt usage omitted | EVIDENCE_BLOCKED | Attempt-vs-commit semantics and Windows path unresolved | Explicit accounting contract, retry fixture and supported Windows path |
| TRAE Work CN | Remote internal, opt-in API | TT src/lib/rollout.js:21831-21915; first-party [Work overview](https://docs.trae.cn/work_what-is-trae-work); enterprise [API](https://docs.trae.cn/enterprise_get-model-usage-for-users) is separate | Web/desktop/mobile; no passive Windows store | Reference per-session corrections; personal endpoint fields/auth not public | Remote request/auth | Enterprise API does not establish personal endpoint scope/stability | EVIDENCE_BLOCKED | Auth, privacy scope and endpoint unresolved | Versioned personal API/permission contract and correction/dedup fixture |
| LM Studio | Passive local server logs | TT src/lib/rollout.js:12585-12615, 12843-13016; first-party [log docs](https://lmstudio.ai/docs/cli/serve/log-stream) say logs may contain exact text | Native Windows log path | Final response/model/time,input/output/total, optional cache/reasoning; no project/conversation/direct billed cost | Content-bearing logs require projection | Local/API/cloud routes differ in billing; only final usage responses captured | DEFERRED_AFTER_M3 | Plausible passive Windows source; later breadth | Sanitized formats, rotation/reload dedup and route-cost fixture |
| Unsloth Studio | Passive SQLite | TT src/lib/rollout.js:13208-13414; first-party [usage table](https://github.com/unslothai/unsloth/blob/124e89a64d681e5428e95039f1909487a9719cb9/studio/backend/storage/studio_db.py#L393-L415), [roots](https://github.com/unslothai/unsloth/blob/124e89a64d681e5428e95039f1909487a9719cb9/studio/backend/utils/paths/storage_roots.py#L230-L323) | Windows install; DB path varies by install/account | Model/provider,time/IDs,input/output/total/cache/reasoning; cost/project unclear | DB contains content/attachments/metadata/subject; reference omits these | contextUsage and account path not matched to reader contract | EVIDENCE_BLOCKED | Windows DB/counter contract unresolved | Named-release path/schema and sanitized install/account/contextUsage/privacy fixture |
| Devin CLI | Passive SQLite session DB | TT docs/devin-usage.md:1-9 | Reference Windows route is WSL; native data directory unknown | Request ID/model/time/token/workdir; replay/fork copies dedup; CLI only | Private local history; scalar projection possible | WSL-only path; CLI differs from cloud/Desktop | DEFERRED_AFTER_M3 | WSL breadth not required after native OpenCode baseline; quota separate | Native Windows source or versioned WSL/dedup fixture |
| Cline | Passive CLI v3/desktop session files | TT src/lib/rollout.js:11698-11740; first-party [message contract](https://github.com/cline/cline/blob/main/sdk/packages/core/docs/messages-contract-v1.md#L207-L240) | Native CLI/desktop path; extension unknown | Model/provider,input/output/cache/cost,time/session/message; optional reasoning; no project | Files include prompts/replies/tools/system prompt | Target surface ambiguous; reference omits extension globalStorage | EVIDENCE_BLOCKED | Surface and extension source unresolved | Select surface; versioned extension path/schema or pin CLI turn/dedup |
| MiniMax Code | Passive session JSONL | TT src/lib/rollout.js:16405-16434; first-party [data docs](https://github.com/MiniMax-AI/minimax-code/blob/main/docs/installation.md#L232-L242) establish root, not session schema | Windows CLI/override documented; exact JSONL path unconfirmed | Model,input/output/cache,time/message ID; no project; zero reference cost ignored | JSONL content-bearing | Current-release schema/token meaning not tied to reference | EVIDENCE_BLOCKED | Version/schema mapping unresolved | Current path/schema plus token/ID/Windows/content-free fixtures |
| Command Code | Passive append-only JSONL | TT src/lib/rollout.js:23557-23589,23706-23735; first-party [sessions](https://commandcode.ai/docs/sessions), [Windows](https://commandcode.ai/docs/windows#on-native-windows) | Native Windows; WSL optional | Model/effort,input/output/cache,time,record/session,cwd and display cost; no reasoning | Transcript includes prompts/replies/tools | Cache-inclusive input and copied forks require dedup | DEFERRED_AFTER_M3 | First-party format/platform support plausible later breadth | Version-pinned cache/fork/resume identity and content-free fixture |

#### Shared-family limits

Shared modules, roots or parser envelopes do not establish interchangeable semantics. Claude Code JSONL differs from Claude Science SQLite; Pi, OmO and oh-my-pi have distinct roots/reasoning rules; Dots is not tied to the reference Pi provider; Kilo CLI parser reuse does not prove its schema. Cursor uses local auth for remote acquisition; Copilot sources overlap; Qoder remains distinct from Qoder CN.

Every target has its own row. These are activity dispositions only: they do not alter quota states, infer quota from activity or claim Cerith support for deferred/blocked targets.
The final M3.B tally is 1 IMPLEMENTED_ACCEPTED, 27 EVIDENCE_BLOCKED and 14 DEFERRED_AFTER_M3. OpenCode is the only accepted activity implementation; the other 41 remain explicit evidence-backed research/coverage dispositions.

### Usage limits: 17-provider reference catalog

The current TokenTracker limits registry covers:

1. Claude
2. Codex
3. Cursor
4. Gemini CLI / Code Assist
5. Kimi
6. Kiro
7. Grok Build
8. GitHub Copilot
9. Antigravity
10. ZCode
11. OpenCode Go
12. Command Code
13. Qoder
14. Qoder CN
15. Ark Coding Plan
16. Ark Agent Plan
17. Devin

Codex is already implemented in Cerith. The remaining providers are targets for evidence-driven expansion, not permission to import TokenTracker's labels or normalization unchanged.

Notable acquisition distinctions in the pinned reference:

| Usage-limit integration | Reference acquisition note |
| --- | --- |
| OpenCode Go | API key + official usage API is the preferred route at this pin; dashboard scraping is a compatibility fallback and local-cost estimation is explicit opt-in, not equivalent quota truth. |
| Gemini CLI / Code Assist | OAuth-backed Code Assist quota; do not relabel this as generic Gemini consumer/API quota. |
| Grok Build | Billing/credit-period semantics; keep included quota, on-demand/prepaid balance and ordinary activity separate. |
| Qoder / Qoder CN | Separate regional/product paths and auth; treat them as distinct integrations. |
| Ark Coding Plan / Agent Plan | Separate plan surfaces and units; do not merge because branding is shared. |

## Acquisition principles

Prefer, in order appropriate to the specific tool:

- passive structured local files/databases;
- provider-supported structured events/hooks;
- existing official local sessions/credential stores;
- official CLI/RPC output;
- documented or first-party-observed provider endpoints;
- one-time explicit setup when no safe automatic route exists.

There is no universal acquisition method. The fact that 42 tools can be presented in one dashboard is evidence for a common product model, not evidence for one common parser/auth framework.

For M3.B, prefer existing passive/local activity over generating model requests. Do not change credentials or create activity solely to validate a collector.

### Zero-config target

A normal first run should detect supported tools and useful existing state automatically.

A hook may be installed automatically only when its behavior is narrow, reversible, attributable to Cerith and does not overwrite unmanaged user configuration. Passive readers are preferred when they provide equivalent freshness.

If quota requires explicit authorization, setup happens once. Successful configuration persists. Repeated provider setup on ordinary Cerith startup is a defect.

## Privacy boundary

Collect only the minimum structured telemetry required for the feature.

Allowed when sourced/verified:

- timestamps;
- model/provider/tool identity;
- scalar token counters;
- cache/reasoning counters when semantics are known;
- bounded local session/activity identity;
- project attribution that does not require storing private content;
- quota/reset/credit fields under the quota contract.

Do not retain prompts, completions, source code, attachments, raw credentials or arbitrary transcript bodies.

A parser may need to open a structured record containing private content to reach usage metadata; if so, it must extract only the required scalar metadata and discard content immediately. Prefer sources that avoid reading content entirely.

## Token and cost semantics

Do not assume providers agree on token fields.

At minimum verify:

- whether input includes cached input;
- whether reasoning is included in output or separate;
- whether counters are per-request, snapshots or cumulative;
- whether mirrored/imported sessions duplicate another tool's activity;
- whether model identifiers map to a published price.

Derived cost must retain pricing provenance and must not become provider-reported billing. Unknown pricing yields unknown/partial cost, not fabricated zero-cost meaning.

### First bounded activity slice — OpenCode

CER-M3-005 implements one OpenCode local-activity source. The collector reads the standard `opencode.db` under the XDG data root (`XDG_DATA_HOME/opencode/opencode.db`, or `~/.local/share/opencode/opencode.db`) or an explicit `OPENCODE_DB` override (absolute as given, relative under the OpenCode data directory). OpenCode can use channel-specific database names; this slice does not guess or scan them. If the service cannot resolve the active database, it reports unavailable.

The source evidence is pinned to OpenCode v1.17.9: first-party [assistant message schema](https://github.com/anomalyco/opencode/blob/v1.17.9/packages/core/src/session/message.ts), [SQLite schema](https://github.com/anomalyco/opencode/blob/v1.17.9/packages/core/src/session/sql.ts), [message updater](https://github.com/anomalyco/opencode/blob/v1.17.9/packages/core/src/session/message-updater.ts), [session projector](https://github.com/anomalyco/opencode/blob/v1.17.9/packages/core/src/session/projector.ts), [session creation](https://github.com/anomalyco/opencode/blob/v1.17.9/packages/core/src/session.ts), [database path selection](https://github.com/anomalyco/opencode/blob/v1.17.9/packages/core/src/database/database.ts) and [XDG data path](https://github.com/anomalyco/opencode/blob/v1.17.9/packages/core/src/global.ts) define the fields and persistence behavior. Session creation assigns `InstallationVersion` to `Session.version`; the projector stores it in `session.version`. Each `session_message.session_id` references that parent session, and the projector writes assistant updates to the message row associated with the same session. Cerith joins the selected assistant row to that parent within its read transaction and reads only the version scalar for qualification; it does not expose the session ID or version as telemetry. Only `1.17.9` is supported. A known other version yields valueless `Unavailable/source_version_unsupported`, because its internal semantics are outside the validated contract. A missing parent/version or unusable version yields valueless `Error/source_version_missing` or `Error/source_version_invalid`, because it violates the persisted source contract. Version qualification happens before the guarded message-data projection; there is no fallback to an older assistant. TokenTracker's [pinned v1.1.3 README](https://github.com/xiufengsun/TokenTracker/blob/a8520617ece3f15a07c342e2c7909eb311234522/README.md) corroborates OpenCode SQLite as an existing passive-reader pattern, not as Cerith's authority. This is an internal application schema, not a stable public telemetry contract; revalidate it when the host version or schema changes. The selected source is local/read-only and requires no network, credentials, model request or application-state write. Its JSON record also contains text, reasoning and tool payloads, so the native reader returns only fixed, type-checked scalar projections and never materializes or publishes the raw JSON.

The collector considers only the newest 64 message metadata rows ordered by `time_created DESC, rowid DESC`, then uses the newest-created assistant in that window. It does not fall back to an older assistant if the selected record is incomplete, failed or invalid. A valid record requires explicit nonnegative integral values for all five fields OpenCode reports: `tokens.input`, `tokens.output`, `tokens.reasoning`, `tokens.cache.read` and `tokens.cache.write`. Those are source-reported categories; Cerith does not infer that they are disjoint or additive. Model ID, model provider ID and `time.completed` are also direct source fields. V2 does not define `tokens.total`; no total, cost, session/project identity or source row ID is published.

The eight source-qualified MetricIds are listed in `METRICS.md`. All snapshots from one poll share `ObservedAt` and state. `occurred_at` preserves the source completion timestamp; `Fresh` means the read succeeded, not that the activity is recent. Polling repeats the latest snapshot and is not a delta or event history. MetricBus remains per-ID and cannot publish a tuple atomically, so a consumer must group by `ObservedAt` and accept only complete groups. No values are cached across unavailable/error results.

The reader reopens the database on each serial five-second poll, examines at most 64 metadata rows and one selected JSON record, limits one SQLite value/row to 64 KiB, uses a 100 ms busy timeout and a 250 ms SQL progress deadline with cancellation interruption. This bounds the query work and selected record size; it is not a hard wall-clock guarantee for native open or an individual JSON parse. Missing source/window or incomplete activity produces valueless `Unavailable`; malformed data/schema/read produces valueless `Error` with a sanitized code. A sanitized host qualification probe on 2026-10-04 selected no assistant in the bounded window, so there was no parent `session.version` to compare; host version qualification is unverified on this run. The production collector remains `LIVE_NOT_AVAILABLE_ON_THIS_HOST / activity_absent`; no message content was read.

## Product surfaces and analytics ownership

This document owns acquisition semantics, not duplicate UI requirements.

- Dashboard/history product scope is canonical in `ROADMAP.md` (M7) and `PRODUCT_SPEC.md`.
- Compact floating/taskbar/tray/detail behavior is canonical in `UI_UX.md` and `ROADMAP.md` (M4).
- This acquisition layer only guarantees truthful normalized MetricIds and safe attribution inputs for those later surfaces.

TokenTracker, Gengchou and AI Limits remain the reference products recorded in `REFERENCE_PROJECTS.md`; their UI/runtime stacks are not imported.

## Parallel implementation boundary

Provider-specific acquisition should be physically isolated where practical, conceptually:

```text
src/Cerith.TelemetryService/Providers/AI/<Provider>/
tests/Cerith.Telemetry.Tests/Providers/AI/<Provider>/
tests/Cerith.Telemetry.Tests/Fixtures/<Provider>/
```

Do not create empty folders merely to match this sketch. Create a provider path only when work begins.

One write owner per provider boundary. Shared files, dependencies, auth infrastructure and registration are owned by a single integration task. A provider worker that discovers a shared requirement stops and escalates instead of editing another owner's path.

## Reference lessons, not architecture inheritance

Use:

- TokenTracker for acquisition breadth, zero-config patterns and analytics ideas;
- Gengchou for compact Windows surfaces and shell resilience;
- AI Limits for adaptive compact layout and lightweight rendering discipline.

Do not inherit:

- provider-specific UI coupling;
- a web/Node runtime as Cerith's native runtime;
- cloud sync/leaderboards/skills/pets as accidental requirements;
- GPL implementation code without an explicit license decision;
- external normalization assumptions without verification.

## Milestone ownership

The canonical milestone split lives in `ROADMAP.md`: M3 owns acquisition/setup, M4 owns generic compact surfaces, and M7 owns persistence/analytics. Keep this document focused on what collectors must acquire and normalize.
