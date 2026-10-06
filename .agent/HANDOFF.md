# CERITH CER-M4-003 — WidgetHost CPU isolation handoff — 2026-10-06

Verdict: `PERFORMANCE_CAUSE_ISOLATED_FIX_DEFERRED`. Base `0c6c68447cefbb12978f681f6d393024189b5025`; branch `codex/CER-M4-003-widgethost-cpu`. All decisive runs opened WidgetHost on `DISPLAY2` with a fresh Release process, 8-second warm-up and 12-second process CPU-time interval. The canonical experimental matrix, counts, median/ranges and uncertainty are in `docs/PERFORMANCE.md`; raw logs and disposable variants stayed outside the repository.

Connected receive/presentation had the same 1.038% median of one core. Applying WinUI row values raised the median to 1.946% without Automation name mutation and 2.206% for the full instrumented path; untouched canonical visible/minimized was 2.074%/1.556%. Over five full-path runs, 408 snapshots produced 408 presentations but only 112 row applications. CPU alone applied 103 of 103 snapshots, while FPS and Codex each applied only 5 unchanged-state transitions over 205 and 102 snapshots. Simple selective-property rewrites were inconsistent across five pairs, so no product UI change was made.

With the service absent, one versus three subscriptions measured 1.425% versus 3.484% median. The current three loops spent 261.5 aggregate seconds in pending `ConnectAsync` across about 102 seconds of process lifetime. Five-second and ten-second outer retry variants lowered CPU but could delay recovery; shorter connect timeouts had overlapping CPU ranges and less margin for healthy handshakes. No retry policy or IPC change was made. One live DISPLAY2 stop/restart check observed all rows become Unavailable and later restore CPU Fresh/Measured, gaming Stopped/Unknown and Codex Error/route_unsupported/Unknown. Normal close passed. No post-fix before/after claim exists.

Local validation: canonical Release build passed with 0 warnings/errors; Core/Telemetry/Integration suites passed 39/397/22 (458/458, zero skipped), and focused MetricPresentation passed 12/12. Diff, tracked filename hygiene and added-line private-data scan passed. No product code, test, package or project file changed. Hosted PR checks must be read on the exact pushed head; a CodeQL skip is not a pass.

Next action: independently review this documentation PR and decide the next M4 correction's acceptable recovery latency and bounded UI/connection approach; any shared connection design needs Advisor review. M4 remains ACTIVE / INCOMPLETE. M3 remains DONE; M5/M7, taskbar/tray, OSD and budget revision remain outside this task.

## Historical — CER-M4-002 handoff

## Current decision

CER-M4-002 is complete with verdict `PERFORMANCE_ACTION_REQUIRED`. The bounded study used Release builds, fresh processes, 8-second warm-up, 12-second process-time intervals, repeated runs and one-second Working Set/Private Bytes samples. CPU is percent of one logical/core-equivalent CPU; this host has 16 logical processors. Canonical numbers, ranges and protocol are in `docs/PERFORMANCE.md`.

The measured native floor is substantial: minimal WinUI visible is 0.000% CPU / 108.20 MiB Working Set / 132.35 MiB private, and minimizing it does not materially change those values. The current M4-001 XAML with subscriptions disabled is also 0.000% CPU and adds only about 7.6 MiB Working Set / 2.0 MiB private above the minimal control. Stable current WidgetHost live is 1.102% / 125.89 / 137.89; live minimized is 0.908% / 125.96 / 138.04; service absent is 1.946% / 119.13 / 135.70. The `< 50 MB` and `well below 100 MB` aspirations are below the bare framework floor on this host and were not changed by this task.

The immediate pre-M4 `fc2f85e...` WidgetHost, isolated outside the current tree and connected to the same live service, measured 0.454% CPU / 128.53 MiB Working Set / 142.40 MiB private across eight pooled runs. A second three-run check again measured current above pre-M4 (1.167% vs 0.389% median CPU). M4-001 therefore reduced memory modestly but introduced or exposed a reproducible connected telemetry/update-path CPU regression. The static M4 UI itself measures 0.000%, while service absence is even more expensive than live operation, so the next bounded M4 task should isolate the current snapshot/presentation update cost and connect/timeout/retry cost before optimizing either.

Live UI Automation during the study verified CPU `Fresh/Measured`, gaming `Stopped/Unknown` with no selected PID, and Codex quota `Error · route_unsupported/Unknown`. No fake provider or telemetry was used. No product optimization, dependency/project change, M5, M7, taskbar/tray or gaming OSD work occurred.

## Historical — CER-M4-001 completed slice

CER-M4-001 is the first bounded implementation slice of M4. It replaces the old WidgetHost diagnostic text wall with one compact native WinUI surface driven by the existing `MetricSnapshot -> Named Pipe -> MetricId` consumer path. The reusable row is provider-independent; the parent supplies only MetricId, label, numeric format and unit. No new project, runtime dependency, IPC discovery channel, provider-specific UI, M5 editor/config schema, M7/history, gaming OSD or tray/taskbar work was introduced.

Representative live MetricIds are `cpu.total.usage`, `game.fps.display` and `ai.codex.quota.primary.remaining`. Fresh/Stale display real values; Unavailable/Stopped/Error display an em dash; Unlimited displays its semantic state rather than a fake percentage; real zero remains zero; confidence is always textually exposed. UI Automation names include label, value, state and confidence. Dark/light resources and the restrained edge/state-rail motif implement the provisional M4 visual direction without importing Design Lab assets or adding a custom font.

Local acceptance is green: Release build 0 warnings/errors, full suite 458/458, focused presentation tests 12/12. Native runtime proof covered live CPU, expected stopped gaming state with no selected target, truthful Codex `route_unsupported`, service interruption to Unavailable/No snapshot, automatic reconnect, and clean window close. Final observation at 96 DPI measured 1.172% of one core / 127.25 MiB visible and 0.781% / 127.66 MiB minimized. These memory measurements exceed the aspirational targets and do not establish the approximately-zero idle CPU target. CER-M4-002 above now provides the baseline attribution; its connected/reconnect CPU follow-up remains required before M4 exit. The 420x340 default and 360x340 minimum keep all three rows visible at 96 DPI. Both attached monitors are 96 DPI, so physical higher-DPI behavior is not claimed as verified.

The bounded GPT-5.6 Sol High visual review via CodexGPT -> ChatGPT Web returned PASS; its optional note about the technical header label was addressed in the final polish pass. Hostile engineering review found no blocking provider/lifecycle/reconnect/accessibility/scope issue. An attempted GPT-6.1 Sol Medium read-only engineering review produced only the runtime usage-limit error and therefore contributes no verdict. Implementation/review head `87d447cd30665b2c272acc4a2a104871ab63aa16` passed Build and Test (37467483707) and Repository Quality (37467484170); CodeQL was SKIPPED (37467483710), not a security PASS. CER-M4-001 is complete at its bounded slice scope; M4 remains ACTIVE / INCOMPLETE and M7 remains NOT STARTED.

## Historical — CER-M3-006 handoff

## Canonical post-merge state

M3 — AI Telemetry & Quotas is DONE after the literal CER-M3-006 exit audit. CER-M3-005 is DONE via PR #50, integrated at 8dfd5f56aa63355bd0d952d774a5d84af750afa2. Its one accepted passive/local M3.B collector is OpenCode 1.17.9, routed through MetricSnapshot → MetricBus → Named Pipe → MetricId consumer.

The 42-target activity catalog has one IMPLEMENTED_ACCEPTED row (OpenCode), 27 EVIDENCE_BLOCKED rows and 14 DEFERRED_AFTER_M3 rows. Every remaining target has a specific acquisition/evidence, Windows, field, privacy, semantic-risk, reason and revisit-evidence record in docs/AI_TELEMETRY.md. Deferred/blocked does not mean supported.

M3.A remains 11 accepted quota integrations, counting Codex. Existing quota blockers (Claude, Gemini CLI / Code Assist, Grok Build) and deferrals (Qoder International, Kimi, Devin) are unchanged and separate from activity status. See docs/PROVIDERS_AI_QUOTAS.md.

The accepted OpenCode host probe had no eligible assistant. Live activity remains LIVE_NOT_AVAILABLE_ON_THIS_HOST / activity_absent; source-version qualification was not observed on this host. Coexistence receipts are synthetic/local: the same-bus 4/4 cases pair unavailable OpenCode activity with hardware, gaming, Codex, Cursor and Antigravity, including a synthetic pending Cursor request timeout; provider-failure isolation is 12/12 with opencode-activity included. These do not claim live provider activity.

