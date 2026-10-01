# Tasks

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

Status: IN PROGRESS
Owner: Dewey One — GPT-6 Luna Max, max effort (sole shared-file integration owner; latest authoritative `turn_context` preserved for this thread is 2026-10-01 23:15:34 Europe/Madrid). Main coordinator/reviewer route requested by the user is GPT-5.6 Sol via Web; that route is not an actual-runtime verification.
Priority: P0
Depends on: CER-M3-003
Branch: `codex/CER-M3-004-integration`
Base: `72b43b268b4992d0b0ea45b6e29067d10450274f` (verified clean isolated worktree)

Goal: implement the remaining 16 reference-backed usage-limit integrations in parallel where evidence supports a safe bounded path, while preserving one write owner per provider path and one integration owner for shared files.

Provider worker claims (one Luna Max owner per provider; queued branch names are planned, not proof of creation; workers do not edit TASKS):

| Provider | Branch | Status |
| --- | --- | --- |
| Claude | `codex/CER-M3-004-claude` | BLOCKED at preserved clean `51afc1c`; no FINALPASS/import. Installed 2.1.197 renewal proof does not establish safe in-place refresh or an independently authorized renewable grant; a signed-in Free product observation does not validate paid Claude Code quota, and no MFA was encountered. Claude slot was reused by Grok Build. |
| Antigravity | `codex/CER-M3-004-antigravity` | FINAL PASS at `8889e4b34da1445c2bafe08093c11dc8c63fd1e3`; integration preserves baseline `fdd8a6d664c3e3a0dc1fc7e3cec3ef2514c3535c` and correction `1f9b436ed60b1752405e87753fb8e3352cd42b6c`. Final focused independent Release 31/31 PASS. A sanitized local-RPC probe recognized four buckets and stable before/after status identity; this is schema/identity evidence only. Production parser/snapshot, installed-version, backend-cache and UI validation remain open; full live gate NOT RUN. |
| Cursor | codex/CER-M3-004-cursor | FINAL PASS implementation `24ca7027e204fb1361cb79f573ddf132c9490994` remains imported in staging. Silent-startup auth acceptance is DEFERRED/UNRESOLVED after the bounded official artifact trace; live same-account UI/quota comparison is separately NOT RUN. This provider-specific disposition does not block future exact FINALPASS imports; see docs/PROVIDERS_AI_QUOTAS.md. |
| Gemini CLI / Code Assist | `codex/CER-M3-004-gemini` | BLOCKED at preserved clean `9704da8`; no import. Review and independent Release 25/25 PASS, but v0.59.0 lazy `getAccessToken` persistence has no verified cross-process sole-writer contract; `--list-sessions` may model-generate summaries and no quiet refresh-only owner operation is verified. Unblock requires supported, versioned non-inference same-owner renewal with durable safe concurrency/rotation, or an approved independent renewable grant. Worker closed; slot reused by ZCode. |
| Kimi | `codex/CER-M3-004-kimi` | BLOCKED at preserved clean `25630291296c7b8721036dc8ff7dd48a98aebb7d`; no FINALPASS/import. Official 2.1.1 `providers:refresh_oauth` awaits owner `ensureFresh` + save without inference/login, but safe Windows cross-process ownership/rotation is unproven: locking is disabled and peer reread is best-effort. Unblock requires a verified secure Windows owner bridge/concurrency path or an approved official independent renewable grant. Historical MID independent 42/42; worker focused 44/44 and full Release 151/151, build 0 warnings/errors; no full Sol FINAL code-review claim. Worker closed; slot reused by OpenCode Go. |
| Kiro | `codex/CER-M3-004-kiro` | FINAL PASS / READY FOR INTEGRATION at exact tip `ca2b2c388be9b608fa96d8fdc3228d8da6ab7aac`; independent focused Release 44/44, including the bounded cleanup-fault follow-up. Imported with preserved history in merge `4d7f0ada9b09b339d22afaa750010dd318d8e4d3`. Four exact MetricIds. Same-account comparison is `LIVE_NOT_AVAILABLE_ON_THIS_HOST` because no compatible signed-in local/browser session exists; this says nothing about accounts elsewhere. Silent renewal remains empirically unverified/deferred; no MFA or auth mutation. |
| Grok Build | `codex/CER-M3-004-grok-build` | BLOCKED at exact tip `d51fba3184b606c36b141e2f9c232c85ef03d0ff`; MID disposition `auth_provenance_unavailable`. Pinned official `getBearerToken` can silently return same-owner static-key fallback after failed session renewal, and ACP exposes no provenance signal. Focused 12/12 and full Release 70/70; preserve source/tests, no import or fabricated PASS. |
| GitHub Copilot | `codex/CER-M3-004-copilot` | FINAL PASS / READY FOR INTEGRATION at `6c59c3437f8b23692bab8d338c67c3ca0941827f`; full original and four correction diffs reviewed, independent focused Release 33/33 PASS. Imported without squash as `71fdb49`. Thirteen exact metrics. A bounded production-provider comparison with the same official account UI is `LIVE_VERIFIED` for available remaining-percent only; absent per-bucket resets stay valueless. Credential class and renewal remain unknown. |
| ZCode | `codex/CER-M3-004-zcode` | FINAL PASS / READY FOR INTEGRATION at `0a8dca3555e6c523ab7c40f4ba1e4303c9b13df9`; independent focused Release 23/23. Imported via no-fast-forward merge `198d25b23fc996cb992b5e7331b179b09f9a7be1`, preserving its four provider commits; no real-account quota comparison claimed. |
| OpenCode Go | `codex/CER-M3-004-opencode-go` | FINAL PASS / READY FOR INTEGRATION at `d102d7d301d523acbf4c107c09e8ccd04229b157`; four-file final diff and owner-selection correction reviewed, independent Release 26/26 PASS. Imported without squash as `1b49128`. Six direct used-percent/reset metrics; static API-key owner. SETUP/LIVE pending; no MFA encountered. |
| Command Code | `codex/CER-M3-004-command-code` | FINAL PASS / READY FOR INTEGRATION at exact tip `3919521a3615e2a7ee72cefd811e3572e3bf4124`; full six-file FINAL plus bounded follow-up, independent Release 35/35. Imported with preserved history in merge `82da0b3b4f3013811480d2a5562b152e62d3ca6f`. Six exact MetricIds. Same-account comparison is `LIVE_NOT_AVAILABLE_ON_THIS_HOST` because no compatible signed-in local/browser session exists; this says nothing about accounts elsewhere. No renewal claim, MFA, login, consent or auth mutation. |
| Devin | `codex/CER-M3-004-devin` | BLOCKED at `1a8e169e2bf73f7650ed22c630cb01a63739eafb`; product/team-scope/provenance remains unresolved at MID. No import. |
| Qoder International | `codex/CER-M3-004-qoder` | Active Luna MID worker in its managed worktree. Keep distinct from Qoder CN; no import before explicit FINAL PASS. |
| Qoder CN | `codex/CER-M3-004-qoder-cn` | NOT STARTED; separate regional endpoint and credentials. |
| Ark Coding Plan | `codex/CER-M3-004-ark-coding-plan` | Active Luna MID worker; first-party route is scoped and prior runtime receipts are independently verified. No import before explicit FINAL PASS. |
| Ark Agent Plan | `codex/CER-M3-004-ark-agent` | NOT STARTED; preserve AFP versus calls/tokens and exact windows. |

