# Tasks

## CER-M4-006 — Composable mixed-domain WidgetHost surface

Status: DONE at bounded task scope — verdict `COMPOSABLE_METRIC_SURFACE_VERIFIED`; final documentation-head review/CI and integration receipts belong to PR #58/Git history.
Owner: Codex — sole repository writer (Track A); Tracks B/C are read-only.
Branch: `codex/CER-M4-006-composable-surface`.
Canonical starting main: `aafc90425b5c17e6514989c4b7f4ddd0f577c6f5`, fetched with clean local main equal to origin/main.
Scope: one local subscription per distinct MetricId feeding multiple native components, truthful MetricBar with explicit presentation range, bounded Hardware/Gaming/AI composition, deterministic reconnect regressions and scale/performance characterization. Preserve D-025, exact per-ID pipes and bounded backpressure. No providers, dependencies, projects, history, persistence, editor, taskbar/tray or OSD. One coherent PR; autonomous protected integration is authorized after all required gates. M4 remains ACTIVE / INCOMPLETE.
Routing: Track A native Codex (requested GPT-6.1 Sol Medium; root runtime model/effort is not independently exposed); Track B GPT-6.1 Sol Medium native. Track C uses GPT-6.1 Sol Medium because the subagent interface does not expose the preferred GPT-6 Luna Max. Initial Track B dispatch failed at capacity before producing a review; same-model retry delivered architecture PASS and final technical PASS after a non-owner cancellation correction.
Implementation: `MetricSubscription.ForDistinctMetrics` deduplicates requested exact IDs. The window owns one loop/client per ID and shared availability-attempt coordination; local event consumers receive committed descriptor/snapshot state on the WinUI context. CPU/quota each use Readout + Bar; RAM/FPS use Readout. Range is explicit 0–100 presentation configuration; valueless/Unlimited states suppress track, out-of-range values retain actual text with an explicit range warning and no fill. No MetricBus product change.
Validation: Release solution build 0 warnings/errors; Core/Telemetry/Integration 39/397/67 (503/503, zero skipped); focused subscription/presentation 48/48. Independent technical review PASS. Actual-service no-window probe observed one accepted pipe per four IDs despite six consumers and one reconnect per ID; service-absent/restart recovery 914.8/995.0 ms, EOF clearing 19.7 ms; real OpenCode quiet hold 3.5 s without churn. These are not WinUI native acceptance receipts.
Graph/hygiene: Graphify AST re-extracted nine changed/new C# files (3234 nodes, 9610 edges, 185 communities); semantic docs and community labels were not regenerated. The extractor reported its namespace-ID collision/dedup warning; individual changed source-symbol nodes are present. No LLM API calls or monetary expense. Diff and repository filename/privacy checks are required before each candidate commit.
Scale: Track C KEEP_CURRENT_BUS; 10–500 subscriptions characterized, with exact routing and lossless bounded Wait unchanged. Canonical methodology/limitations are in `docs/PERFORMANCE.md`. F3 characterized with its scaling/backpressure limits retained; F4 dedicated deterministic regression gap addressed. F5/F7 remain before M4 exit and F6/F8 remain public-release debt.
Native gate: actual WinUI startup/absent/restart/close and both shared representations passed on the available primary screen. Dark/light default/minimum passed at 96 DPI, including full bottom-range containment within native client and scroll viewport. A real minimum-size clipping defect was fixed by removing duplicated client Grid minima while preserving outer presenter constraints. Independent source/visual review PASS; higher physical DPI remains F5 work. Release rebuild 0 warnings/errors and Integration 67/67 after that XAML-only correction. Track C reviewed 34 applicable bounded runs: characterization complete/no blocking implementation-specific defect established; expanded-surface CPU/memory increases remain measured, CPU nonregression/budgets not proven and F7 OPEN. Default-size performance receipts precede the minimum-only correction, whose removed constraints were inactive at that geometry. Earlier intended-minimized samples are excluded from verified minimized evidence. Canonical results/limits are in docs/PERFORMANCE.md. Source-head 6cbf21afef8586a9de3b353359a3180d84461666 independent review PASS; Build and Test 37697605476 attempt 2 SUCCESS after the existing Copilot child-output timing failure on attempt 1. Repository Quality 37697605475 SUCCESS; CodeQL 37697605502 SKIPPED under D-013, not security PASS. Final documentation-delta review/checks and integration are recorded in PR #58/Git history.
Overnight continuation updated on 2026-10-08: complete this task, integrate with normal head-pinned protections, then resolve actual M4 gaps. M5/M6 are authorized only after the previous milestone has passed independent exit review, integrated and been documented closed. M7 remains excluded. Existing Codex/ChatGPT and Antigravity quota are authorized with zero additional monetary cost; no dangerous/destructive operations, security weakening or shared-history rewrites. One writer and one existing same-chat continuation; preserve evidence and checkpoint before quota exhaustion. Routine unchanged-state notifications remain quiet.

## CER-M4-005 — Establish the MetricId binding contract

Status: DONE at bounded task scope — verdict `METRIC_BINDING_CONTRACT_VERIFIED`; read-only GPT-6.1 Sol Medium final diff review and Advisor product/architecture review returned `PASS`.
Owner: Codex — sole repository write owner; GPT-6.1 Sol Medium audit is read-only.
Branch: `codex/CER-M4-005-metric-binding`
Historical task base: `b9449ae7358d09acdbc8dfaffa24c114370d33e0`.
Reviewed candidate head: `4cd329d1139a1c3b2d620c87af92323e8696ad39`.
Scope: establish whether semantic descriptor metadata can be bound to provider-independent WidgetHost components without a second registry or premature discovery framework. Preserve truthful metric semantics, independent exact-MetricId-per-pipe connections and CER-M4-004 reconnect behavior. No new acquisition, M5 editor/configuration, history, taskbar/tray, OSD, dependencies, or project.
Decision: protocol v1 keeps its exact-subscription/no-replay guarantee. `accepted` includes a registered descriptor; late registration is attached once to the first later snapshot, including valueless states. Missing metadata does not block or reject the subscription. Presentation format stays local; display name, unit and value kind come from the shared registry. After acceptance, silence does not imply transport failure; pipe closure/cancellation does.
Audit: initial overall verdict `FIXES_REQUIRED`; F1/F2 blocked M4-005 and are addressed by this candidate. F3/F4/F5/F7 remain before M4 exit; F6/F8 remain before public release; F9 is reconciled here; F10 remains historical context.
Local validation: Release solution build 0 warnings/errors; Core/Telemetry/Integration 39/397/32 (468/468, zero skipped); focused MetricPresentation 15/15, Named Pipe 17/17 and OpenCode descriptor path 1/1. New regressions cover described → descriptor-free → late-described reconnect, wrong-ID and repeated descriptors, and a quiet connection held for 3.5 seconds through cancellation and EOF. DISPLAY2-only native checks covered healthy startup, service-absent startup/recovery (2.19s), stop/restart (2.18s), truthful CPU/game/Codex state and clean close without foreground focus. Graphify re-extracted six changed source files (3155 nodes, 9461 edges, 179 communities); community labels were not regenerated.
Review: first pass `FIXES_REQUIRED` identified stale descriptor binding and missing lifecycle coverage; corrected and re-reviewed as `PASS` with no architecture disagreement. The actual WinUI binding reset was source-reviewed; integration regressions cover transport and presentation state, not automated WinUI control interaction.
Exact-head hosted receipt for reviewed candidate `4cd329d1139a1c3b2d620c87af92323e8696ad39`: Build and Test SUCCESS (run 37680696512, attempt 2); Repository Quality SUCCESS (run 37680696546); CodeQL SKIPPED (run 37680696518), not PASS. Earlier checkpoint receipts are retained in WORKLOG. Git history and PR #57 remain authoritative for the integration receipt and checks on subsequent documentation corrections.
Acceptance: implementation, verification and review are complete at bounded task scope; D-025 records the accepted contract. M4 remains ACTIVE / INCOMPLETE; M0–M3 are DONE; M5/M6/M7, taskbar/tray and gaming OSD remain NOT STARTED; performance budgets remain unchanged. Await Advisor-controlled integration/next-task decision. Do not begin CER-M4-006.

## CER-M4-004 — Bound disconnected WidgetHost reconnect CPU

Status: DONE — `PERFORMANCE_RECONNECT_FIX_VERIFIED`; bounded single-flight reconnect coordination adopted after a corrected exact-base comparison.
Owner: Codex — sole repository write owner; no parallel writer.
Branch: `codex/CER-M4-004-widgethost-reconnect`
Base: `fe619e9581aca038a16abb3a6087f129225dce3b` (`main == origin/main`, clean tree after fetch).
Scope: reduce CPU while TelemetryService is absent only if paired evidence shows a worthwhile improvement within the recovery envelope. Preserve independent MetricId-per-pipe connections, truthful unavailable/recovery semantics, cancellation and lifecycle. No protocol/topology redesign, new project/dependency, M5, M7, OSD, taskbar/tray or performance-budget revision. Benchmark artifacts remain outside Git; visible Cerith windows are DISPLAY2-only.
Evidence: ten distinct-base/candidate Release pairs reduced disconnected CPU median 54.71% (3.434% to 1.555% of one core), with 10/10 paired wins. Ten service restart cycles restored all three rows in 0.985–1.054s; connected visible/minimized checks found no material regression. Exact measurements and lifecycle evidence are canonical in `docs/PERFORMANCE.md`.
Validation: Release solution build PASS (0 warnings/errors); Core/Telemetry/Integration 39/397/22 (458/458, zero skipped); focused Named Pipe tests 10/10; read-only concurrency review PASS after the clean-EOF fix. Reviewed candidate head `26c1e8da8f7c96e776a2768f071b4965cd70ece4` passed Build and Test and Repository Quality; CodeQL was SKIPPED (not PASS).

## CER-M4-003 — Isolate and correct WidgetHost CPU regression