## Boundary

M4 is NEXT / NOT STARTED; M7 is NOT STARTED. No M4 surfaces, M7 history, public plugin SDK, remote/multi-PC capability or unrelated hardware/gaming work is included. No product code or dependency changes were required for CER-M3-006. Detailed review/CI/PR receipts are in .agent/TASKS.md and .agent/WORKLOG.md.
## Historical — CER-M3-004 merge handoff — 2026-10-03

PR #47 is MERGED by user-authorized SQUASH as `55a43fb02055e25ab60e18e0406aafd508ffd502`. Immediately before merge, head `03d9907b491d27bf0d23b67f2953cff17a4465f9`, base `72b43b268b4992d0b0ea45b6e29067d10450274f`, 110 ahead/0 behind, mergeability and required exact-head checks were verified. Merge used expected-head protection. Reviewed candidate and canonical squash trees are identical (`94a7132ed2e6bce0805757366030e6efa12c327b`); reviewed code/tests/dependencies are unchanged.

Review chain: initial `0b9daad6534623d256b79bbd30f39387405793d2` — FIXES REQUIRED (3 HIGH, 6 MEDIUM); first correction `84fbf84d1065ba859605cdd2dc83019a1b51fe69` — FIXES STILL REQUIRED, only residual ZCode finding 5; final correction `03d9907b491d27bf0d23b67f2953cff17a4465f9` — independent PASS — ZCODE CORRECTION VERIFIED, READY FOR ADVISOR MERGE DECISION. Findings 1–9 are closed within their reviewed scopes; findings 1–4 and 6–9 were preserved by the final bounded review.

CER-M3-004 implementation wave is DONE, and PR #48 records the completed post-merge reconciliation. PR #47 is integrated as squash `55a43fb02055e25ab60e18e0406aafd508ffd502`; the reviewed candidate and squash trees are identical. M3 remains ACTIVE / INCOMPLETE because accepted passive/local activity acquisition remains an exit requirement. M0/M1/M2 and CER-M3-001/002/003 remain DONE.

Canonical accepted quota integrations (11): Codex, Cursor, Antigravity quota, ZCode, GitHub Copilot, OpenCode Go, Kiro, Command Code, Qoder CN, Ark Agent Plan, Ark Coding Plan. EVIDENCE_BLOCKED: Claude; Gemini CLI / Code Assist; Grok Build. DEFERRED_AFTER_M3_QUOTA_WAVE: Qoder International; Kimi; Devin. Existing auth/live limits remain; no provider was reopened or upgraded during merge.

Candidate receipt: Release 0 warnings/errors, Core 39/39, Telemetry 384/384, Integration 10/10 (433/433, zero skipped); ZCode 28/28 in the independent review, coexistence 1/1 and supervisor isolation 11/11 in the candidate receipt. Build and Test SUCCESS 37135720609; Repository Quality SUCCESS 37135720604; CodeQL SKIPPED 37135720599, not a security PASS. These are candidate receipts, not fresh local post-merge tests.

Next action: use canonical `main` with this reconciliation, then plan the first bounded M3.B passive/local AI activity collector. M3.B remains NEXT / NOT STARTED. M4 must not start; M7 has not started. Preserve historical branches and worktrees. CodeQL remains SKIPPED, not a security PASS. No M3.B/M4/M7, real Grok, credential operation, branch/worktree deletion or new implementation occurred.

## Historical pre-merge handoff — preserved

# CERITH CER-M3-004 FINAL ZCODE CLOSURE HANDOFF

## Current review state — 2026-10-03

Verdict: `ZCODE_CORRECTION_COMPLETE_READY_FOR_FINAL_REREVIEW`. This is not an independent PASS. The fresh bounded review of `0b9daad6534623d256b79bbd30f39387405793d2` → `84fbf84d1065ba859605cdd2dc83019a1b51fe69` returned `FIXES STILL REQUIRED — NO-GO FOR MERGE`, with findings 1–4 and 6–9 closed and finding 5 partially unresolved. Preserve that result; the current correction addresses only finding 5. The next reviewer must inspect the ZCode-only delta from `84fbf84d1065ba859605cdd2dc83019a1b51fe69` to the exact pushed head, its regressions, the review/handoff reconciliation and exact-head CI.

## Residual finding and correction

ZCode's `ApplyAuthReadLocked` cleared A's retry state when auth became invalid but retained `_nextPollTimestamp`. After `_activeAuth` became null, B's ready observation did not satisfy the changed-owner/credential conditions, so a direct poll remained blocked by A's one-hour deadline and the sleeping loop was not awakened. The fix resets admission and wakes only on verified-owner → unverifiable and no-verified-owner → verified-owner transitions. It clears owner-specific retry/rejection state for B, preserves same-owner A→A backoff and avoids repeated wakes during continuous invalid state. Changes are limited to `src/Cerith.TelemetryService/Providers/AI/ZCode/ZCodeQuotaProvider.cs` and its existing focused test file.

Regressions: `UnavailableAuthBetweenOwnersClearsLongRetryAdmissionForDirectPoll` and `RunWakesForNewOwnerAfterUnavailableAuthClearsSleepingOwnersRetry` failed against the unfixed base and pass after the correction. `UnavailableAuthCancelsPendingOldOwnerPublication` also passes, holding a bus publication across the invalid-auth transition and verifying it cannot publish A's quota. Existing direct A→B, running-loop A→B, same-owner retry, owner-work cancellation and bounded shutdown tests remain green.

## Validation and repository state

Release build: 0 warnings/errors. Core 39/39; Telemetry 384/384; Integration 10/10; total 433/433; zero skipped. ZCode focused suite 28/28; shared-bus coexistence 1/1; supervisor failure isolation 11/11. `git diff --check` PASS; filename/path hygiene PASS (137 paths); bounded sensitive-added-diff scan PASS (zero credential/private-path matches); dependency/project diff EMPTY. The exact pushed SHA, ahead/behind count, Build and Test, Repository Quality, CodeQL disposition and run IDs are recorded in the current PR #47 description. PR #47 remains OPEN / DRAFT and mergeable; canonical `main` remains untouched. M3 remains ACTIVE / INCOMPLETE.

Provider statuses are unchanged: accepted/staged Codex, Cursor, Antigravity quota, ZCode, GitHub Copilot, OpenCode Go, Kiro, Command Code, Qoder CN, Ark Agent Plan and Ark Coding Plan; EVIDENCE_BLOCKED Claude, Gemini CLI / Code Assist and Grok Build; DEFERRED_AFTER_M3_QUOTA_WAVE Qoder International, Kimi and Devin. No subagents, Sol, Astra or Luna Max were used. THIS IS NOT THE FINAL INDEPENDENT RE-REVIEW.

## Exact next action

Run one final fresh read-only independent re-review limited to the ZCode delta from 84fbf84d1065ba859605cdd2dc83019a1b51fe69 to <FINAL_HEAD>, its regressions, documentation reconciliation and exact-head CI. Preserve the eight previously closed findings unless this ZCode-only delta changes their basis.

## Preserved previous Grok / Shadow closure record

## 1. VERDICT

MISSION_CANDIDATE. Execution closure is complete within the verified scope; CER-M3-004 remains IN PROGRESS and M3 remains ACTIVE / INCOMPLETE pending fresh independent review/Advisor decision. No unsupported provider was imported to manufacture completion.

## 2. CANONICAL STATE

MAIN: 72b43b268b4992d0b0ea45b6e29067d10450274f (origin/main; local main remains untouched at 3f5ca74a26edf88a02e6bda05f759ee3c74032cd).
PR: #47, OPEN / DRAFT.
INTEGRATION BRANCH: codex/CER-M3-004-integration.
Starting head for this closure: f1531e2559185e2e13ff62459711c8864a6e6c02 (105 ahead / 0 behind main).
Exact final head/ahead/behind must be pinned after the closure commit with git rev-parse HEAD / git rev-list --left-right --count origin/main...HEAD and compared with gh pr view 47 --json headRefOid,isDraft,state. This file cannot embed the hash of its own commit. Reuse the existing integration worktree; no worktree was created or replaced.

## 3. WORK COMPLETED THIS UNIT