Provider cadence (supersedes continuous draft supervision): exactly two scheduled Sol gates per provider: MID direction gate and FINAL complete gate. At MID, return a committed compact handoff with SHA, scope, evidence and open questions, then pause for Sol MIDPASS or MIDFIXES; apply MIDFIXES within that gate and resume autonomous completion after MIDPASS. Request FINAL only when CODECOMPLETE + TESTSCOMPLETE + HANDOFFREADY. Between gates, escalate only concrete high-risk issues. Follow canonical privacy/source boundaries in `docs/PROVIDERS_AI_QUOTAS.md` and `docs/SECURITY_PRIVACY.md`, and testing policy in `docs/TESTING_QA.md`.

Concurrency: Kiro and Command Code are now accepted exact FINALPASS imports alongside Cursor, Antigravity, ZCode, Copilot and OpenCode Go; Codex remains the separate completed provider. Claude, Gemini, Kimi, Grok Build and Devin remain evidence-blocked at preserved tips; Qoder International and Ark Coding Plan are active at MID; Qoder CN and Ark Agent Plan have not started. Cursor auth remains DEFERRED/UNRESOLVED with live comparison NOT RUN. Kiro and Command Code same-account comparisons are `LIVE_NOT_AVAILABLE_ON_THIS_HOST`; Kiro's empirical renewal remains deferred and Command renewal is unclaimed. Keep current sessions/branches; future queue concurrency defaults to three and may increase to four/five only when route evidence is clear and shared-auth/review backlog is absent. Do not dispatch from integration.

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