Status: DONE at bounded isolation scope — verdict `PERFORMANCE_CAUSE_ISOLATED_FIX_DEFERRED`; independent Advisor review accepted the evidence as cost-category isolation, not an exact profiler-stack root cause. No product fix was adopted. M4 remains ACTIVE / INCOMPLETE; M3 remains DONE; M7 remains NOT STARTED.
Owner: Codex — sole repository write owner; no parallel writer.
Branch: `codex/CER-M4-003-widgethost-cpu`
Base: `0c6c68447cefbb12978f681f6d393024189b5025` (`main == origin/main`, clean tree after fetch before claim).
Scope: disposable Release isolation of connected snapshot, presentation, UI and disconnected retry costs; minimal evidence-backed correction only if warranted. No M5, M7, tray/taskbar, OSD, new framework/dependency/project or budget revision.
Result: DISPLAY2 five-run stages isolated a 1.038% receive/presentation floor, 1.946% UI-without-Automation-name and 2.206% full-path median. The equality guard reduced 408 presentations to 112 UI applications; CPU changed on nearly every snapshot. Disconnected one/three-subscription medians were 1.425%/3.484%, with pending `ConnectAsync` time tracking CPU. Selective UI mutation did not improve consistently, while longer retries reduce CPU at the cost of possible recovery delay. No product correction met the Phase B gate. Full matrix, counts, limitations and native lifecycle evidence: `docs/PERFORMANCE.md`.
Validation: canonical Release build 0 warnings/errors; Core 39/39, Telemetry 397/397, Integration 22/22 (458/458, zero skipped); focused MetricPresentation 12/12; diff/private-data/filename checks passed. No product file, runtime dependency, project or new abstraction. Hosted CI disposition belongs to the exact PR head and must be checked there.

## CER-M4-002 — Attribute the WidgetHost native performance baseline

Status: DONE at bounded attribution scope. Verdict: `PERFORMANCE_ACTION_REQUIRED`; M4 remains ACTIVE / INCOMPLETE.
Owner: Codex — sole repository write owner for CER-M4-002; no parallel writer.
Branch: `codex/CER-M4-002-performance-baseline`
Base: `1f18fd3771193d6aea4f9cff0ebe6684e5fb7006` (`main == origin/main`, clean tree after fetch before claim).
Scope: controlled local attribution of the native WidgetHost process cost using a disposable minimal WinUI baseline, current WidgetHost with service absent/live/minimized, and the immediate pre-M4 WidgetHost control. Evidence and workflow documentation only; no product optimization, new project/dependency in the repo, M5, M7, tray/taskbar or gaming OSD work.
Acceptance: repeated process-specific CPU-time deltas plus Working Set/Private Bytes, host/runtime facts, framework-floor and incremental-cost estimates, explicit uncertainty and a decision gate.
Result: bare WinUI visible median 0.000% CPU / 108.20 MiB WS / 132.35 MiB private; current static M4-001 UI 0.000% / 115.81 / 134.31; current service-absent 1.946% / 119.13 / 135.70; current live visible pooled 1.102% / 125.89 / 137.89; live minimized 0.908% / 125.96 / 138.04. The isolated pre-M4 `fc2f85e...` WidgetHost against the same live service measured 0.454% / 128.53 / 142.40 across eight pooled runs. A second three-run comparison repeated the post-M4 > pre-M4 CPU ordering. Memory is mostly framework floor and M4-001 improves it modestly versus pre-M4; current connected/reconnect CPU requires the next bounded M4 performance task. Canonical protocol, ranges and limitations are in `docs/PERFORMANCE.md`.

## CER-M4-001 — First generic native MetricId surface

Status: DONE — CER-M4-001 bounded slice complete; M4 remains ACTIVE / INCOMPLETE.
Owner: Codex — sole repository write owner for CER-M4-001; independent visual reviewer is read-only.
Branch: `codex/CER-M4-001-generic-metric-surface`
Base: `fc2f85eaad588d0fcf807c5836ed184b2a051e90` (`main == origin/main`, clean tree before claim).
Scope: replace the WidgetHost diagnostic presentation with the smallest reusable native MetricId-driven metric surface using existing Named Pipe / MetricSnapshot contracts. Support truthful state/confidence semantics, dark/light themes, native lifecycle and representative hardware/gaming/AI metrics. No M5 editor/config schema, M7/history, gaming OSD, taskbar/tray integration, IPC redesign, new project or runtime dependency.
Acceptance: Release build/full tests/focused presentation tests, native dark/light/live/reconnect validation, measured idle CPU/working set, bounded GPT-5.6 Sol High visual review via CodexGPT → ChatGPT Web, hostile engineering review, one bounded PR candidate with exact-head hosted Build and Test / Repository Quality / CodeQL disposition.
Local candidate receipt: Release build PASS with 0 warnings/errors; full suite 458/458 PASS, zero failed/skipped; focused MetricPresentation 12/12 PASS. Native dark/light captures render coherently; live CPU reports Fresh/Measured, no-target gaming reports Stopped/Unknown, and this host reports Codex quota Error/route_unsupported/Unknown without coercing either case to zero. Service interruption changes all rows to Unavailable/No snapshot and automatic reconnect restores live state; normal window close exits cleanly. At 96 DPI the intended window is 420x340 with an effective 360x340 minimum and all three rows visible. Both attached displays report 96 DPI, so a physical higher-DPI move is not claimed. Final measured observation: visible 1.172% of one core / 127.25 MiB working set; minimized 0.781% / 127.66 MiB.
Review receipt: bounded GPT-5.6 Sol High visual review via CodexGPT -> ChatGPT Web returned PASS against the native dark/light/non-Fresh evidence and the Design Lab direction. Its optional note about the technical `MetricId` header label was addressed in the final polish pass. Hostile owner engineering review found no provider coupling, new IPC contract, repaint loop, M5/M7 leakage, runtime dependency, or lifecycle blocker. A GPT-6.1 Sol Medium read-only review attempt returned the runtime usage-limit error before producing a verdict; no PASS is attributed to it.
Candidate receipt: implementation commit `87d447cd30665b2c272acc4a2a104871ab63aa16` passed Build and Test (run 37467483707) and Repository Quality (run 37467484170); CodeQL was SKIPPED (run 37467483710), not a security PASS. A later documentation receipt commit recorded the exact candidate CI; the final correction candidate and its hosted checks are recorded in PR #53 history.
Performance concern: the WidgetHost observation is above the aspirational memory targets, and the CPU sample does not establish the approximately-zero idle target. `docs/PERFORMANCE.md` records the values, unknown baseline attribution and a bounded investigation required before M4 exit.

## CER-M3-006 — M3.B catalog disposition audit and M3 exit candidate

Status: DONE — PR #51 was squash-merged from reviewed head `c9c34a9cfc1de0fabf13b42625b8057df281c217` as `6c262aaa7ae75ecb46c3e45f76a69b7304df4128`.
Owner: Codex — sole repository write owner; read-only specialists may research disjoint target groups, followed by one fresh read-only GPT-6.1 Sol Medium reviewer.
Branch: `codex/CER-M3-006-m3b-catalog-disposition`
Base: PR #51 base (pre-merge): `8dfd5f56aa63355bd0d952d774a5d84af750afa2`; canonical `main` / `origin/main` after merge: `6c262aaa7ae75ecb46c3e45f76a69b7304df4128`.
Scope: research and documentation only; no product/dependency changes, additional collector, or M4/M7 work.
Acceptance assessment: 42/42 M3.B targets accounted for in docs/AI_TELEMETRY.md: 1 IMPLEMENTED_ACCEPTED, 27 EVIDENCE_BLOCKED, 14 DEFERRED_AFTER_M3. M3.A remains 11 accepted quota integrations; existing quota blocked/deferred states are unchanged and separate.
Coexistence evidence: HardwarePresentMonCodexCursorAndAntigravityPublishIndependentlyOnTheSameBus (4/4) exercises unavailable OpenCode activity beside CPU/RAM, gaming, Codex, Cursor and Antigravity; Cursor includes success, missing, auth-failed and a pending synthetic request timeout. Provider-failure isolation includes opencode-activity (12/12). These are synthetic/local receipts, not live activity.
Exit decision: every literal ROADMAP gate is satisfied; M3 DONE is canonical. OpenCode is the sole accepted M3.B implementation. M4 is NEXT / NOT STARTED; M7 is NOT STARTED. The fresh GPT-6.1 Sol Medium review returned PASS on reviewed head `c9c34a9cfc1de0fabf13b42625b8057df281c217`. PR #51 is MERGED via squash `6c262aaa7ae75ecb46c3e45f76a69b7304df4128`; Build and Test and Repository Quality succeeded on that exact head; CodeQL was SKIPPED, not PASS.

## CER-M3-005 — First passive/local AI activity vertical slice

Status: DONE via PR #50, squash-merged from reviewed head c2a8594107f1a3b68071f6c2244c144ba7ded409 as 8dfd5f56aa63355bd0d952d774a5d84af750afa2. CER-M3-006 records the separate M3 exit audit.
Owner: Codex — sole repository write owner for this task.
Branch: `codex/CER-M3-005-m3b-activity`
Base: `2441568f0899affdbc6e43d8359a6496c4c4dde7` (canonical `main` / `origin/main`).
Initial implementation commit: `0d8b67b9cca104ca1b993f335bd04e88fd55d70a`. Version-qualification and coexistence correction commit: `c3c9dc313114e1d64a0e8062093855d22a461c8f`. PR #50 and Git history are authoritative for the exact reviewed head, hosted checks and merge result: https://github.com/ParedesYTecho/Cerith/pull/50.
Scope: compare 3–5 realistic host-available passive/local AI activity sources, choose one evidence-backed collector, then route its directly observed fields through MetricSnapshot, MetricBus, existing IPC and a MetricId consumer. One collector only; no M4 UI, M7 history, quota inference, artificial model requests, credential mutation, or speculative shared framework.
Review boundary: an earlier GPT-6.1 Sol Medium native-Codex read-only design review returned PASS after six design finding groups were resolved. That was a design gate, not the implementation review. The owner validation below is not an independent implementation-review or exact-head hosted-check receipt; PR #50 and Git history are authoritative for those results. CodeQL SKIPPED is not a security PASS.
Privacy: never retain or emit prompts, completions, source code, credentials, or raw provider records; use synthetic private-safe fixtures and inspect no real content unless schema evidence makes minimal scalar confirmation necessary.