PROVIDER/TASK: Grok deployed billing qualification.
PREVIOUS STATUS / FINAL STATUS: EVIDENCE_BLOCKED / EVIDENCE_BLOCKED, with a narrower proven credential-class contract and a concrete failed owner probe.
BRANCH / START SHA / END SHA: grok-build / d51fba3184b606c36b141e2f9c232c85ef03d0ff / unchanged.
CHANGES: one repeatable tools/check_grok_billing_protocol.py manual development check; canonical evidence and stale source links corrected. Both explicitly pinned official binary versions reject API-key/untrusted-issuer fixtures before loopback billing access; neither observes replacement owner within three seconds. Both commands exit 1, not PASS. Earlier less-isolated owner observation is retained as disagreement. Official stdio refresh plus child termination is not proven safe; --leader can create/evict writers. No token export, model/session request, real auth operation or new integration commit.
LIVE CLASSIFICATION: EVIDENCE_BLOCKED; no compatible-account live claim.
LIMITATIONS: synthetic cases do not prove valid subscription responses, expiry/rotation, all account races, persistence or deployed source equivalence.

PROVIDER/TASK: Claude remaining contract.
PREVIOUS / FINAL STATUS: EVIDENCE_BLOCKED / EVIDENCE_BLOCKED.
BRANCH / START / END: claude / 51afc1cc91b4b461c93715e9c324c918a1cbc6fc / unchanged.
CHANGES: confirmed official SDK prior-approval boundary and model-only setup-token scope; passive statusline lacks owner/backend observation binding. Host subscription absence is not an eligibility restriction. Existing valid-session reader and 18 deterministic tests remain preserved, not production registered. No inference, login, secret access or import.

PROVIDER/TASK: integration closure.
PREVIOUS / FINAL STATUS: MISSION_CANDIDATE / MISSION_CANDIDATE awaiting independent review. Provider runtime/registrations/dependencies remain unchanged. Scope/ownership, research matrix, review packet, tools documentation, state and handoff reconciled; historical records retained. Source detail is canonical in docs/AI_QUOTA_REFERENCE_MATRIX.md, not copied here as a second specification.

## 4. PROVIDER MATRIX

ACCEPTED/STAGED: Codex, Cursor, Antigravity quota, ZCode, GitHub Copilot, OpenCode Go, Kiro, Command Code, Qoder CN, Ark Agent Plan, Ark Coding Plan (11).
EVIDENCE_BLOCKED: Claude; distinct enterprise Gemini CLI / Code Assist; Grok Build.
HUMAN_GATE_PENDING: no new login/MFA/consent gate attempted.
DEFERRED_AFTER_M3_QUOTA_WAVE: Qoder International, Kimi, Devin. Exact branches/tips/blockers remain in TASKS and the review packet. Ark Coding Plan is already staged, not deferred.

## 5. DOCUMENTATION RECONCILIATION

Files: tools/README.md, docs/AI_QUOTA_REFERENCE_MATRIX.md, .agent/TASKS.md, STATE.md, HANDOFF.md, WORKLOG.md and CER-M3-004_REVIEW_PACKET.md.
Current authorization now covers bounded implementation; after two credible Grok avenues failed to satisfy owner/writer safety, no speculative provider delta was added. Claude coverage remains host-independent. No 17/17 acceptance gate, permanent model policy or architecture decision was introduced. Historical records preserved: YES.

## 6. VALIDATION

Fresh local closure validation: Release build 0 warnings/errors; Core 39/39; Telemetry 356/356; Integration 10/10 (405/405), zero skipped; runtime source unchanged. Previous focused checks: Claude 18/18, Gemini 25/25, Grok 12/12, Ark Agent 20/20, Ark Coding 19 cases; coexistence 1/1; supervisor isolation 11/11. Hosted checkpoint 113afc6 also confirms 39/356/10 tests; the final documentation head must be pinned separately for review.
Manual Grok qualification: 1.0.34 exit 1; 1.0.46 exit 1. Credential rejection passed in four synthetic cases; bounded owner observation failed in all six, including both admitted-class mock-acquisition cases. Not a provider acceptance gate pass.
Python syntax, wrong-hash and disabled-optimization guards pass; diff-check and 136-file filename hygiene pass. Bounded added-diff secret/personal-path patterns: zero matches (not comprehensive). Production src/tests/project/dependency diff from f1531e2 is empty; no new runtime dependency. The full Telemetry suite covers the retained coexistence/supervisor cases; focused counts above are the preceding receipts.
Graph: incremental tool AST refresh, 3,292 nodes / 8,795 edges / 153 communities; existing corpus/edges preserved, no missing files/self-loops. No semantic/doc/model refresh; historical community names were retained.

## 7. CI

Prior exact f1531e2: Build and Test SUCCESS (37012137265), Repository Quality SUCCESS (37012137315), CodeQL SKIPPED (37012137241). Closure code/tool checkpoint 113afc6 passed hosted build/quality (37043424898 / 37043424940); CodeQL skipped (37043424841), not a passing security scan. The final receipt-only head is resolved from Git/PR and must have its own green checks.

## 8. SECURITY / PRIVACY

Credential material committed: NO. Personal/private artifacts committed: NO. New auth ownership: NONE. Only isolated synthetic fixture auth files and their processes were created/drained; neither official user credential storage nor accounts were changed. The manual tool emits fixed booleans/version labels, not secret or account payloads. No browser cookies, paid workload, reset credit or model request.

## 9. SHADOW AUDIT

Completed in the preceding marathon; not repeated here. Canonical evidence matrix remains in docs/REFERENCE_PROJECTS.md. Short soaks, all-Ignored mutation report, non-differential runner, weak drift/privacy acceptance and broad performance/zero-leak claims were downgraded. Useful fixture/adversarial/restart ideas remain USE_WITH_REVIEW; no architecture port or M4/M7 implementation.

## 10. OPEN RISKS

Claude: approved renewable subscription output with owner/observation binding remains missing. Gemini enterprise: supported product-appropriate quota output and Windows writer/persistence contract remain missing. Grok: failed bounded owner observation, safe official refresh/termination and existing-owner attachment not established. Existing staged-provider live limits, queued MetricBus transition boundary and source/version risks remain in the review packet. Synthetic/manual checks do not replace same-account validation.

## 11. INDEPENDENT REVIEW READINESS

YES after exact-head hosted checks are green. Packet: .agent/CER-M3-004_REVIEW_PACKET.md. Pin the final PR head independently; the owner did not perform an independent review. PR stays DRAFT; candidate status does not authorize merge or advance M3.

## 12. EXACT NEXT ACTION

Run a fresh independent hostile read-only review on the final green PR #47 head using the prepared packet.

## Preserved earlier marathon record

# Handoff — CER-M3-004 autonomous marathon

Status: MISSION_CANDIDATE; task remains IN PROGRESS pending fresh independent review/Advisor decision; M3 ACTIVE / INCOMPLETE. Sole execution/shared-file owner is GPT-6.1 Sol Medium, native Codex, concurrency one. Historical review receipts remain historical; this owner's self-review is not independent approval. Keep PR #47 DRAFT and do not merge main.

Starting truth: fetched origin/main 72b43b268b4992d0b0ea45b6e29067d10450274f; local main 3f5ca74a26edf88a02e6bda05f759ee3c74032cd remains untouched. Existing integration head 9b3056e54d3a876e05bc19119637575640d5ba62 was 100 ahead / 0 behind origin/main and had green Build and Test / Repository Quality; CodeQL SKIPPED. All 18 worktrees were clean and no competing active writer was found. Twelve newer commits than the user's expected snapshot were preserved, including Ark Coding Plan integration and Qoder International's justified revert.

Completed: current first-party Claude / Gemini CLI-Code Assist / Grok Build blocker rechecks, exact versions and source pins in docs/AI_QUOTA_REFERENCE_MATRIX.md. Claude remains blocked on an approved quiet renewable subscription bridge; enterprise Gemini remains distinct but blocked on product-appropriate supported acquisition and safe persistence; Grok's newer no-API-key source policy narrows its provenance blocker but is not bound to the installed binary. Preserved focused branches pass 18/18, 25/25 and 12/12 respectively; none was imported. Qoder International, Kimi and Devin remain deferred; Ark Coding Plan was already accepted/staged and was not reverted.

Bounded production correction: Ark Agent Plan acquisition wait now responds to shutdown even if its dependency ignores cancellation. New regression failed before the change and passes afterward; focused provider tests 20/20. No quota semantics, credentials or dependencies changed. Fresh Release build 0 warnings/errors; Core 39/39, Telemetry 356/356, Integration 10/10 (405 total); coexistence 1/1; supervisor isolation 11/11; Ark Coding Plan combined focused coverage 19 cases. Diff-check and 135-file filename hygiene pass. Bounded added-diff pattern scan: zero known-token/bearer/private-key/personal-path matches; eight synthetic .invalid emails, zero others. This is not a comprehensive secret scan. Dependency diff from main is empty.