Cursor FINAL PASS implementation 24ca7027e204fb1361cb79f573ddf132c9490994 remains in staging. A bounded official Windows artifact trace confirmed version 3.22.12 and a valid Anysphere signature, but the Inno 6.4.0.1 payload could not be parsed with the checksum-verified upstream extractor (supported through 6.3.3); token lifetime/renewal and owner operation remain unproven. Mark Cursor auth acceptance DEFERRED/UNRESOLVED, distinct from live UI/quota NOT RUN. A sanitized browser-only observation is UI_ONLY and adds no quota-equality or renewal proof. This provider-specific disposition does not block imports of unrelated exact FINALPASS tips. Antigravity, ZCode, Copilot and OpenCode Go are accepted staged imports. Command Code and Kiro have bounded active follow-ups; Qoder International is active at MID. Import only exact future FINALPASS tips.

Sol bounded follow-up review passed exact integration HEAD 2640d434cbd515fb04edec8ba0d8e06f85370ea6: lifecycle/coexistence correction PASS, focused independent Release 8/8, complete nine-file diff reviewed and diffcheck clean. Shared production is unchanged by the review correction. The regression gates an entered Cursor request while two rounds of real Hardware, synthetic PresentMon, Codex and accepted Antigravity publications complete on the same bus, then bounds cancellation/drain through the existing runner wrapper. Full Release suite/hygiene/privacy evidence is recorded in WORKLOG; no comprehensive secret-scanner claim.

Prior integration checkpoint before Kiro/Command imports (code/test commit `283f182`): Copilot tip `6c59c3437f8b23692bab8d338c67c3ca0941827f` and OpenCode Go tip `d102d7d301d523acbf4c107c09e8ccd04229b157` were imported as separate no-fast-forward merges `71fdb49` and `1b49128`, preserving both provider histories. That checkpoint's supervisor, coexistence, Release and CI results remain recorded in WORKLOG; later integration status follows.

Current integration wave: imported only exact Kiro `ca2b2c388be9b608fa96d8fdc3228d8da6ab7aac` and Command Code `3919521a3615e2a7ee72cefd811e3572e3bf4124` FINALPASS tips as separate no-fast-forward merges `4d7f0ada9b09b339d22afaa750010dd318d8e4d3` and `82da0b3b4f3013811480d2a5562b152e62d3ca6f`. Registered both with the existing supervisor and documented four Kiro plus six Command Code metrics. The gated same-bus test publishes real Hardware, synthetic PresentMon, Codex and all seven accepted non-Codex providers twice while Cursor acquisition is pending; bounded cancellation/drain remains. Supervisor failure isolation covers all eight registered IDs including Codex. Removed the ZCode test-only forwarding wrapper and reused its existing Harness directly. Reviewed extraction criteria: no shared primitive justified because no pair of accepted providers demonstrates both identical semantics and harmful duplication. No provider production edits beyond the two accepted tips or new dependency; two Kiro PowerShell test-request deadlines were set to the existing 10-second bound after the hosted full suite exceeded the prior 5-second fixture timeout.

Verification: serial Release solution build passed with 0 warnings/errors. Core 39/39 and Integration 10/10 passed on the unchanged staged source; after the Kiro test-only deadline correction, direct MTP Telemetry 295/295 and the two isolated Kiro cases 2/2 passed. Focused coexistence 1/1 and supervisor failure isolation 8/8 passed. The `5a91da4` Actions Build and Test run passed build/Core, then failed two Telemetry Kiro ACP tests at the 5-second test-only deadline (293/295); Repository Quality passed and CodeQL was skipped by policy. The local `dotnet test` bridge returned exit 5 / zero tests for all three projects; guarded direct test-app runs executed the suites successfully. Graphify refreshed to 2,734 nodes / 8,002 edges (no clustering). Configured repository path hygiene passed for 117 tracked files; targeted added-diff privacy patterns and `git diff --check` passed. These scoped checks are not a comprehensive secret scan. Copilot's actual production-provider comparison is `LIVE_VERIFIED` for available remaining-percent only; absent per-bucket resets remain unavailable and renewal is unknown. Kiro and Command Code same-account comparison is `LIVE_NOT_AVAILABLE_ON_THIS_HOST`; no account absence elsewhere or renewal is inferred. Kiro empirical renewal remains deferred. Corrected-head hosted CI is pending; PR #47 stays draft. Local `main` remains `3f5ca74a26edf88a02e6bda05f759ee3c74032cd`; no main merge or M3/16-provider completion claim.

Integration policy: stage FINALPASS provider commits in small verified groups with CI between groups under the concurrency limit above. The final integration PR remains draft for Advisor review and required CI before any main merge; the integration owner does not merge main. A slow/absent/auth-failed provider must not stop hardware, gaming, Codex or other AI integrations.

Wave acceptance: every subtask ends as accepted implementation or evidence-backed blocked/deferred; at least several additional quota integrations must be accepted, not merely classified. Integrated providers pass Release build/tests, repository hygiene, identity/auth/failure tests, bounded live comparison against the same account/workspace where practical, and Advisor review.

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