Candidate decision: OpenCode CLI 1.17.9 is selected. Its first-party v1.17.9 source defines the `session_message` SQLite table, V2 assistant usage fields, and database path/override behavior; the schema is internal rather than a stable public contract. The host has the standard database and matching schema/index, but the bounded collector probe found no recent assistant row (`activity_absent`); no user row content was read. The collector fails closed on schema/index/query errors and does not search channel-suffixed database names.

| Host candidate | Evidence and limitation | Decision |
| --- | --- | --- |
| OpenCode CLI 1.17.9 | Local SQLite file; pinned first-party schema exposes assistant time, model/provider IDs and five token categories. Database `data` also contains conversation/tool content, so only guarded scalar JSON1 projections are used. No auth, network, inference or write. | Selected; best evidence-to-risk ratio. |
| Codex CLI 0.160.0 | Official local rollout JSONL source exists, but host schema is not pinned to the installed version; corpus is large and content-bearing, with replay/resume/fork lifecycle complexity. | Rejected for this first slice. |
| Claude Code 2.1.185 | Host session records exist, but a stable versioned passive usage schema was not established. `claude -p` would trigger inference. | Rejected for this first slice. |
| Gemini CLI 0.59.0 | First-party docs describe transcript-bearing local chats and token stats, but no suitable host chat/activity was present. | Rejected; no artificial request to create evidence. |
| Cursor | No host state database was present. | Not host-available. |

Implementation contract: read at most the newest 64 `session_message` metadata rows ordered by `time_created DESC, rowid DESC`, then select the newest-created assistant within that window. Join that selected row's `session_id` to its parent `session` and read only the scalar `session.version`. Accept only `1.17.9`; a known unsupported version produces valueless `Unavailable/source_version_unsupported`, while a missing parent/version or unusable version produces valueless `Error/source_version_missing` or `Error/source_version_invalid`. Qualification precedes the guarded JSON projection. Never fall back to an older assistant when the selected one is unsupported, incomplete, failed or invalid. Publish only explicit source `input`, `output`, `reasoning`, `cache.read`, `cache.write`, model ID, model provider ID and `time.completed`. There is no V2 `total` field in this source schema; cost, session/project identity, version and raw row identifiers are omitted. Five source-reported categories remain independent; no additivity or quota relationship is asserted. Repeated polls are latest-state snapshots, not deltas or event history.

New-file rationale: `OpenCodeActivityReader.cs` owns the native read-only SQLite/path/projection boundary so content-bearing source records stay out of provider lifecycle code. `OpenCodeActivityProvider.cs` owns this provider's descriptors, serial polling, state mapping and MetricBus publication; existing quota providers have different semantics and should not own it. `OpenCodeActivityProviderTests.cs` owns the synthetic SQLite fixture and end-to-end IPC/MetricId acceptance; there was no OpenCode-specific test surface. No generic abstraction or fixture file was added.

The native SQLite boundary reads one file read-only per poll, uses the standard XDG data path or explicit `OPENCODE_DB` override (relative overrides resolve under the OpenCode data directory), has a 64 KiB SQLite value/row limit, 100 ms busy timeout, 250 ms SQL progress deadline every 1,000 VM opcodes, and cancellation via `sqlite3_interrupt`. Polls are serial at 5 seconds; there is no cursor/cache/replay. A successful `Fresh` poll does not mean the activity is recent. All eight snapshots from one read share `ObservedAt` and state, but MetricBus publishes per ID and is not an atomic tuple; consumers must group by `ObservedAt` and accept only a complete tuple.

Local validation: `dotnet build Cerith.sln -c Release` PASS (0 warnings/errors); `dotnet test Cerith.sln -c Release --no-build --verbosity minimal` PASS (446/446, zero skipped); focused `OpenCodeActivityProviderTests` PASS (12/12); the shared-bus coexistence test PASS (4/4 data rows) with the real OpenCode provider plus hardware, PresentMon/gaming, Codex, Cursor and Antigravity; provider-failure isolation PASS (12/12, including `opencode-activity`). The source qualification cases include supported success, unsupported with malformed JSON and no values, absent parent, unusable version, and no fallback to an older supported assistant. `git diff --check` PASS. Repository hygiene scan covered 140 tracked files with zero private-artifact filename matches. Bounded added-diff privacy scan found zero sensitive assignment patterns and zero personal machine paths. No runtime/package dependency was added; the only project-file change links the existing `NamedPipeMetricClient` source for IPC acceptance. Graphify code AST update PASS (six code files re-extracted; 3,092 nodes / 9,912 edges; no LLM processing). Its read-only diagnostic reported 545 dangling-endpoint edges, 2 self-loops, and 74 directed / 160 undirected same-endpoint collapsed edges, with the same warning counts in the immediate pre-update backup. The bounded host probe selected no eligible assistant, so `session.version` was not compared; host version qualification remains unverified and live activity remains `LIVE_NOT_AVAILABLE_ON_THIS_HOST / activity_absent`.

Historical receipts for earlier heads: implementation head `0d8b67b9cca104ca1b993f335bd04e88fd55d70a` — Build and Test PASS (run 37159116636); Repository Quality PASS (run 37159116603); CodeQL SKIPPED (run 37159116613). Earlier docs head `edbea38a92c91c21ed8edb3a131f455d8401288a` — Build and Test PASS (run 37159384180); Repository Quality PASS (run 37159384157); CodeQL SKIPPED (run 37159384139). These receipts do not substitute for exact-head checks on the current PR #50 candidate. CodeQL SKIPPED is never a security PASS.

## Current model-routing policy reconciliation

Status: DONE via PR #49
Owner: Codex — sole repository writer for this workflow-only policy update.
Branch: `codex/reconcile-current-model-routing-policy`
Base: `d00d75d4ed1c22088510885b432add5640acdf24` (canonical `origin/main`).
Scope: routing policy in `.agent/MODEL_ROUTING.md`; `.agent/TASKS.md` and `.agent/WORKLOG.md` contain only the ownership and completion receipt required by repository workflow. No product, provider, milestone or dependency work.
Result: active routing is merge-stable: GPT-6 Luna Max is the default; GPT-5.6 Sol High via CodexGPT → ChatGPT Web is the bounded strong route; GPT-6.1 Sol Medium native handles substantial/context-heavy strong reasoning, with High exceptional only; Astra is excluded and CER-M3-004 routing is historical. No product or milestone state changed.
Validation: `git diff --check`, tracked-artifact hygiene and added-diff privacy checks; exact-head hosted checks will be recorded by the PR.
Post-merge receipt: PR #49 was squash-merged at expected head `564b56e1d11b89fa95334c3f30181ee1654823c2`, producing canonical `main` SHA `2441568f0899affdbc6e43d8359a6496c4c4dde7`. Exact-head Build and Test and Repository Quality were SUCCESS; CodeQL was SKIPPED, not a security PASS. PR state was OPEN, non-draft and mergeable; only `.agent/MODEL_ROUTING.md`, `.agent/TASKS.md` and `.agent/WORKLOG.md` changed. Local `main` matched `origin/main` at the squash SHA, was clean, and `git diff --check` passed. PR branch was preserved.

## CER-M3-004 post-merge reconciliation