Graph: preserved existing corpus with incremental replacement of two changed C# files: 3,289 nodes / 8,793 edges / 160 communities; zero missing source files, dangling endpoints or self-loops. No external model. Extractor coalesces the shared Ark namespace; graph navigation does not prove source semantics. Documentation semantics were not re-extracted.

Current validated code checkpoint: 96eefc59b704e6d6a34b683c7be9cfcf9f0a704f. Hosted Build and Test SUCCESS (37005658373), Repository Quality SUCCESS (37005658310), CodeQL SKIPPED (37005658376). The complete fresh-context packet is prepared in .agent/CER-M3-004_REVIEW_PACKET.md; it records exact lineage, sources, metrics, owner/cache/shutdown boundaries, preserved blockers, commands and review questions. Bounded hostile self-review found and corrected the Ark shutdown gap; all staged source trees were compared with their preserved tips. This is self-review, not independent approval.

Candidate packet head 991187e605657256f0cff8f8d14af3998fbd8989 passed Build and Test (37006483453) and Repository Quality (37006483463); CodeQL SKIPPED (37006483519). Draft PR body now describes actual wave/dispositions/tests/limitations. Previously local-only Claude, Gemini, Grok, Kimi and Devin branches were pushed unchanged after scoped privacy/filename checks. A signed official Grok 1.0.46 artifact/version-only check retained its alpha-versus-stable channel and source-binding uncertainty; no ACP/auth command.

Shadow audit completed read-only: canonical evidence matrix and key SHA-256 receipts are in docs/REFERENCE_PROJECTS.md. Located mutation report has 44/44 Ignored mutants; short tests do not demonstrate required long soaks. Differential runner counts records without either parser; drift fallback can report unchanged without a valid pin; privacy harness excludes its reports root. Official Codex/Claude sample refs fail. Python restart is an in-process simulation; C# child-kill/checkpoint source is a useful test design awaiting reproduction. M4 dummy-window and M7 same-process/benchmark artifacts do not prove production runtime, long multiprocess soak or zero leaks. WSL/zero-idle-CPU producer retractions retained. No research harness executed, artifact changed or architecture ported; dirty rehearsal preserved.

Exact next action: run a fresh independent hostile review of the final PR head using .agent/CER-M3-004_REVIEW_PACKET.md. Final documentary/audit commit requires green exact-head Build and Test / Repository Quality; CodeQL skipped is not a security pass. PR remains DRAFT and main untouched. No auth/account operation, model request, reset credit or live claim upgrade. Current validated code remains 96eefc5; obtain exact final documentary head with git rev-parse HEAD / gh pr view 47 --json headRefOid. No independent review has occurred.

Follow-up requested 2026-10-02: three preserved quota-reference clones and six additional GitHub monitors inspected at exact revisions; canonical findings in docs/AI_QUOTA_REFERENCE_MATRIX.md#github-acquisition-comparison-follow-up--2026-10-02. Contract explicitly requires Claude coverage for compatible subscribers independently of host access. Official Grok FAQ proves shared weekly pool; pinned x.ai/billing auth gate rejects ApiKey/WebLogin and avoids bearer export, narrowing the prior assessment. Passive official billing logs are a real source but omit owner identity. Next acquisition investigation should prove deployed version and owner binding for x.ai/billing; do not reuse bearer-export ambiguity, invent zero, mutate credentials or port reference architecture. Provider branches/code/tests/dependencies unchanged; source-only research is not live acceptance. New documentary head requires exact-head CI, while the prior final code/documentary head 43aef2e passed hosted build/quality (37008881062/37008881026), CodeQL skipped (37008881045). PR remains draft; future independent review must pin the new head.

---

# Handoff — CER-M3-003 AI telemetry expansion plan and foundation contract

## PR #45 first adversarial review

GPT-6.1 Sol High reviewed head `5b0f6e45ecd230189033afa304e2a7300e1f48fe` and returned **FIXES REQUIRED / NO-GO for that head; GO for the product direction**.

Applied corrections:
- one current runtime publication path: source → MetricSnapshot → MetricBus → IPC → MetricId consumer; any richer M7 history contract requires a later explicit decision;
- D-024 now governs the quota matrix's future expansion and the pre-Codex "No AI provider" statement is explicitly historical;
- the 42-tool catalog is classified as heterogeneous reference coverage, including remote, WSL, shared-parser and multi-source cases;
- Gemini CLI / Code Assist and Grok Build are named precisely; OpenCode Go's preferred API-key route is recorded;
- M3 now has a real minimum-delivery gate so blocked/deferred classification cannot close the entire breadth target;
- CER-M3-002 Codex retains its accepted non-renewing auth policy;
- AI Limits is independent-adapter corroboration, not an independent upstream oracle.

No product/test/dependency code changed. New-head CI and a bounded re-review remain before merge.

Status: ACTIVE / DOCS ONLY. Branch `docs/CER-M3-003-ai-telemetry-direction` is based on canonical main `3f5ca74a26edf88a02e6bda05f759ee3c74032cd`. No product code, tests, packages, credentials or provider runtime behavior changed.

The durable direction is now:

- keep CER-M3-002 Codex as the completed first vertical slice;
- supersede only D-023's future sequential Claude→Antigravity ordering via D-024;
- use the current 17-provider usage-limit catalog and 42-tool local-activity catalog as evidence-driven breadth targets;
- treat TokenTracker, Gengchou and AI Limits as complementary references rather than one architecture source;
- prefer zero-config local detection/session reuse, with one-time explicit setup only when necessary and no normal startup reconfiguration;
- keep provider acquisition isolated from MetricId-driven rendering;
- allow parallel provider work only under disjoint write ownership, with one integration owner for shared files;
- place compact floating/taskbar/tray/detail surfaces in M4 and history/dashboard analytics in M7, while M3 owns acquisition.

Reference snapshots recorded by this task:

- TokenTracker `a8520617ece3f15a07c342e2c7909eb311234522`;
- Gengchou `4d02b9d1f85e806c92c4defc8e8c4c822467b6f3`;
- AI Limits `a42bbd043070538ff5c4161b7eda952720173345`.

Important reference roles:

- TokenTracker: 42-tool local acquisition, 17 usage-limit integrations, zero-config patterns, dashboard/widgets and cost/activity lessons.
- Gengchou: taskbar/floating/tray/detail surfaces, compact headline/attention semantics, Windows shell resilience and a documented warning about provider identity being spread across too many files.
- AI Limits: adaptive Compact/Medium/Expanded density, taskbar/tray fallback, single renderer/layout/theme discipline and measured lightweight runtime.

License boundary remains explicit: TokenTracker and Gengchou are MIT at the pinned references and still require provenance/notice review before focused code adaptation; AI Limits is GPL-3.0-or-later and remains research-only absent a separate license decision.

Next boundary: finish review/CI for this documentation PR. Do not start provider implementation from this branch. After integration, define the minimum implementation foundation and provider-specific tasks from the audited matrix; do not create speculative framework layers or let multiple writers share paths.

---

# Handoff — CER-M3-002 Codex quota vertical slice

Status: CER-M3-002 DONE. Advisor PASS; PR #43 was squash-merged as `3c3db6dd1102ec3ed2d95b6234e5550df08ab34a`. Approved head `89091db4e0fe90415ef059c1d29a5d0e7488ff42` and integrated product tree match. M3 remains ACTIVE / INCOMPLETE; Claude is NEXT / NOT STARTED and Antigravity follows. The effective Codex configuration was verified on 2026-09-30 as the supported standard route, with no `openai_base_url` or custom provider override.

Sanitized live validation passed against the signed-in official usage UI and the installed official Codex app-server v0.159.0. The UI comparison covered all six metrics at code head `13116b9e224b6d740bed02aa3a1fa123452af094`; durations matched the 5-hour/weekly buckets, remaining values matched whole-percent UI rounding, and authoritative `reset_at` matched the UI countdown precision. A later bounded capture on pre-documentation HEAD `bbd69ff34c5af6d1b08aa01bb28a9ea9a76ce8b8` collected six Fresh/Measured Cerith snapshots from one acquisition and made one `account/rateLimits/read` call for the same selected account. The primary and secondary used/remaining directions matched within one percentage point for official integer rounding; durations matched (5 hours and 7 days), and both reset timestamps matched within one second. The call set `excludeResetCreditDetails=true` and omitted `supportsLunaReserve`; no reset-credit consumption method was called. Auth and config files were unchanged across the call.

