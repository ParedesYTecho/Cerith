# Handoff — CER-M3-004 quota wave integration

Status: CER-M3-004 IN PROGRESS; M3 remains ACTIVE / INCOMPLETE. The preserved integration branch includes Cursor `24ca7027e204fb1361cb79f573ddf132c9490994`, Antigravity `8889e4b34da1445c2bafe08093c11dc8c63fd1e3`, ZCode `0a8dca3555e6c523ab7c40f4ba1e4303c9b13df9`, Copilot `6c59c3437f8b23692bab8d338c67c3ca0941827f` (no-fast-forward merge `71fdb49`), and OpenCode Go `d102d7d301d523acbf4c107c09e8ccd04229b157` (no-fast-forward merge `1b49128`). All accepted provider commit histories are preserved. PR #47 remains draft; canonical `main` is untouched.

Ownership: Dewey One is the sole shared-file writer on GPT-6 Luna Max, actual `turn_context` verified at 2026-10-01T17:46:59.513Z. External GPT-6.1 Sol Medium remains the read-only MID/FINAL reviewer (last runtime verification 11:45:23.649Z). Kiro is awaiting FINAL review; Command Code handoff `3cac1e552b5e5a87441e1b85cee81a3e0a0b6cbd` is awaiting Sol MID review. Grok Build, Claude, Gemini, and Kimi remain BLOCKED at preserved tips. Exact current details are in TASKS; routing policy stays only in `MODEL_ROUTING.md`.

Integration and verification: both new providers use the existing `KeepServiceAliveAfterProviderExitAsync` seam; 13 Copilot and 6 OpenCode Go IDs and semantics are in canonical metric/provider docs. Gated coexistence exercises Hardware, PresentMon, Codex, Antigravity, Copilot, and OpenCode Go for two rounds while a Cursor request is pending, then bounds cancellation/drain. Release build passed with 0 warnings/errors; Core 39/39 and Integration 10/10 passed. Telemetry's 211 tests pass in class-isolated MTP runs; the combined module printed passing case results but the process exits on an unhandled `System.Threading.PeriodicTimer.Dispose` finalizer `NullReferenceException`, so the aggregate process gate remains open. GitHub Actions run [`36906898017`](https://github.com/ParedesYTecho/Cerith/actions/runs/36906898017) at `114b855` also passed Build and Core contracts and reported 176/176 Telemetry cases before the same process failure; IPC integration was skipped and hygiene passed. Graphify refreshed to 2,251 nodes / 5,774 edges / 88 communities. Configured obvious-private-artifact path check passed for 107 tracked files; targeted diff privacy patterns and `git diff --check` passed. These checks are scoped, not a comprehensive secret scan.

Acceptance and limits: Copilot credential renewal class is unknown and no silent-renewal claim is made. OpenCode Go supports the official static API-key owner; SETUP/LIVE validation remains pending, with no MFA encountered. Antigravity's sanitized local-RPC observation establishes four recognized buckets and stable before/after status identity only; parser/snapshot, installed-version, backend-cache and UI validation remain open. Cursor auth is DEFERRED/UNRESOLVED and its live UI/quota comparison is NOT RUN. ZCode real-account quota comparison remains unclaimed. Grok Build is BLOCKED for `auth_provenance_unavailable`; preserve its code/tests and do not import. No common framework, dependency, or shared primitive was added.

Canonical refs: local `main` remains `3f5ca74a26edf88a02e6bda05f759ee3c74032cd`; `origin/main` is `72b43b268b4992d0b0ea45b6e29067d10450274f` (mission base). Neither was moved or merged. Next: keep PR #47 draft, wait for explicit provider FINALPASS gates, and do not merge main or mark the 16-provider mission/M3 complete.

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