Status: DONE via PR #48
Owner: Codex — sole repository writer for this bounded reconciliation; concurrency one; no subagents.
Branch: `codex/CER-M3-004-post-merge-reconciliation`
Base: `55a43fb02055e25ab60e18e0406aafd508ffd502` (PR #47 squash merge; tree identical to reviewed head `03d9907b491d27bf0d23b67f2953cff17a4465f9`).
Scope: current operational state and roadmap receipt only; preserve review history, provider dispositions and all provider/research worktrees. No product code, dependencies, architecture or M3.B work.
Result: current operational state reconciled through PR #48; historical provider and review evidence is preserved. M3 remains ACTIVE / INCOMPLETE. M3.B is NEXT / NOT STARTED; M4 must not start. The 11 accepted integrations and blocked/deferred provider dispositions remain unchanged.
Validation: docs-only diff, `git diff --check`, configured repository hygiene and added-diff privacy checks. No local build/test rerun; product code is unchanged. Exact-head hosted checks are reported on PR #48; CodeQL remains SKIPPED, not a security PASS.

## CER-M3-001 — AI quota source reconnaissance and first-provider decision

Status: DONE
Owner: Codex (sole repository writer; read-only provider specialists)
Priority: P0
Depends on: M0/M1/M2 DONE; model-routing PR #41 integrated
Branch: `codex/CER-M3-001-quota-research`
Base: `730ae6c5d518c812213c631eab4129c4bcbea1f6` (`main == origin/main`, clean tree before claim)

Scope: evidence for Claude/Codex/Antigravity sources, semantics, privacy and validation; minimal documentary contract and CER-M3-002 ordering. No provider implementation, packages, UI or M4. Canonical findings belong to the quota reference matrix and provider contract.

Acceptance: sourced matrix, explicit uncertainty, minimum first-provider MetricIds and conformance/live test plan; independent semantic/security review; one research PR, CI, squash integration and clean local checkout.

Result: three-provider sourced matrix and pinned comparisons complete; D-023 selects Codex via the official-client-observed read-only usage GET. Six prospective MetricIds and source/identity/auth/cache/error/live-validation contract are documentary only. No provider implemented. Final independent GPT-6.1 Sol Medium source/semantic/security review PASS, including the pinned Antigravity 1.1.11 quota command evidence; local Release build and 74 existing tests PASS. PR integration is subject to required build/hygiene and squash protection. M3 ACTIVE / INCOMPLETE.

## CER-M3-002 — Codex quota vertical slice

Status: DONE
Owner: Codex (sole repository write owner)
Priority: P0
Depends on: CER-M3-001
Branch: `codex/CER-M3-002-codex-quota`
Base: `9b3d78db99fac5b4967479a9ab9b74259801a4e3` (`main == origin/main`, clean working tree before claim)

Scope: implement only the ordinary Codex bucket using the read-only standard ChatGPT file-auth source, six minimum metrics, and conformance/live acceptance in `docs/PROVIDERS_AI_QUOTAS.md`, following D-023. No generic AI framework, extra billing/model buckets, broader auth/routing support without evidence, or unrelated task work.

Acceptance: all six typed metrics through the existing pipeline; bounded read-only standard file auth and fixed HTTPS source; offline parser/lifecycle/identity tests; Release build and all suites; repository hygiene; independent security/lifecycle review; same-account official usage-UI comparison plus bounded official app-server terminal corroboration at the implementation level, with shared-upstream provenance recorded. Late account-A responses must not be queued for account B. MetricBus v1 may still deliver an immutable snapshot already queued before the owner transition; strict-boundary subscribers reset on owner change.

Result: implementation and Advisor approval complete; cacheable stale snapshots preserve value/ObservedAt and expose the current acquisition ErrorCode, with deterministic cached `source_disagreement` cases for offline, timeout, HTTP 5xx and HTTP 429. Release build PASS (0 warnings/errors), Core 39/39, Telemetry 58/58, Integration 10/10; `git diff --check` PASS; hygiene PASS (83 tracked paths); independent security/lifecycle review PASS. Graphify refreshed to 838 nodes/2,034 edges/81 communities; integrity check PASS. Effective Codex configuration passed the standard-route check before live validation. A sanitized official-UI comparison at code head `13116b9e224b6d740bed02aa3a1fa123452af094` matched durations, rounded remaining percentages and authoritative `reset_at`; a repeat followed ordinary Codex task activity. A bounded app-server `account/rateLimits/read` at pre-documentation HEAD `bbd69ff34c5af6d1b08aa01bb28a9ea9a76ce8b8` matched Cerith's primary and secondary used/remaining direction within one percentage point, reported duration exactly, and `reset_at` within one second. Six Fresh/Measured snapshots came from one Cerith acquisition; one official read used the same selected account. Both resets keep `source_disagreement` because `reset_after_seconds` conflicts with `reset_at`; UI and app-server corroborate `reset_at`. The current Gengchou, TokenTracker and AI Limits implementations are separate codebases but share the same `/wham/usage` upstream, as does the official app-server; this is implementation-level corroboration, not an independent upstream source. No external monitor software was installed or run. No Codex credential/auth/config change; Cerith initialized its protected local identity key. The initial provider process may have issued multiple read-only GETs at its normal 60-second cadence while the local reader was corrected; the exact count was not observable. No login/logout, paid test workload, reset-credit consumption, or raw quota values/payloads/identifiers/tokens/screenshots/account details were used or persisted. PR #43 passed final review and was squash-merged as `3c3db6dd1102ec3ed2d95b6234e5550df08ab34a`; approved head and integrated tree match. Build and Test and Repository Quality passed on the squash commit; CodeQL was skipped under D-013. CER-M3-002 DONE; M3 ACTIVE / INCOMPLETE.

## CER-M3-003 — AI telemetry expansion plan and foundation contract

Status: DONE
Owner: Advisor (docs-only sole repository write owner)
Priority: P0
Depends on: CER-M3-002
Branch: `docs/CER-M3-003-ai-telemetry-direction`
Base: `3f5ca74a26edf88a02e6bda05f759ee3c74032cd`
Merge: PR #45 squash `2aa865ef35000680a3110ff7134b836bd966ef82`

Scope: turn the post-Codex product decision into canonical, implementation-ready direction before parallel provider work. Audit TokenTracker, Gengchou and AI Limits as complementary references; record the 42-tool local-activity target and 17-provider usage-limit target; define zero-config/one-time-setup policy; define compact-surface/dashboard roadmap placement; define provider-isolation and single-owner parallelization boundaries. Documentation/decision only: no product code, packages, provider implementation, WidgetHost surface implementation or UI framework changes.

Acceptance: D-024 and canonical AI telemetry document; PRODUCT_SPEC/ROADMAP/ARCHITECTURE/UI_UX/REFERENCE_PROJECTS/quota contract agree; current reference revisions and license boundaries recorded; no external project becomes Cerith's architecture; CER-M3-002 remains untouched; next implementation work can be split into bounded provider/foundation tasks without repeating product research; CI/hygiene and Advisor review pass.

Result: first GPT-6.1 Sol High adversarial review returned FIXES REQUIRED / NO-GO on `5b0f6e45...`; all findings were corrected. Bounded second review PASS on `5b38464ed1313d44cb3ce8d71f02b1c634f43c56`; Build and Test PASS, Repository Quality PASS, CodeQL SKIPPED under D-013. PR #45 was squash-merged as `2aa865ef35000680a3110ff7134b836bd966ef82`; approved and integrated trees are identical. No product/test/dependency code changed.

## CER-M3-004 — AI quota expansion wave 1

Status: DONE
Owner: completed implementation wave; post-merge reconciliation owner is recorded above.
Historical correction owner: GPT-6 Luna Xhigh — sole repository writer on native Codex for the authorized 2026-10-03 final ZCode correction, concurrency one, no subagents. The bounded independent re-review of `0b9daad6534623d256b79bbd30f39387405793d2` → `84fbf84d1065ba859605cdd2dc83019a1b51fe69` returned `FIXES STILL REQUIRED — NO-GO FOR MERGE`: findings 1–4 and 6–9 are closed; finding 5 remains partially unresolved. This ZCode-only correction does not reopen the eight closures. Fresh final bounded read-only independent re-review remains pending; this owner's self-review is not approval. PR #47 remains DRAFT; canonical main stays untouched.
Priority: P0
Depends on: CER-M3-003
Branch: `codex/CER-M3-004-integration`
Base: `72b43b268b4992d0b0ea45b6e29067d10450274f` (verified clean isolated worktree)

Result (2026-10-03): final independent ZCode-only review PASS on `03d9907b491d27bf0d23b67f2953cff17a4465f9`; findings 1–9 closed within reviewed scopes. User-authorized PR #47 squash `55a43fb02055e25ab60e18e0406aafd508ffd502` has the identical reviewed tree. Eleven accepted quota integrations are canonical; blocked/deferred and live/auth dispositions remain unchanged. Candidate receipt 433/433, zero skipped; Build and Test and Repository Quality SUCCESS; CodeQL SKIPPED, not PASS. M3 remains ACTIVE / INCOMPLETE; next product boundary M3.B passive/local activity acquisition, NOT STARTED.

### Historical pre-merge execution record

Goal: stage evidence-backed safe integrations from this wave, preserve one writer per provider path and one owner for shared files, and record exact blockers for the rest. Follow-up authorized 2026-10-02: same sole owner researches the three preserved reference clones and additional GitHub monitors for Claude subscription availability and Grok weekly quotas; that research-only unit completed at f1531e2. The subsequent user instruction to finish remaining work authorizes bounded provider implementation on preserved branches and integration-owner registration/tests/docs work. This sole owner now evaluates the official Grok billing acquisition and remaining Claude contract; credentials and canonical main remain untouched, and PR #47 remains draft. The 16-provider reference catalog remains research scope; it is not a 17/17 candidate or completion gate.

Final ZCode closure: fix only residual finding 5 (verified owner A → unverifiable auth → verified owner B inheriting A's poll admission), add direct-poll and actual-RunAsync regressions, inspect same-owner and invalid-state transitions, run the complete Release/build/hygiene gates, push the bounded candidate, obtain exact-head Build and Test and Repository Quality, report CodeQL's actual disposition, update the review packet and PR description, and stop ready for one fresh bounded independent re-review of `84fbf84d1065ba859605cdd2dc83019a1b51fe69` → exact final head. The prior reviewer disposition remains attached to its original range. Do not redo the provider wave, alter the eight closed findings, change blocked/deferred statuses, mark PR ready, merge, or touch canonical main.

Provider branch dispositions (the current worktree and exact commit state are recorded below; only the integration owner edits shared files):

| Provider | Branch | Status |
| --- | --- | --- |
| Claude | `codex/CER-M3-004-claude` | EVIDENCE_BLOCKED at preserved clean `51afc1cc91b4b461c93715e9c324c918a1cbc6fc`; no import. Current CLI documentation and SDK 0.3.287 experimental usage seam were rechecked; neither establishes an approved renewable third-party subscription bridge with safe official-client persistence and stable owner binding. Installed package 2.1.185, official latest metadata 2.1.287. Fresh focused tests 18/18 PASS. Exact two-avenue evidence and unblock contract are in docs/AI_QUOTA_REFERENCE_MATRIX.md. No authenticated quota probe, inference or credential mutation. Follow-up confirms missing host subscription is not the implementation blocker; existing valid-session paths and a passive official-statusline candidate are documented in the GitHub acquisition comparison. |
| Antigravity | `codex/CER-M3-004-antigravity` | FINAL PASS at `8889e4b34da1445c2bafe08093c11dc8c63fd1e3`; integration preserves baseline `fdd8a6d664c3e3a0dc1fc7e3cec3ef2514c3535c` and correction `1f9b436ed60b1752405e87753fb8e3352cd42b6c`. Final focused independent Release 31/31 PASS. A sanitized local-RPC probe recognized four buckets and stable before/after status identity; this is schema/identity evidence only. Production parser/snapshot, installed-version, backend-cache and UI validation remain open; full live gate NOT RUN. |
| Cursor | codex/CER-M3-004-cursor | FINAL PASS implementation `24ca7027e204fb1361cb79f573ddf132c9490994` remains imported in staging. Silent-startup auth acceptance is DEFERRED/UNRESOLVED after the bounded official artifact trace; live same-account UI/quota comparison is separately NOT RUN. This provider-specific disposition does not block future exact FINALPASS imports; see docs/PROVIDERS_AI_QUOTAS.md. |
| Gemini CLI / Code Assist | `codex/CER-M3-004-gemini` | EVIDENCE_BLOCKED at preserved clean `9704da8c73ca494b55af013b2d431669266c5d79`; no import. Code Assist Standard/Enterprise remains distinct useful scope after consumer CLI retirement; this consumer oauth-personal/internal v1internal implementation does not establish that product integration. Installed 0.59.0, official latest 0.62.0. Pinned current plaintext persistence still writes directly without a sole-writer/atomic reader contract; no supported Cerith renewal bridge is established. Fresh focused tests 25/25 PASS. Current first-party scope and source evidence are in docs/AI_QUOTA_REFERENCE_MATRIX.md. |
| Kimi | `codex/CER-M3-004-kimi` | DEFERRED_AFTER_M3_QUOTA_WAVE at preserved clean `25630291296c7b8721036dc8ff7dd48a98aebb7d`; no import. Official 2.1.1 `providers:refresh_oauth` awaits owner `ensureFresh` + save without inference/login, but safe Windows cross-process ownership/rotation is unproven: locking is disabled and peer reread is best-effort. Reopen only with a verified secure Windows owner bridge/concurrency path or approved independent renewable grant. Prior deterministic results remain on the branch. |
| Kiro | `codex/CER-M3-004-kiro` | FINAL PASS / READY FOR INTEGRATION at exact tip `ca2b2c388be9b608fa96d8fdc3228d8da6ab7aac`; independent focused Release 44/44, including the bounded cleanup-fault follow-up. Imported with preserved history in merge `4d7f0ada9b09b339d22afaa750010dd318d8e4d3`. Four exact MetricIds. Same-account comparison is `LIVE_NOT_AVAILABLE_ON_THIS_HOST` because no compatible signed-in local/browser session exists; this says nothing about accounts elsewhere. Silent renewal remains empirically unverified/deferred; no MFA or auth mutation. |
| Grok Build | `codex/CER-M3-004-grok-build` | EVIDENCE_BLOCKED at exact tip `d51fba3184b606c36b141e2f9c232c85ef03d0ff`; no import. Current pinned source adds a credible enforced no-API-key policy via GROK_DISABLE_API_KEY_AUTH and OIDC preference, narrowing the old provenance blocker. Isolated pinned-binary billing checks now prove rejection of API-key and untrusted-issuer fixture auth before backend access on 1.0.34 and 1.0.46. Both fail the bounded owner-switch qualification (exit 1); renewal/persistence and child-termination safety remain unproven. Fresh focused tests 12/12 PASS. Preserve auth_provenance_unavailable; exact first-party paths/unblock evidence are in docs/AI_QUOTA_REFERENCE_MATRIX.md. No real secret inspection or authenticated quota probe. Reproducible manual qualification is tools/check_grok_billing_protocol.py; loopback/synthetic fixtures only. Direct stdio and --leader fallback both leave official-writer lifecycle guarantees unresolved. Shared weekly semantics and passive-log limits are documented; no import or acceptance upgrade. |
| GitHub Copilot | `codex/CER-M3-004-copilot` | FINAL PASS / READY FOR INTEGRATION at `6c59c3437f8b23692bab8d338c67c3ca0941827f`; full original and four correction diffs reviewed, independent focused Release 33/33 PASS. Imported without squash as `71fdb49`. Thirteen exact metrics. A bounded production-provider comparison with the same official account UI is `LIVE_VERIFIED` for available remaining-percent only; absent per-bucket resets stay valueless. Credential class and renewal remain unknown. |
| ZCode | `codex/CER-M3-004-zcode` | FINAL PASS / READY FOR INTEGRATION at `0a8dca3555e6c523ab7c40f4ba1e4303c9b13df9`; independent focused Release 23/23. Imported via no-fast-forward merge `198d25b23fc996cb992b5e7331b179b09f9a7be1`, preserving its four provider commits; no real-account quota comparison claimed. |
| OpenCode Go | `codex/CER-M3-004-opencode-go` | FINAL PASS / READY FOR INTEGRATION at `d102d7d301d523acbf4c107c09e8ccd04229b157`; four-file final diff and owner-selection correction reviewed, independent Release 26/26 PASS. Imported without squash as `1b49128`. Six direct used-percent/reset metrics; static API-key owner. SETUP/LIVE pending; no MFA encountered. |
| Command Code | `codex/CER-M3-004-command-code` | FINAL PASS / READY FOR INTEGRATION at exact tip `3919521a3615e2a7ee72cefd811e3572e3bf4124`; full six-file FINAL plus bounded follow-up, independent Release 35/35. Imported with preserved history in merge `82da0b3b4f3013811480d2a5562b152e62d3ca6f`. Six exact MetricIds. Same-account comparison is `LIVE_NOT_AVAILABLE_ON_THIS_HOST` because no compatible signed-in local/browser session exists; this says nothing about accounts elsewhere. No renewal claim, MFA, login, consent or auth mutation. |
| Devin | `codex/CER-M3-004-devin` | DEFERRED_AFTER_M3_QUOTA_WAVE at preserved tip `1a8e169e2bf73f7650ed22c630cb01a63739eafb`; product/team scope and quota-source provenance remain unresolved. No import. |
| Qoder International | `codex/CER-M3-004-qoder` | DEFERRED_AFTER_M3_QUOTA_WAVE at actual clean tip `7a1a89e9f88ea1de57f5bf180f4e8bfd382c88fb`; branch preserved, no import. It reads undocumented desktop IPC methods (`auth/status`, `credit/usage`); current official docs expose CLI `/usage` and SDK `getUsageInfo()` instead. Reopen if Qoder documents/version-controls the direct IPC contract or a low-dependency supported SDK route becomes available. |
| Qoder CN | `codex/CER-M3-004-qoder-cn` | FINAL PASS at exact clean source tip `b2bc2f59018e63aad5938e92a3494b9ded5e4baf`; imported without squash in merge `88ea7ba6d28f0441ebcc59ac00089c3b16ae1a5d`, preserving all four provider commits. Ten MetricIds are registered and documented. Qoder International remains a distinct product and is deferred at its preserved branch tip as recorded above. `LIVE_NOT_AVAILABLE_ON_THIS_HOST`: neither official CLI is present/running and no compatible local product/config/browser session exists; this does not imply accounts are absent elsewhere. Positive host WinTrust verification and same-account UI comparison remain unverified. No install, login, account creation, or usage. |
| Ark Coding Plan | `codex/CER-M3-004-ark-coding-plan` | Integrated from clean tip `c5841bf2ee6140d61ee517db62d8b176a996ce21` by merge `ed1a6ec`; registered through the existing provider supervisor seam. Six typed IDs expose used percentages and reset instants for session/weekly/monthly windows. Official CLI owns the SSO session; Cerith does not read or persist credentials. This host has no `arkcli` on `PATH`; live comparison is `LIVE_NOT_AVAILABLE_ON_THIS_HOST`. Local Release solution build passed with 0 warnings/errors; guarded direct suites passed Core 39/39, Telemetry 355/355, Integration 10/10. Hosted Build and Test and Repository Quality passed at candidate d3cfc24; CodeQL was skipped by workflow policy. |
| Ark Agent Plan | `codex/CER-M3-004-ark-agent` | Implementation complete at clean tip `0c9ba1c04b1899a502f5fc554a7dc8196a41dd5f`; exact provider history imported with merge `1d5cb488e56600124ced8e6e8b3cf537ff3783e2`. Sixteen typed MetricIds cover 5h/daily/weekly/monthly. The official CLI's registered `usage.get_afp_usage` action fills daily; a post-acquisition unforced product read checks active profile and owner scope. Provider Release tests 19/19 and provider-branch full suite 126/126 passed; integration Release build and full-suite verification are recorded below. `LIVE_NOT_AVAILABLE_ON_THIS_HOST`: no compatible local CLI/profile or authenticated session; official console showed login. First-party API/CLI/auth docs, pinned route/profile behavior, dry-run, source inspection and deterministic tests provide the remaining evidence. The Ark CLI does not expose HTTP `Retry-After`; Cerith adds no transport retry and makes no header-aware claim. Mission-level review remains pending under the current override; no per-provider Sol gate. |

Integration checkpoint (autonomous 2026-10-02): code and source-recheck checkpoint 96eefc59b704e6d6a34b683c7be9cfcf9f0a704f retains ten non-Codex providers plus Codex. Fresh Release build 0 warnings/errors; Core 39/39, Telemetry 356/356, Integration 10/10 (405 total), Ark Agent Plan 20/20, coexistence 1/1, shared supervisor injection 11/11. Existing preserved branch source trees are unchanged except the tested Ark Agent shutdown wait; later integration fixtures are retained. Hosted Build and Test SUCCESS (37005658373), Repository Quality SUCCESS (37005658310); CodeQL SKIPPED (37005658376), not a passing security scan. Hygiene/diff-check/scoped sensitive patterns pass; dependency diff empty. Complete fresh-context review packet is .agent/CER-M3-004_REVIEW_PACKET.md. Final documentation commits require exact-head CI; PR #47 stays draft and no independent review or merge occurred.

Mission review cadence (current marathon override; earlier provider-specific reviews remain historical): Review packet is .agent/CER-M3-004_REVIEW_PACKET.md. The current Sol execution owner performs hostile self-review only. Prepare the validated candidate for one independent global review in a fresh future read-only context; do not launch that final review from this owner or mark PR #47 ready. Preserve earlier review receipts as historical records. Follow canonical privacy/source boundaries in `docs/PROVIDERS_AI_QUOTAS.md` and `docs/SECURITY_PRIVACY.md`, and testing policy in `docs/TESTING_QA.md`.

Concurrency: one writer, one integration branch. Qoder International, Claude, Gemini, Kimi, Grok Build and Devin branches remain preserved with their explicit blocked/deferred dispositions; none is imported unless its documented blocker changes. Ark Coding Plan is integrated alongside the prior accepted Codex, Cursor, Antigravity, ZCode, Copilot, OpenCode Go, Kiro, Command Code, Qoder CN and Ark Agent Plan providers. Preserve all existing provider worktrees. Priority unresolved work is Claude, then Gemini CLI / Code Assist if distinct, then Grok Build; marginal work is bounded cheap closure only. Do not require 17/17. Final independent review remains a future-context gate.

Validation outcomes and safety limits are defined once in `docs/PROVIDERS_AI_QUOTAS.md`; distinguish missing compatible host access from implementation evidence gaps. `HUMAN_GATE_PENDING` is reserved for an irreducible action on a plausible compatible session and does not pause unrelated provider work.

Proven common provider correctness checklist (provider-owned implementations/tests; no shared framework or dependency):

- Preserve the original `ObservedAt` on Stale snapshots (reviewed Kimi/Antigravity).
- Use monotonic finite cache age independently of wall-clock reset timing (Kimi/Antigravity).
- Honor long valid Retry-After independently of cache TTL (Kimi/Antigravity).
- Cancel pending bus publication on observed owner/scope transitions and cache-expiry/reset crossings (Kimi/Gemini/Antigravity drafts). MetricBus samples already queued before a transition cannot be retracted.
- Bound Stopped publication and cover drain faults through nested-finally cleanup (Kimi/Antigravity).
- A saved renewable token plus rereading owner files does not prove silent future Cerith-only startup. Require a verified supported non-inference official-owner renewal path or an explicitly approved provider-specific independent renewable-auth ownership, secure-persistence, and concurrency design. Tests that manually rewrite owner credentials prove recovery only, not automatic renewal. Repeated human login is not renewable-auth support. This is an acceptance requirement, not approval for a generic auth framework, dependency, or secret store; provider routes remain evidence-specific.

These reviewed multi-provider findings support correctness rules, not a provider-wave PASS or shared infrastructure approval. No shared subscriber-reset primitive is approved. Auth renewal ownership remains provider-specific; Antigravity loopback/CSRF/proxy settings are not generalized.

Codex is already DONE via CER-M3-002 and is not reimplemented. No speculative runtime foundation is required before provider evidence exists. The coordinator confirmed the integration claim; provider staging branches may rely on it.

Provider-worker write boundary, created only when that provider starts:
- `src/Cerith.TelemetryService/Providers/AI/<Provider>/`
- `tests/Cerith.Telemetry.Tests/Providers/AI/<Provider>/`
- `tests/Cerith.Telemetry.Tests/Fixtures/<Provider>/` when fixtures are justified.

Provider workers must not edit `Program.cs`, project/solution files, Core, IPC, ControlCenter, WidgetHost, canonical docs, agent state, another provider path, shared auth/storage code or dependencies. If a provider genuinely requires one of those, stop and return the exact shared requirement to the integration owner.

Each worker must:
- start from the pinned/current evidence in AI_TELEMETRY, AI_QUOTA_REFERENCE_MATRIX, TokenTracker/Gengchou/AI Limits and first-party sources;
- prefer safe reuse of an existing official session;
- implement one-time setup only when truly necessary;
- never require normal reconfiguration on every app start;
- preserve provider/account/workspace identity and missing != zero;
- keep quota, credits, balance, billing and activity semantically separate;
- add deterministic parser/lifecycle/auth/cache tests inside its boundary;
- stop rather than guess when source semantics, auth ownership or identity remain unresolved;
- return a sanitized live-validation plan and any human authorization needed.

Integration owner owns shared registration/lifecycle, `Program.cs`, dependencies, canonical MetricIds/docs/state, coexistence tests, shared secret/setup infrastructure only if accepted providers prove it necessary, and staged integration. No shared secret infrastructure is approved yet; initial routes should preserve official credential ownership.

Coordinator review constraints: provider IDs follow source semantics; do not force primary/secondary slots. For Copilot, preserve `token_based_billing`, `quota_remaining`, and `credits_used` as distinct source fields, and retain legacy premium-request fields under their legacy semantics when reported.

Lifecycle gate: `Program.RunAsync` includes the pipe server and provider/publisher tasks in `Task.WhenAny` and awaits them together, so an unexpected provider-runner fault can terminate TelemetryService. Add only the smallest integration-boundary failure isolation justified by accepted providers. Coexistence tests must cover absent, slow and auth-failed sources, an unexpected provider failure and shutdown cancellation, proving other accepted providers plus hardware/gaming/Codex continue.

Integration progress (2026-09-30): shared failure isolation is implemented in `Program.cs` at commit `6e99d608c7df039c4c591420ec80b845e5fb6d22`. The small `Func<Task>` supervisor encloses Codex construction and `RunAsync`, contains synchronous construction/start and asynchronous runner failures, emits only a fixed provider ID and sanitized category, and waits for service cancellation after an unexpected exit. It adds no provider framework or dependency. Baseline at `412daad424bc7d85e1180611ded41fbab30cbe5c`: Release build 0 warnings/errors; Core 39/39, Telemetry 58/58, Integration 10/10; hygiene 84 tracked paths. After the seam: Release build 0 warnings/errors; Core 39/39, Telemetry 62/62, Integration 10/10; `git diff --check` and hygiene PASS.

The shared-bus composition uses the Codex harness's real registry/MetricBus, real `HardwareTelemetryProvider`, production `PresentMonTelemetryProvider` through its existing `IPresentMonCapture` fake seam, Codex, Cursor through its synthetic-auth/server seam, and Antigravity through its existing injected acquisition seam. Subscribers start before publication. The same Release test passes Cursor success, missing auth, unauthorized response, and timeout while Hardware/RAM, PresentMon, Codex and Antigravity continue publishing on that bus. Real RAM must be Fresh/Measured; real CPU must be Fresh/Measured or valueless Unavailable. Antigravity values are Stale/Measured because the local RPC has no authoritative backend observation timestamp. Provider tests also cover cancellation, unexpected worker failure, owner transitions, and bounded stopped publication/shutdown. No hardware fake, generic fixture/framework, dependency, shared identity primitive, or token store is introduced.

Antigravity final review accepted tip `8889e4b34da1445c2bafe08093c11dc8c63fd1e3` after the original `fdd8a6d664c3e3a0dc1fc7e3cec3ef2514c3535c` and correction `1f9b436ed60b1752405e87753fb8e3352cd42b6c`. The accepted diff adds only its provider-owned files and no package or shared-file changes. Sol's bounded follow-up and focused independent Release 31/31 plus diff-check passed; worker Release build had zero warnings/errors, and its earlier full solution run was 137/137 before the final regression test. The correction retains a verified listener port, distinguishes unverifiable owner from a positive changed owner, preserves profile backoff, and clears cached/pending quota when positive Retry-After prevents owner verification. Already-buffered immutable MetricBus samples remain non-retractable. No current-user official language server was present, so live RPC/version/schema/backend-cache validation remains NOT RUN.

Cursor's exact implementation remains staged while silent-startup auth acceptance is DEFERRED/UNRESOLVED; live same-account comparison is separately NOT RUN. A sanitized browser-only observation is UI_ONLY and adds no quota-equality or renewal proof. Current import decisions follow the current mission override above: accepted provider owners complete their exact implementation and self-review; no per-provider Sol review is required. Qoder International is deferred for undocumented IPC support; Ark Coding Plan is integrated through the official CLI. Historical per-provider FINALPASS labels below do not override the current mission review cadence.

Sol bounded follow-up review passed exact integration HEAD 2640d434cbd515fb04edec8ba0d8e06f85370ea6: lifecycle/coexistence correction PASS, focused independent Release 8/8, complete nine-file diff reviewed and diffcheck clean. Shared production is unchanged by the review correction. The regression gates an entered Cursor request while two rounds of real Hardware, synthetic PresentMon, Codex and accepted Antigravity publications complete on the same bus, then bounds cancellation/drain through the existing runner wrapper. Full Release suite/hygiene/privacy evidence is recorded in WORKLOG; no comprehensive secret-scanner claim.

Prior integration checkpoint before Kiro/Command imports (code/test commit `283f182`): Copilot tip `6c59c3437f8b23692bab8d338c67c3ca0941827f` and OpenCode Go tip `d102d7d301d523acbf4c107c09e8ccd04229b157` were imported as separate no-fast-forward merges `71fdb49` and `1b49128`, preserving both provider histories. That checkpoint's supervisor, coexistence, Release and CI results remain recorded in WORKLOG; later integration status follows.

Current Qoder CN integration group: imported exact FINAL PASS tip b2bc2f59018e63aad5938e92a3494b9ded5e4baf in no-fast-forward merge 88ea7ba6d28f0441ebcc59ac00089c3b16ae1a5d, preserving all four provider commits. Integration commit c4df8c173996621f8fe522461153fb3175d2063f registers Qoder CN through the existing KeepServiceAliveAfterProviderExitAsync seam, documents its ten exact MetricIds, and extends shared-bus and failure-isolation coverage. While Cursor acquisition is entered and pending, the gated same-bus test publishes twice from real Hardware, synthetic PresentMon, Codex, Antigravity, ZCode, Copilot, OpenCode Go, Kiro, Command Code and synthetic Qoder CN values; Cursor cancellation/drain remains bounded. Supervisor failure isolation covers all nine registered provider IDs including Codex. This group adds no shared primitive or dependency: one Qoder implementation does not establish a second matching implementation with harmful duplication.

Verification at c4df8c1: serial Release solution build passed with 0 warnings/errors. Direct guarded MTP runs passed Core 39/39, Telemetry 315/315 and Integration 10/10; focused Qoder CN 19/19, gated coexistence 1/1 and supervisor failure isolation 9/9. Graphify code refresh: 2,894 nodes / 8,450 edges. Configured filename hygiene passed for 123 tracked paths; scoped added-diff privacy checks passed over 1,721 added lines; git diff --check passed. These checks are not a comprehensive secret scan. Copilot remains LIVE_VERIFIED only for available remaining-percent comparison with the same-account official UI; absent per-bucket resets remain valueless and credential class/lifetime/renewal remain unknown. Qoder CN comparison is LIVE_NOT_AVAILABLE_ON_THIS_HOST; no account absence elsewhere, host signer verification or UI equality is inferred. PR #47 remains draft. Local main is 3f5ca74a26edf88a02e6bda05f759ee3c74032cd; origin/main is 72b43b268b4992d0b0ea45b6e29067d10450274f. No main merge or M3/16-provider completion claim.

Integration policy: stage only evidence-backed accepted providers in small verified groups, validate their Release build/tests and coexistence, and inspect hosted CI after push. The final integration PR remains draft through the single global review and subsequent Advisor/user decision; the integration owner does not merge main. A slow/absent/auth-failed provider must not stop hardware, gaming, Codex or other AI integrations.

Wave acceptance: safe bounded integrations must pass Release build/tests, repository hygiene, identity/auth/failure and coexistence checks; unavailable live comparisons must be labeled precisely. Every unresolved target receives an evidence-backed blocked/deferred disposition. The mission candidate receives hosted CI and one global independent review; this wave does not impose per-provider Sol gates, require 17/17 parity, or close M3.

## CER-M2-003 — selected game/session summary and M2 exit

Status: DONE
Owner: GPT-6 Luna Max (Codex, sole repository writer)
Priority: P0
Depends on: CER-M2-002
Branch: `codex/CER-M2-003-reconciliation`
Base: `5364ec7824f424e0206c9ebdecf826e16484d60d` (product PR #38 merged; tree matches approved head `561450d391234c98294fdd5fc6e99336575a13d1`)

Scope: reconcile operational state after the product merge. Docs/state only; do not start M3.

Result: product PR #38 added `game.session.application` for the explicitly selected `--pid` process using Windows `Process.ProcessName`; D-022 and `docs/METRICS.md` hold the durable contract. The label remains Fresh independently of frame/PresentMon availability, and the retained process handle prevents PID reuse from becoming a new session. No automatic game detection or extra process metadata is included.

M2 exit: PASS. A controlled service-loss probe observed the same Fresh application identity through PresentMon stop/restart. FPS, frame time and P1 became valueless Error/Unavailable while stopped; FPS/frame time recovered Fresh after service start and P1 after its warm-up. TelemetryService and the selected target continued without restart or retarget.

Verification: Release build PASS (0 warnings/errors); Core 39/39, Telemetry 26/26, Integration 9/9 PASS. PR #38 head `561450d391234c98294fdd5fc6e99336575a13d1` passed CI `build` and `hygiene`; CodeQL skipped under D-013. Fresh Sol Medium hostile review PASS with no actionable findings. Squash `5364ec7824f424e0206c9ebdecf826e16484d60d` has the same tree as the approved head. CER-M2-003 DONE; M2 DONE; M3 NEXT / NOT STARTED.

## CER-M2-002 — Displayed FPS P1 rolling window

Status: DONE
Owner: GPT-6 Luna Max (Codex, sole repository writer)
Priority: P1
Depends on: CER-M2-001
Branch: `codex/CER-M2-002-presentmon-p01`
Base: `029d3a254d5dc684b9f4c224c36b778671351015` (`origin/main`, confirmed after rebase)

Scope: add truthful displayed-FPS P1 using the existing PresentMon boundary, MetricBus, Named Pipe v1 and MetricId-only WidgetHost. Keep the existing one-second FPS and frame-time metrics independent. No 0.1% low, process selection, IPC redesign, or M2 exit claim.

Semantic gate: fresh GPT-6 Sol High read-only review completed and accepted before product edits. Use `PM_METRIC_DISPLAYED_FPS` / `PM_STAT_PERCENTILE_01` as PresentMon v2.6.0 defines it: reciprocal displayed interval percentile with reversed rank and upstream linear interpolation; finite zero intervals from non-displayed frames remain in the percentile input. MetricId `game.fps.display.p01`; requested trailing window 10 seconds, warmup from the first positive displayed-change observation. Reset after more than two seconds without recent positive display evidence and on capture/target restart. This is P1 FPS, not average worst 1% or integral low. The 4096-frame PresentMon ring may shorten retained history above roughly 410 frames/s; `PM_STAT_COUNT` is not implemented and is not a gate.

Result: implementation and deterministic tests passed. Release restore/build PASS with 0 warnings/errors; Core 39/39, Telemetry 22/22, Integration 9/9 PASS; Repository Quality `hygiene` PASS; CodeQL SKIPPED under D-013. Fresh hostile Sol High review PASS; Ponytail diff/debt/repo audit and code-reviewer skill completed without new complexity or actionable findings. Controlled D3D11 runtime and independent numeric validation (including the high-drop zero-sample reconstruction), service-loss recovery and DISPLAY2 visibility are recorded in `.agent/HANDOFF.md`. Product PR #36 merged by squash as `0a7ce2ee87aa27e65efea804fa4c0767faa17cbf`; approved product head `cf49247d53808f3cb0653708316c8cc732450c2a` and integrated tree match. M2 remains INCOMPLETE; CER-M2-003 is NEXT / NOT STARTED.

## CER-M2-001 — PresentMon displayed telemetry vertical slice

Status: DONE
Owner: Codex (single writer)
Priority: P0
Depends on: M1 — Hardware MVP
Branch: `codex/CER-M2-001-presentmon-display`
Base: `89ddd69c13affd76b3b6085a25753bb7b83b0a3f` (`main`, confirmed after fetch)

Scope: one explicit target PID; official PresentMon Service/API; average displayed FPS and displayed frame time; normalized snapshots through the existing MetricBus, Named Pipe v1 and WidgetHost. No other gaming metrics or M2 exit claim.

Result: official PresentMon 2.6.0 Service/SDK verified and installed; implementation and deterministic tests complete. Advisor PASS. The correction based on `c63e25110e4c18be3a6698e58b80cbbd4f411dba` made multiple-chain errors current-poll state, preserved the API-returned frame blob size for row stepping, copied the upstream MIT license to build/publish, and clarified displayed-FPS semantics. Release restore/build PASS (0 warnings/errors); Core 39/39, Telemetry 16/16, Integration 9/9 PASS; publish and output inspection PASS. A final-build same-PID, same-capture D3D11 run recovered `Fresh` after removing the second chain, with variable `Error/multiple_swap_chains`, `Unavailable` and `Fresh` samples across the transition. The instrumented rerun verified both chains continued calling `Present` but did not reproduce that telemetry sequence; exact counts and the per-poll API limitation are in `.agent/HANDOFF.md`. Loader SHA-256 remained unchanged; build and publish each contain loader plus license. Product PR #31 passed Build and Test and Repository Quality; CodeQL was SKIPPED under D-013. Approved product head `086db95e29124900854e29d2b70caa16b3f43889` was squash-merged to `main` as `9902cc19ff0f2911ab4ee0ece2337d12b1d9edcf`, with an identical integrated tree. M2 remains INCOMPLETE; CER-M2-002 is NEXT / NOT STARTED.

## CER-M1-004 — Hardware/provider diagnostics and M1 exit

Status: DONE
Owner: Codex (single writer)
Priority: P0
Depends on: CER-M1-003
Branch: `codex/CER-M1-004-hardware-diagnostics-m1-exit`

Scope: minimal sanitized hardware/provider diagnostics, real non-elevated runtime verification, and M1 exit audit with evidence for its criteria. No M2 work.

Result: Advisor PASS final for diagnostics and M1 exit. Both evidence gates passed without product-code changes: the same service/LHM 0.9.6 discovered four Storage when elevated and published coherent `storage.0` usage/total/free `Fresh`/`Measured` values through the existing pipe; without elevation it still discovered zero. A targeted console signal produced `TelemetryService stopped.` and process exit code 0. Release build and all canonical suites PASS. PR #29 passed Build and Test and Repository Quality (CodeQL SKIPPED under policy) and was squash-merged into `main` at `59ecb648a9f714376578fc16cdaa446571bd4874`. M1 DONE; M2 NEXT/NOT STARTED. Non-elevated Storage remains unverified on this host. No new dependency, metric ID, diagnostics protocol, ControlCenter feature or M2 work.

## CER-M1-003 — Storage and network telemetry baseline

Status: DONE (Storage runtime unverified: LHM discovered zero devices without elevation)
Owner: Codex (single writer)
Priority: P0
Depends on: CER-M1-002
Branch: `codex/CER-M1-003-storage-network`

Scope: probe local no elevado y mapping verificado de LibreHardwareMonitorLib 0.9.6 para almacenamiento y red; extender el único lifecycle hardware, métricas canónicas y demo compacta en WidgetHost. Sin diagnostics M1, paquetes nuevos ni framework de hotplug. M1 permanece INCOMPLETE.

Result: Advisor PASS final; PR #27 passed Build and Test and Repository Quality (CodeQL SKIPPED under current policy) and was squash-merged into `main` at `70c54cf692c8a8f33d6719a845a8124d0fd9a9fc`. Storage usage/total/free has LHM 0.9.6 source mapping audited but no positive runtime validation on this host: Windows saw four disks and LHM discovered zero Storage without elevation. Storage read/write throughput remains deferred. Network download/upload/usage had real `Fresh/Measured` traffic. `storage.N`/`network.N` lack guaranteed persistent physical identity; removed devices become unavailable and additions/reconnections may require restart. No general hotplug system or M1 diagnostics. M1 INCOMPLETE; hardware/provider diagnostics pending; CER-M1-004 NOT STARTED.

## CER-M1-002 — Multi-metric hardware baseline

Status: DONE
Owner: Codex (single writer)
Priority: P0
Depends on: CER-M1-001
Branch: `codex/CER-M1-002-multi-metric-hardware`

Scope: one LHM lifecycle for CPU/GPU, direct Windows RAM baseline, concurrent v1 Named Pipe sessions, minimal multi-metric WidgetHost, focused tests and canonical documentation. M1 remains incomplete.

Result: PR #25 received independent Advisor PASS, Build and Test PASS, Repository Quality PASS and CodeQL SKIPPED under current policy; squash-merged into `main` at `6f442a2ecdf92d4368dce30ad55572950e52ccb2`. Local non-elevated CPU/GPU and pre-change IPC probes completed; 11 metrics published concurrently on one CPU and one AMD GPU; Release build and Core 39/39, Telemetry 7/7, Integration 9/9 PASS. CPU/RAM/GPU/VRAM/fan baseline and concurrent Named Pipe are integrated. NVIDIA/Intel/multi-GPU remain physically unvalidated; `gpu.N` has no proven persistent physical identity. M1 remains incomplete. CER-M1-003 is not defined or claimed.

## CER-M1-001 — LibreHardwareMonitor CPU usage vertical slice

Status: DONE
Owner: Codex
Priority: P0
Depends on: M0 — Foundation
Branch: `feat/CER-M1-001-lhm-cpu-usage`

Scope: probe local no elevado de LibreHardwareMonitorLib 0.9.6; si confirma inequívocamente CPU Total, publicar `cpu.total.usage` por el MetricBus y Named Pipe existentes y mostrarlo en WidgetHost. Sin otras métricas ni arquitectura de providers.

Acceptance: Release build, tres suites, CI y demo local PASS; revisión independiente del Advisor PASS; PR integrado por squash. M1 sigue abierto y no se declara completa.

Resultado: PR #22 pasó review independiente del Advisor con PASS y se integró mediante squash en `b7e40447f48cd6afd2b015a94a49d825f28099f9`. Build and Test PASS, Repository Quality PASS, CodeQL SKIPPED según D-013. D-019 quedó integrada. `cpu.total.usage` es el único slice hardware implementado. No se probó hardware diverso ni se añadieron tests CI dependientes de hardware. La licencia exacta de Mono.Posix.NETStandard 1.0.0 continúa como deuda de provenance/third-party-notice previa al public release; no se conoce incompatibilidad.

## CER-M0-001 — Repository and solution skeleton

Status: DONE
Owner: Codex
Priority: P0
Resultado: solución compilada; el runner de pruebas está configurado y el resumen confirma cero casos, intencionadamente. La revisión de seguridad y Graphify se completaron.

Scope:

```text
repository root
src/
tests/
.github/
tools/
```

Acceptance:

- `Cerith.sln`;
- projects:
  - `Cerith.Core`
  - `Cerith.TelemetryService`
  - `Cerith.WidgetHost`
  - `Cerith.ControlCenter`
  - `Cerith.Core.Tests`
  - `Cerith.Telemetry.Tests`
  - `Cerith.Integration.Tests`
- dependency direction matches `docs/ARCHITECTURE.md`;
- build succeeds;
- no product features;
- no unnecessary NuGet dependencies;
- security/quality config remains intact;
- staged files reviewed before first commit.

## CER-M0-002 — Metric contracts

Status: DONE
Owner: Codex
Priority: P0
Depends on: CER-M0-001

Resultado: contratos y 17 pruebas reales completados; review del Advisor aprobada en PR #6 y merge mediante squash en `main` (`e895e1666d376a22c15a31d6b809c5a6b716307d`). Build and Test y Repository Quality pasaron; CodeQL se omitió según D-013. Los PR Dependabot #3 y #4 siguen siendo mantenimiento separado.

Acceptance:

- MetricId;
- MetricDescriptor;
- MetricSnapshot;
- MetricState;
- MetricConfidence;
- meaningful unit tests.
- CI ejecuta `Cerith.Core.Tests` sin tolerar `ZeroTests`; solo los proyectos vacíos toleran temporalmente el código 8 en invocaciones propias, y pierden esa excepción al incorporar su primer test real. Sin tests dummy.

## CER-M0-003 — MetricRegistry

Status: DONE
Owner: Codex
Priority: P0
Depends on: CER-M0-002
Resultado: implementación completada; PR #8 pasó review del Advisor con PASS y se integró mediante squash en `main` (`00e8a9f1d35d9d96b754298a55ff4cfb416bcc71`). Build and Test y Repository Quality success; CodeQL skipped según D-013.

## CER-M0-004 — MetricBus

Status: DONE
Owner: Codex
Priority: P0
Depends on: CER-M0-002
Resultado: PR #10 pasó review independiente del Advisor con PASS y se integró mediante squash en `main` (`bd305ae1b2975c8d3a81125688ec21639ab90817`). Build and Test y Repository Quality: success; CodeQL: skipped según D-013. D-015 y el contrato de `docs/METRICS.md` quedaron integrados. Dependabot #3/#4 permanece separado.

Acceptance:

- multiple subscribers;
- cancellation;
- bounded memory;
- disposal;
- tests.

## CER-M0-005 — SyntheticProvider

Status: DONE
Owner: Codex
Priority: P0
Depends on: CER-M0-003, CER-M0-004
Resultado: PR #12 pasó review independiente del Advisor con PASS y se integró mediante squash en `main` (`729075dcfcef1ae849b9339a96b438d234e7ff37`). Build and Test y Repository Quality: success; CodeQL: skipped según D-013.

## CER-M0-006 — IPC prototype

Status: DONE
Owner: Codex
Priority: P0
Resultado: benchmark real en Windows comparó Named Pipes, TCP loopback y AF_UNIX con dos runs, 5000 RTT medidos por candidato/payload y comprobaciones funcionales. D-016 y el baseline de `docs/PERFORMANCE.md` quedaron integrados. PR #14 pasó review independiente del Advisor con PASS y se mergeó mediante squash en `dc6c051f0734fc5eb8a301a16bda671d2ba94221`. Named Pipes es el transporte elegido para la primera implementación; esta debe conservar local-only y demostrar rechazo de clientes remotos, porque `CurrentUserOnly` por sí solo no basta. No hay una task IPC/WidgetHost reclamada.

Acceptance:

- compare realistic candidate mechanisms;
- measure latency/CPU/complexity;
- record decision;
- do not overbuild.

## CER-M0-007 — Named Pipe snapshot bridge

Status: DONE
Owner: Codex
Priority: P0
Depends on: CER-M0-004, CER-M0-005, CER-M0-006
Branch: `feat/CER-M0-007-named-pipe-bridge`

Scope: `Cerith.Core` wire contract; `Cerith.TelemetryService` local-only server; `Cerith.WidgetHost` client; integration tests; CI ZeroTests removal; compact architecture and decision updates.

Acceptance: versioned bounded snapshots over a Windows Named Pipe with current-user isolation and effective remote rejection; deterministic lifecycle, cancellation, reconnect and protocol tests; Release build and all test projects pass.

Resultado: PR #16 pasó review independiente del Advisor con PASS y se integró mediante squash en `main` (`192afc43632ea50fe92e6416e65431ce0bbe7da8`). D-017 y el bridge están integrados; SyntheticProvider → MetricBus → Named Pipe → WidgetHost client demostrado. Protocolo v1, máximo 64 KiB por frame, ACL del usuario actual y `PIPE_REJECT_REMOTE_CLIENTS` verificado con `GetNamedPipeInfo`. No se probó desde otra máquina ni otra cuenta. Core.Tests 39/39, Telemetry.Tests 7/7, Integration.Tests 8/8; todos sin tolerancia ZeroTests. Build and Test PASS; Repository Quality PASS; CodeQL SKIPPED según D-013. Sin paquetes ni proyectos nuevos.

## CER-M0-008 — Native visual pipeline

Status: DONE
Owner: Codex
Priority: P0
Depends on: CER-M0-007
Branch: `feat/CER-M0-008-native-visual-pipeline`

Scope: TelemetryService publica `synthetic.counter` periódicamente; WidgetHost WinUI 3 unpackaged recibe snapshots por el cliente Named Pipe existente y muestra el valor cambiante. Sin widget engine ni supervisión de procesos.

Acceptance: ventana inmediata con estado Connecting, valor real cambiante y estado unavailable ante fallo; cierre y cancelación limpios; restore, build Release y tres suites PASS; demo manual reproducible.

Resultado: Advisor independiente PASS; PR #18 integró el slice por squash en `main` (`15646293886e8951779c45d4bf894c3017c0bc7c`). Build and Test y Repository Quality PASS; CodeQL SKIPPED según D-013. El linked source de `NamedPipeMetricClient.cs` en Integration.Tests queda aceptado solo para M0. Windows App SDK 2.5.1 queda aceptado para M0; la compatibilidad con la licencia GPL-3.0-or-later candidata debe cerrarse antes de fijar/publicar la licencia definitiva. Sin cambios de implementación tras la review.

## CER-M0-009 — M0 exit diagnostics and scope closure

Status: DONE
Owner: Codex
Priority: P0
Depends on: CER-M0-008
Branch: `feat/CER-M0-009-m0-exit-diagnostics`

Scope: diagnóstico mínimo del ciclo de vida de TelemetryService y aclaración del alcance M0 de ControlCenter y diagnostics. Sin UI nueva, frameworks de logging ni diseño de M1.

Resultado: Advisor PASS; PR #20 se integró por squash en `main` (`40c0b82920a4b9ca797300bea16d8a1f506a97c7`). Build Release 0 warnings/errors; Core 39/39, Telemetry 7/7, Integration 8/8 PASS; CI Build and Test y Repository Quality PASS; CodeQL SKIPPED según D-013. Demo manual observó ready, stopped y fallo del segundo proceso sin mensaje ready. Sin cambios de WidgetHost, proyectos, paquetes ni abstracciones. M0 — Foundation queda DONE tras satisfacer sus requisitos documentados.