The public Codex adapters were checked at current branch heads: [Gengchou `4d02b9d`](https://github.com/ynjmxn/gengchou/blob/4d02b9d1f85e806c92c4defc8e8c4c822467b6f3/src/poller.rs), [TokenTracker `daa6c55`](https://github.com/xiufengsun/TokenTracker/blob/daa6c55099d34458ff773f6c1edd777f16fb7693/src/lib/usage-limits.js), and [AI Limits `a42bbd0`](https://github.com/napxlexn/ailimits/blob/a42bbd043070538ff5c4161b7eda952720173345/src/providers/codex.rs). Each is an independently implemented adapter, but each direct ordinary-quota read uses the same undocumented `https://chatgpt.com/backend-api/wham/usage` upstream; the official app-server also resolves to that endpoint. Thus the comparison is implementation-level corroboration, not an independent upstream oracle. Gengchou reads existing Codex auth (including its Windows credential/WSL fallback paths), disables automatic CLI refresh, preserves primary/secondary inputs, sorts by known duration, drops non-finite percentages and treats zero duration as unknown without rounding. TokenTracker reads the Codex auth bundle and may refresh/persist expired credentials before its GET; it rounds/clamps `used_percent`, classifies 18,000-second and 604,800-second windows by duration, can reorder slots, and retains a positional fallback. AI Limits tries its own stored usage token then `~/.codex/auth.json`, does not rotate the CLI token, rounds/clamps used percentage, parses `reset_at` as Unix seconds, and hard-labels primary as session and secondary as weekly while ignoring duration. These differences affect auth side effects and normalization, not upstream independence. No monitor software was installed or launched.

Both live comparisons leave the reset discrepancy visible: `reset_after_seconds` disagrees with `reset_at`, so Cerith reports `source_disagreement`; the UI and app-server independently corroborate `reset_at` to their displayed/one-second precision. No values were averaged. No raw quota values, payloads, identifiers, tokens, screenshots or account details were written to the repository or PR. No credentials/auth/config were modified; no login/logout, paid model workload or reset-credit consumption occurred. The initial service run while correcting the local pipe reader may have issued multiple ordinary read-only GETs at its 60-second cadence; its exact count was not observable. Advisor approved CER-M3-002, and PR #43 was squash-merged.

## Implementation

TelemetryService registers and publishes the six ordinary Codex quota metrics through the existing MetricRegistry, MetricBus and Named Pipe contracts. The provider reads only standard ChatGPT file auth, uses the fixed HTTPS usage endpoint, rejects custom/managed routing and redirects, bounds auth/body reads, checks response and local identity, and never refreshes credentials. Cacheable failures now put the current sanitized acquisition code on Stale snapshots while preserving cached value and ObservedAt, including when the cached reset had `source_disagreement`. Deterministic offline, timeout, HTTP 5xx and HTTP 429 tests cover this behavior; the 429 case also verifies long Retry-After suppression. Parser, owner-generation cancellation, cache expiry, slow-subscriber behavior and valueless Stopped shutdown publication have offline coverage. Core, IPC and WidgetHost contracts were not changed. MetricBus v1 cannot retract a snapshot already queued before an owner switch; the blocked publish and late HTTP result are canceled, and subscribers needing a strict account boundary must reset.

## Verification

Release solution build PASS (0 warnings/errors). Core 39/39, Telemetry 58/58 and Integration 10/10 PASS; the four focused cache-failure cases pass. `git diff --check` and repository filename hygiene PASS (83 tracked paths checked). Independent read-only security/lifecycle review PASS; both Advisor cache-failure findings are closed. On the integrated squash commit `3c3db6dd1102ec3ed2d95b6234e5550df08ab34a`, Build and Test and Repository Quality passed; CodeQL was skipped under D-013. Graphify code and document graph refreshed to 838 nodes/2,034 edges/81 communities; integrity check found no missing, dangling or self-loop edges. Effective-route verification passed before live reads. The official UI and app-server live comparisons passed for durations, used/remaining direction and rounding, and authoritative reset timestamps. The shared-upstream limitation among the three public adapters and official app-server is recorded above; no independent upstream oracle was found. A repeat UI observation followed ordinary Codex task activity; no paid workload or reset credit was generated.

## Remaining acceptance

CER-M3-002 is DONE after Advisor PASS and PR #43 squash merge. Preserve the recorded shared-upstream limitation, `source_disagreement`, and MetricBus v1 account-boundary limitation. M3 remains ACTIVE / INCOMPLETE. CER-M3-003 Claude is NEXT / NOT STARTED; Antigravity follows after Claude. No Claude implementation was started during this reconciliation.

---

# Previous handoff — CER-M3-001 quota research and source contract

Status: CER-M3-001 DONE (research/contract). M0/M1/M2 DONE; M3 ACTIVE / INCOMPLETE; CER-M3-002 NOT STARTED. No AI provider implementation, source file, package, UI, M4 or legacy quota code was added.

## Canonical outputs

- `docs/AI_QUOTA_REFERENCE_MATRIX.md`: three-provider evidence/provenance matrix, pinned official/external sources, discrepancies and unknowns; no private payloads.
- `docs/PROVIDERS_AI_QUOTAS.md`: metadata audit, Codex six-ID minimum, read-only identity/routing boundary, cache/state rules and deterministic/live acceptance plan.
- D-023 in `.agent/DECISIONS.md`: **Codex → Claude → Antigravity** implementation order. CER-M3-002 will implement only the ordinary Codex quota vertical slice, after a new task claim; widgets remain MetricId-only.
- `docs/REFERENCE_PROJECTS.md` disambiguates OpenUsage and points to the single revision/evidence matrix. `docs/METRICS.md` removes misleading speculative Codex session/Antigravity weekly examples. Roadmap records actual M3 progress.

## Evidence and verification

Bootstrap: clean `main == origin/main` at `730ae6c5d518c812213c631eab4129c4bcbea1f6`, with routing PR #41 integrated. Sole write owner on `codex/CER-M3-001-quota-research`; two GPT-5.6 Sol High Web provider specialists were read-only. Independent GPT-6.1 Sol Medium native semantic/security review and final consolidated source/order review PASS, including verified Antigravity changelog 1.1.11.

A single bounded Windows Codex usage GET returned HTTP 200 and verified used/duration/reset shape. Access token and selected account header stayed in memory; no token renewal, login/logout, auth copy or payload file. That observation is not a provider implementation or full UI cross-validation. Claude and Antigravity had no authenticated local probe. Antigravity CLI changelog 1.1.11 explicitly supports quota JSON in print mode without an agent turn; installed-version/schema/account/auth side effects remain unprobed. No Antigravity command was launched against user authentication.

Release build PASS (0 warnings/errors); Core 39/39, Telemetry 26/26, Integration 9/9 PASS. These are the existing suites required by PR CI, not new AI conformance proof. `git diff --check` and tracked-file hygiene PASS during preparation. No structural source changes: Graphify was queried for MetricSnapshot/State/Confidence navigation, no graph refresh required. No probe/temp payload/auth files were generated.

## Next boundary and retained risks

CER-M3-001 closes at the single research PR boundary under required build/hygiene checks and squash protection. Verify clean integrated main and stop; CER-M3-002 remains NEXT / NOT STARTED. Its official UI/status live comparison is mandatory and not satisfied by the research GET. Undocumented Codex backend stability, file-auth-only standard routing coverage, later Claude identity/scope/event receiver and Antigravity machine-readable source/project/group/auth behavior are explicit limits, not hidden implementation claims. Previous hardware/native/dependency debts remain in STATE and canonical docs.

---

# Previous handoff — CER-M2-003 selected game/session summary and M2 exit

Status: CER-M2-003 DONE; M2 — Gaming MVP DONE; M3 — AI Quotas MVP NEXT / NOT STARTED. Product PR #38 was squash-merged as `5364ec7824f424e0206c9ebdecf826e16484d60d`; approved head `561450d391234c98294fdd5fc6e99336575a13d1` and integrated tree match. The reconciliation is docs/state only.

## Contract and implementation

The session is the lifetime of the single explicitly selected `--pid` process. `game.session.application` publishes the Windows `Process.ProcessName` basename through the existing MetricBus, Named Pipe v1 and MetricId-only WidgetHost route. The same opened `Process` identity is retained across PresentMon retries; the implementation does not reopen a numeric PID after the original process exits. A failed initial handle acquisition remains terminal until TelemetryService restarts. The name has no path or `.exe` extension.

While the selected process is alive and its name is readable, the label stays `Fresh`/`Measured` through no-frame periods and PresentMon service loss. Frame metrics keep their own `Unavailable`/`Error` states and values remain absent. Target exit stops all four gaming metrics. M2 does not implement automatic game detection, launcher-to-renderer inference or process-tree grouping, and does not publish PID, path, command line, window title, package identity or process tree. Exact state and UI semantics are in D-022 and `docs/METRICS.md`.

No dependency, new source file, Core or IPC change, or provider abstraction was added.

## Verification and runtime evidence

- Release build PASS with 0 warnings/errors; Core 39/39, Telemetry 26/26, Integration 9/9 PASS (74 total). PR #38 `build` and `hygiene` PASS; CodeQL SKIPPED under D-013. `git diff --check` and repository hygiene PASS.
- Fresh Sol Medium hostile review of approved product head `561450d391234c98294fdd5fc6e99336575a13d1`, after the service-loss probe, PASS with no actionable findings. The `code-review-checklist` review found no actionable issue.
- On a controlled D3D11 target on the verified secondary display, WidgetHost displayed the selected application label with live FPS, frame time and P1. Pausing left the label Fresh while frame metrics became Unavailable; target exit stopped all four metrics. A second selected target displayed its own label.
- During the 2026-09-29 service-loss probe, 160 Named Pipe snapshots kept `game.session.application` Fresh with the same `M2TestProducerNext` identity while PresentMon was stopped and restarted. FPS, frame time and P1 became valueless Error and then Unavailable while stopped. FPS and frame time recovered Fresh after service startup; P1 recovered after its own warm-up. TelemetryService remained alive and the target selection did not change.
- A 15-second local sample observed TelemetryService at 0.52% of one logical CPU and a steady 84.3 MiB working set; pipe publication was about 500 ms. These are one-host observations, not a before/after claim.
- Cleanup PASS: the controlled target and observer exited, PresentMon was restored to Running/Automatic, and the temporary probe directory was absent.

## M2 exit audit

| Roadmap scope | Status | Evidence |
| --- | --- | --- |
| PresentMon, FPS and frame time | DONE | CER-M2-001; product PR #31 |
| Displayed FPS P1 | DONE | CER-M2-002; product PR #36; D-021 |
| Selected game/session summary | DONE | CER-M2-003; product PR #38; service-loss continuity and recovery observed |
| M2 — Gaming MVP | DONE | All committed M2 scope implemented, tested and runtime-validated |
| M3 — AI Quotas MVP | NEXT / NOT STARTED | No M3 work started |

Limitations remain: selection is explicit by PID, not automatic game detection; no commercial-game, anti-cheat, launcher/multi-process, or cross-vendor matrix is claimed. Later gaming metrics remain deferred by `docs/ROADMAP.md`.

---

# Previous handoff — CER-M2-002 Displayed FPS P1 rolling window

Status: CER-M2-002 DONE. Product PR #36 merged by squash as `0a7ce2ee87aa27e65efea804fa4c0767faa17cbf`; approved product head `cf49247d53808f3cb0653708316c8cc732450c2a` and integrated tree match. M0, M1, CER-M2-001 and CER-M2-002 are DONE; M2 remains INCOMPLETE and CER-M2-003 is NEXT / NOT STARTED.

## Implementation and semantics

The existing PresentMon boundary publishes `game.fps.display.p01` using a separate `PM_STAT_PERCENTILE_01` query over a requested trailing 10 seconds. P1 warm-up starts at the first positive displayed-change observation and resets after more than two seconds without recent display evidence or on capture/target restart. It keeps its own state alongside the one-second FPS and frame-time metrics. The exact reciprocal-percentile semantics, zero-sample treatment and 4096-frame retention limit are canonical in D-021 and `docs/METRICS.md`.

No package, new source file, Core/IPC contract, generic statistics abstraction or provider-specific WidgetHost binding was introduced. The P1 descriptor and MetricId reuse the existing provider/registry/bus/pipe path.

## Verification and real evidence

- Release restore/build PASS with 0 warnings/errors; Core 39/39, Telemetry 22/22 and Integration 9/9 PASS (70 total). Local repository hygiene and `git diff --check` PASS. PR #36 `build` and `hygiene` PASS; CodeQL SKIPPED under D-013. Fresh hostile Sol High review PASS, with no unresolved findings.
- Visible D3D11 producer runs were positioned on the verified secondary display and did not appear on the primary display. The solution test suites are headless.
- In the baseline producer mode, P1 became `Fresh`/`Measured`; a 15-second sample showed 31 fresh P1 observations with 499–501 ms gaps (median about 500 ms). P1 values during the run were 40.41–43.44 FPS. This is a local controlled workload result, not a claim that its presents were all displayed.
- In the controlled high-drop mode, PresentMon P1 was 42.397098 FPS. The independent oracle received 640 positive `PM_METRIC_BETWEEN_DISPLAY_CHANGE` rows; because this query output contained no zero rows, it reconstructed 1121 zero-sample equivalents from the separate 10-second `PM_METRIC_DROPPED_FRAMES` fraction (0.63657), then calculated raw P99 interval 23.58652 ms / P1 42.397098 FPS. Cerith's adjacent Fresh/Measured P1 samples were 42.30–42.47 FPS. The reconstruction is explicitly an estimate based on the drop fraction, not direct observation of omitted zero rows.
- Stopping `PresentMonSharedService` produced P1 `Error/presentmon_api_failure`, then valueless `Unavailable`; after the service returned to Running/Automatic, P1 recovered to Fresh without restarting TelemetryService. The first recovery sample followed the required display-evidence warm-up. No-frame/warm-up windows remained valueless `Unavailable`, and target exit published valueless `Stopped`.
- The final 15-second performance sample used 0.250 CPU seconds for TelemetryService (1.67% of one logical core). Working set changed from 63.6 to 62.0 MiB. These are one-host observations; there was no controlled before/after comparison or reliable isolated PresentMon-service overhead measurement.

## Remaining limits

The 4096-frame ring can retain less than the requested 10 seconds above roughly 410 frames/s; PresentMon v2.6.0 does not implement `PM_STAT_COUNT` for dynamic queries. Validation used controlled D3D11 producers on this machine, not a commercial game, anti-cheat environment, multi-vendor GPU matrix or D3D12 workload. Do not claim those cases validated or claim M2 complete.

---

# Previous handoff — CER-M2-001 PresentMon displayed telemetry vertical slice

Status: CER-M2-001 DONE. Advisor PASS; product PR #31 merged by squash as `9902cc19ff0f2911ab4ee0ece2337d12b1d9edcf`. The approved product head was `086db95e29124900854e29d2b70caa16b3f43889`, based on `89ddd69c13affd76b3b6085a25753bb7b83b0a3f`; the integrated product tree matches that approved head. M0 and M1 are DONE; M2 remains INCOMPLETE and CER-M2-002 is NEXT / NOT STARTED.

## Implementation boundary

TelemetryService accepts one explicit positive `--pid` and publishes only `game.fps.display` and `game.frametime.display`. The PresentMon adapter owns the native session/tracking/query handles and verifies the API version, metric metadata, data layouts and sample freshness before returning readings; frame rows use the blob size returned by `pmRegisterFrameQuery`. It publishes through existing MetricRegistry, MetricBus and Named Pipe protocol v1. WidgetHost consumes only the MetricIds. No Core contract, package, generic provider framework, logging of process metadata or solution-wide architecture change was introduced.

Cerith carries only the official x64 `PresentMonAPI2Loader.dll` and the adjacent upstream MIT license. It does not redistribute PresentMon middleware or the Service; the official Service installs its matching API. The loader and its license are copied into normal build and publish output. A 32-bit TelemetryService cannot load it and receives a sanitized `presentmon_architecture` error while hardware publication remains independent.

## Official release and local runtime

Official PresentMon v2.6.0 (released 2026-09-21) MSI SHA-256: `0cb43d2622356c1e277a77f840887f9000cea4e2d4236c0850922fc204c6f820`; Authenticode: Valid, Intel Corporation. The installed API reported version 3.4.0. The SDK loader is version 2.6.0.0, x64, SHA-256 `BA5863F35687F0A1E3A98C5E22535313CB5A37E7DDAFAF8861477BF390FF43BE`; its Intel signature is valid. Service was restored to Running/Automatic after fault injection. These probes were run from a non-elevated user process; the Windows service itself runs under its service account.

## Runtime evidence

- A temporary D3D11 window presented real frames under an explicit PID. At a roughly 165 Hz cap, repeated pipe samples changed around 158–165 FPS and 6.0–6.3 ms. Both IDs were `Fresh`/`Measured`; UI Automation read `164.9 FPS` and `6.07 ms` while the pipe client was non-elevated.
- On the final Release run (`run-final-valid`), one PID and one TelemetryService capture showed `Fresh` before the second chain. Across the transition interval (10:58:01.053–10:58:22.552 UTC), each metric had 22 `Error/multiple_swap_chains` snapshots (last at 10:58:21.556), 18 `Unavailable`, and 4 `Fresh`; three `Fresh` samples were earlier in the interval (10:58:01.053, 10:58:11.052 and 10:58:14.053). The first post-removal recovery sample was 10:58:22.552. Because the query window is one second, that boundary can lag the swap-chain destruction; these counts do not mean all four `Fresh` samples occurred while both chains were present. From that first recovery sample through 10:58:50.552, both metrics stayed `Fresh` (57 snapshots each); target exit produced `Stopped` at 10:58:51.057. No capture restart or retarget occurred.
- An instrumented confirmation run (`run-confirm-count-semantics`, one target PID and one capture throughout) logged successful `Present` calls for both chains in all 19 one-second intervals of phase `two`: each chain recorded 75–82 calls per interval. In the matching 20-second pipe interval, the two metrics produced 78 `Fresh`, 2 `Unavailable`, and 0 `Error` snapshots combined. The preceding one-chain phase had 88 `Unavailable` snapshots; after destroying chain 1, `one-again` had 8 `Fresh` and 112 `Unavailable` snapshots before `Stopped`. This repeat does not demonstrate a clean 1→2→1 telemetry transition and does not establish whether absent display samples came from event loss/occlusion or query-window timing.
- PresentMon v2.6.0's output count is the number of dynamic-query result rows for swap chains with frame samples in that poll's time window; the implementation returns one fallback row when the window has no matching chains. It is not a live-object inventory. Thus successful D3D `Present` calls do not guarantee that both chains contribute rows or displayed-frame samples in each API poll. The logs show per-poll variation; the API source does not specify a stable active-chain count or identify the cause of these gaps. See the pinned [`FrameMetricsSource`](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/FrameMetricsSource.cpp#L347-L377) and [`DynamicQuery`](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/DynamicQuery.cpp#L470-L500) implementations.
- A no-frame target produced no zero FPS/frame-time sample. The API probe observed zero dynamic values and no display-change frames; Cerith withheld values until valid recent frame evidence existed.
- Closing the target changed both IDs to valueless `Stopped`/`Unknown` on the next approximately 500 ms poll. Polls whose API result count exceeded one produced `Error`/`Unknown`, code `multiple_swap_chains`, with neither row selected or combined; observed states varied as detailed above.
- Stopping `PresentMonSharedService` left TelemetryService alive and publishing `cpu.total.usage` as `Fresh`/`Measured`; the two gaming IDs became valueless `Unavailable`/`Unknown`. Restarting the official service restored fresh gaming samples without restarting TelemetryService or WidgetHost.
- Startup discovered CPU 1, GPU 1, Storage 0 and Network 13. During PresentMon loss, hardware publication continued. This is one local machine and one D3D11 probe; no commercial game, anti-cheat environment, cross-vendor hardware or controlled capture-overhead comparison was tested.
- Over 12 seconds with a target active, TelemetryService used 0.188 seconds CPU (1.56% of one logical core by process CPU-time delta) and working set increased from 79.6 to 80.5 MiB. PresentMonService working set was 27.9 MiB at both endpoints; its CPU delta was below the available sampling resolution. These are local spot measurements, not a performance guarantee or a before/after target-impact test.
- Forty-five pipe FPS samples had 496 ms minimum, 500 ms median and 502 ms maximum gaps. API sample recency is separately checked against QPC and display-change evidence.

## Pre-mortem outcome

| Risk | Result |
| --- | --- |
| Service/API version mismatch | Installed API v3.4.0 was introspected; unsupported version/metadata paths become sanitized Error, never guessed queries. |
| Loader/process architecture mismatch | Official x64 loader verified and runtime-tested; 32-bit mismatch has a sanitized failure path. No 32-bit runtime was installed for a separate execution test. |
| Service or capability absent | Service stop produced `Unavailable`; hardware stayed live; service restart recovered without restarting Cerith. |
| Permission/elevation assumption | Non-elevated client, TelemetryService, pipe client and WidgetHost completed the real frame flow. |
| No-frame window becomes zero | No-frame probe returned no value; only finite positive samples with recent frame evidence become Fresh. |
| Stale Fresh after target/service loss | Target close produced valueless Stopped; Service stop produced valueless Unavailable. |
| Target exits during query | Process state is checked around capture queries; deterministic stop tests and real close probe did not retain stale values. The narrow exit/query race is handled as a valueless stopped/error result. |
| Multiple swap chains | One final-build run recovered `Fresh` after the second chain was destroyed; a separately instrumented repeat logged both chains presenting but did not reproduce that state sequence. The API count is per-poll rows, not active objects; no chain is selected or averaged. |
| Cancellation or native-handle leak | Query/tracking/session destruction is centralized; temporary native API probes returned successful cleanup statuses and all probe processes exited. Long-duration leak soak was not run. |
| Anti-cheat or ETW limitations | No commercial game/anti-cheat test was run; behavior is claimed only for the tested D3D11 workload and official Service/API. |
| Process metadata privacy | Explicit PID is not logged; no process name, executable path, title, command line or raw provider payload is emitted. |
| Provider-framework overdesign | Existing registry, bus and pipe are reused; native seam is limited to the concrete PresentMon ABI. |
| M1 hardware regression | Core, Telemetry and Integration suites passed; concurrent runtime kept CPU and other hardware publication alive through PresentMon stop/restart. |
| Dependency or provenance error | MSI and loader hashes/signatures were verified; upstream MIT license is physically present beside the unchanged loader in Release build and publish output; no package or middleware was added. |

## Verification and handoff

For the Advisor correction, `dotnet restore Cerith.sln` and Release solution build passed with 0 warnings/errors; Core 39/39, Telemetry 16/16 and Integration 9/9 passed. Release `dotnet publish` passed. Physical inspection confirmed both loader and license in normal build and publish outputs; loader SHA-256 matches the checked-in official binary. `git diff --check` and the configured tracked-file hygiene check passed. One same-PID run observed `Fresh` → varying `Error`/`Unavailable`/`Fresh` polls during the dual-chain transition → sustained `Fresh` after returning to one chain; the instrumented repeat verified both chains called `Present` continuously but did not reproduce the same telemetry sequence. PresentMon API probe reported 3.4.0; the installed service was Running/Automatic. Graphify query shows the existing capture type and field-context references to non-primitive types, but the member identifiers `_ambiguousSwapChains` and `_frameRowSize` are absent from `graph.json`; these scalar fields add no graph node or relationship, so no incremental refresh was warranted. Product PR #31: Build and Test PASS, Repository Quality PASS, CodeQL SKIPPED under D-013; squash `9902cc19ff0f2911ab4ee0ece2337d12b1d9edcf`. The merged tree matches approved head `086db95e29124900854e29d2b70caa16b3f43889`. No product changes were made during reconciliation. M2 remains INCOMPLETE; CER-M2-002 is NEXT / NOT STARTED.

Canonical semantics, architecture, performance/provenance and decision records are in `docs/METRICS.md`, `docs/ARCHITECTURE.md`, `docs/PERFORMANCE.md`, `docs/REFERENCE_PROJECTS.md` and `.agent/DECISIONS.md`. CER-M2-001 is DONE; M2 is still INCOMPLETE, and CER-M2-002 has not started.

# Previous handoff — CER-M1-004 hardware/provider diagnostics and M1 exit

Status: CER-M1-004 DONE; M1 — Hardware MVP DONE; M2 NEXT/NOT STARTED. Advisor PASS final for diagnostics and M1 exit. Approved product head `1028767c0e38c06061707eeed694e54fc6b15f6a` on base `17bdeb61b65edc28db01df5a2935f2e8fd109747` passed PR #29 Build and Test and Repository Quality (CodeQL SKIPPED under policy), then was squash-merged into `main` at `59ecb648a9f714376578fc16cdaa446571bd4874`. The integrated tree matches the approved head. This handoff records the post-merge state; no product code changed after that head.

## Missing diagnostics and design

M0 already printed service ready/stopped and WidgetHost Connecting/value/unavailable. Hardware snapshots already distinguished Fresh, Unavailable and Error with sanitized codes, but there was no discovery summary, no visible error detail in WidgetHost, and a silent stream could leave an old value displayed indefinitely. TelemetryService now prints only discovered CPU/GPU/Storage/Network counts, a per-sample state/error-code summary when it changes, and a warning that LHM internal failures and sensor truth are unverified. Zero discovered means only that LHM did not discover the category; cause is unknown. The ready line follows a successful first publish. Unexpected fatal failures print a generic code instead of a raw exception.

`MetricSnapshot` remains the per-metric authority. The internal `HardwareDiagnosticSummary` in the existing provider file aggregates only one publish tick, then is discarded; it is justified by the console diagnostic boundary and deterministic classification test. No second metric cache, MetricId, diagnostic protocol, new file, package, logging framework or ControlCenter feature. WidgetHost renders known sanitized error codes and marks a disconnected or silent subscription unavailable, then reconnects. Normal readings remain Fresh/Measured; a real zero stays zero.

## Pre-mortem and evidence

| Risk | Result |
| --- | --- |
| False healthy verdict; diagnostic proves itself but not data | No healthy verdict. Counts describe publication outcomes; measured UI values do not prove sensor truth. |
| Capability absent vs sensor unavailable vs known failure vs real zero | Discovery counts, Unavailable/Error/Fresh snapshots and one deterministic test keep these distinct. Zero discovered has unknown cause. |
| Prior value appears valid after runtime loss | Receive timeout and reconnect; real service stop/restart demo showed unavailable then fresh values without restarting WidgetHost. |
| LHM internal failures / unobserved bad values | Still undetectable; explicitly stated in console and canonical metrics docs. |
| Hardware absent, elevation, topology | Storage 0 non-elevated and 4 elevated on this host. Elevation enabled a positive path here; non-elevated support remains unproven. Startup indices, removal and re-add limits remain. |
| Lifecycle/leaks | One LHM Computer per process; WidgetHost connections dispose on retry. Repeated LHM open/close not verified; upstream StorageGroup subscription remains a known limit. |
| Privacy / provider identifiers | Console has category counts and validated codes only; no serial, MAC, IP, path, GUID, raw exception or provider identifier. |
| Duplicate state / framework / dependency | No durable diagnostic state, new dependency or generic framework. |
| Existing CPU/RAM/GPU/VRAM/fan/Storage/Network regression | Release build and all canonical suites PASS; non-elevated demo exercised available groups. Elevated Storage probe exercised the existing positive path without changing code. |

## Verification

Release solution build PASS, 0 warnings/errors. Core 39/39, Telemetry 9/9, Integration 9/9 PASS. The new test checks Fresh measured zero, Unavailable without value and Error with sanitized code. It does not simulate real hardware. `git diff --check` PASS; no configured preflight script. Repository Quality tracked-file filename patterns and final diff/security review PASS. No new dependency or generated/probe file committed.

Non-elevated runtime: LHM discovered CPU 1, GPU 1, Storage 0, Network 13. The first summary was Fresh 44, Unavailable 0, Error 6, with `invalid_percentage` six times (network sensors that returned invalid utilization). UI Automation observed CPU/RAM/GPU/VRAM/fan and Network values, Storage unavailable after the first-snapshot timeout, all displayed values unavailable after service loss, and fresh values again after service restart without restarting WidgetHost. The earlier PTY Ctrl+C returned wrapper code 1 without a shutdown line; this was not an application exit-code test. WidgetHost closed through its window handle.

Gate A PASS, elevated only: a temporary, uncommitted .NET 10 client started the unchanged service and read the existing local pipe. Non-elevated: ready, Storage discovered 0. Elevated via UAC: ready, Storage discovered 4; `storage.0.usage`, `storage.0.total.bytes` and `storage.0.free.bytes` were all `Fresh`/`Measured` doubles without error codes. Usage was about 33.8%; total was positive; free was nonnegative and below total; usage differed from `100 × (1 - free/total)` by under 0.000002 percentage points. Existing Storage `Data` conversion is ×1,000,000,000 bytes. No read/write throughput MetricIds were reinstated. Missing/error values remain absent under the Core contract; zero discovery generated no Storage snapshots. This proves neither non-elevated Storage support nor independent sensor truth. No serial, path, firmware, SMART raw data or provider identifier was retained.

Gate B PASS: a separate isolated service process reached ready. A probe attached only to its console (two members: probe and service), sent CTRL+C, observed `TelemetryService stopped.` in captured stdout and exit code 0 within ten seconds; stderr was empty. This reproduces graceful cancellation and pipe/publisher completion. The earlier PTY result was a wrapper limitation, not a reproduced lifecycle regression. No `Program.cs` change was needed. NVIDIA/Intel/multi-GPU, forced refresh exception, hotplug and repeated LHM open/close remain untested.

Temporary probe directories remain outside the repository: automatic approval review rejected both directory and individual-file cleanup. They contain only temporary probe code, state/count/value results and sanitized service output, with no provider identifiers or raw hardware payloads. No probe file was staged or committed.

## M1 exit audit against docs/ROADMAP.md

| Requirement | Existing implementation and evidence | Limit / exit |
| --- | --- | --- |
| LibreHardwareMonitor | v0.9.6 integrated in one provider; real non-elevated discovery and samples | Backend internal errors can be hidden; non-blocking for baseline |
| CPU | `cpu.total.usage`; observed measured UI reading | Single local CPU; non-blocking |
| GPU | Indexed LHM GPU samples; observed AMD UI reading | Other vendors/multi-GPU and persistent identity unverified; non-blocking |
| RAM | `GlobalMemoryStatusEx`; observed measured UI reading | Physical-memory API only; non-blocking |
| VRAM | GPU memory sensor mapping; observed measured UI reading | Same vendor/identity limits; non-blocking |
| Basic fans | GPU fan sensor mapping; observed Fresh/Measured 0 RPM | Sensor truth and other fans unverified; non-blocking |
| Storage/network basics | Storage source mapping and elevated `storage.0` usage/total/free `Fresh`/`Measured` pipe readings; Network real readings here and positive traffic in CER-M1-003 | Non-elevated Storage discovery 0 on this host; read/write throughput deferred. Positive path and graceful absence satisfy M1 scope with privilege limit explicit |
| Diagnostics | Discovery counts, snapshot states/codes, UI timeout/reconnect; real degraded demo and deterministic test | Cannot independently validate hardware truth; non-blocking |

Conclusion after Advisor PASS final and PR #29 merge: `M1 — Hardware MVP: DONE` with both evidence gates PASS and the listed limits explicit. M2 is next and has not started.

The product PR changed the provider, TelemetryService Program, WidgetHost MainWindow, one existing Telemetry test, canonical docs and operational memory. This reconciliation changes only `.agent/STATE.md`, `.agent/TASKS.md`, `.agent/HANDOFF.md`, `.agent/WORKLOG.md` and `docs/ROADMAP.md`. New tracked files/dependencies: none. No code structure changed, so Graphify refresh is unnecessary.

Closure self-review expanded the manual probe to an admitted xAI-issuer OIDC fixture: both binaries acquired only the loopback mock quota, but neither observed replacement owner in that case either. Six owner observations fail; this is still synthetic protocol evidence, not a valid subscription/renewal/live comparison. Hosted code/tool checkpoint 82da884 passed Build and Test (37042555549, 0 warnings/errors, 39/356/10 tests), Repository Quality (37042555581); CodeQL skipped (37042555555). The three-case tool checkpoint subsequently passed its own exact-head checks, recorded below.

Hosted code/tool checkpoint 113afc648c03b36c04c888b0c867d34f284ca958 (107 ahead / 0 behind main): Build and Test SUCCESS 37043424898, 0 warnings/errors, Core 39/39, Telemetry 356/356, Integration 10/10; Repository Quality SUCCESS 37043424940; CodeQL SKIPPED 37043424841, not a security pass. The final receipt-only documentation commit preserves that exact runtime/tool tree. Pin the current PR head and its checks for fresh review.
