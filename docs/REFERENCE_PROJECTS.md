# External Reference Projects and Research Provenance

Status: Canonical research provenance.
Purpose: preserve which external projects materially influenced Cerith and why, without making any of them Cerith's architectural base or automatic source of truth.

## Core rule

Cerith learns from external projects but keeps its own architecture.

A project listed here may contribute product ideas, observed edge cases, provider-source clues, UX patterns or implementation comparisons. That does **not** mean:

- its architecture should be copied;
- its code should be imported;
- its telemetry semantics are automatically correct;
- its API behavior is authoritative;
- its license permits copying code into Cerith.

When implementation-specific behavior matters, revalidate it against official sources and current upstream code. Record the upstream revision/commit when the exact behavior becomes a durable dependency or design input.

## Product and telemetry references

### LibreHardwareMonitor

Repository: https://github.com/LibreHardwareMonitor/LibreHardwareMonitor

Role in Cerith:
- primary initial hardware acquisition backend for M1;
- source for CPU/GPU/RAM/VRAM/temperature/fan/etc. sensor observations where supported.

Boundary:
- LibreHardwareMonitor belongs in TelemetryService/provider code, not Core or generic widgets;
- Cerith normalizes readings into its own MetricId/MetricSnapshot contracts;
- sensor names/mappings must be verified rather than assumed universal.

### PresentMon

Repository: https://github.com/GameTechDev/PresentMon

Role in Cerith:
- official Service/API source for CER-M2-001 displayed FPS and displayed frame time and the CER-M2-002 displayed FPS P1 statistic;
- pinned integration contract: [v2.6.0 release](https://github.com/GameTechDev/PresentMon/releases/tag/v2.6.0), [Service/SDK guidance](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/README-Service.md) and tagged [PresentMonAPI.h](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonAPI2/PresentMonAPI.h).
- P1 semantic provenance: tagged [`MetricsCalculatorDisplay.cpp`](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/CommonUtilities/mc/MetricsCalculatorDisplay.cpp) emits zero intervals for non-displayed frames; [`DynamicMetric.h`](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/DynamicMetric.h) retains finite zeros in percentile input; tagged [`DynamicStat.cpp`](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/DynamicStat.cpp) reverses rank for reciprocal metrics, interpolates the selected raw interval percentile, and reciprocates. Tagged [`Middleware.cpp`](https://github.com/GameTechDev/PresentMon/blob/v2.6.0/IntelPresentMon/PresentMonMiddleware/Middleware.cpp) sets a 4096-frame-metric per-swap-chain ring capacity. The API rejects `PM_STAT_COUNT` for dynamic queries. Cerith's controlled high-drop validation used a separate `PM_METRIC_DROPPED_FRAMES` measurement to reconstruct the zero-sample population absent from `pmConsumeFrames` rows before comparing raw-oracle P99/P1.

Boundary:
- generic Cerith widgets bind to MetricId, never PresentMon-specific types;
- use the official Service/API and loader contract; do not ship the service middleware DLL;
- the API header defines the source metrics, average statistic, PID tracking, dynamic and frame queries; local runtime behavior is recorded separately in CER-M2-001 evidence;
- derived gaming claims require measured definitions and validation; this slice does not establish anti-cheat compatibility or universal capture coverage.

### MSI Afterburner / RTSS / HWiNFO / Rainmeter

Role in Cerith:
- product/capability/UX references;
- examples of monitoring, overlays, layouts, sensor surfaces and customization.

Boundary:
- they are not Cerith's codebase or architectural template;
- their existence does not justify provider-specific widgets or closed metric contracts.

## AI quota research references

The original Cerith design explicitly studied AI quota monitors. These repositories are research/corroboration sources, not authorities. Official provider behavior remains higher priority.

### Gengchou

Repository: https://github.com/ynjmxn/gengchou

Pinned reference revision: `4d02b9d1f85e806c92c4defc8e8c4c822467b6f3`.

Primary Cerith role: **Windows compact-surface and shell-integration reference**, plus provider-acquisition corroboration.

High-value lessons:

- taskbar widget, compact floating monitor, provider tray icons and detail popup expose the same quota state at different density levels;
- compact surfaces select a stable headline window but allow an urgent/high-usage window to take over, keeping detail available on hover/click;
- semantic placement survives Explorer/taskbar rebuilds, monitor changes and RDP/lock transitions better than raw saved screen coordinates;
- exact-DPI/theme rendering and deterministic dump modes make visual regressions reviewable instead of relying on hand screenshots;
- provider detection spans Windows files, Credential Manager, existing Desktop/CLI sessions and already-running WSL distributions without waking WSL just to poll;
- stale/auth/rate-limit states keep last-known values visible without pretending they are fresh;
- `compact_view.rs` is a useful example of keeping compact product semantics independent of HWND/DPI/drawing code.

Important negative lesson: `docs/ADDING_A_PROVIDER.md` documents that provider identity is spread across many files and adding a provider can become a tree-wide diff. Cerith should not repeat that coupling; provider acquisition must remain isolated from surface rendering and WidgetHost must remain MetricId-driven.

License: MIT at the pinned revision. Code adaptation is possible only after explicit provenance/attribution review; the project is still not Cerith's architectural base.

### AI Limits

Repository: https://github.com/napxlexn/ailimits

Pinned reference revision: `a42bbd043070538ff5c4161b7eda952720173345`.

Primary Cerith role: **compact native visual-system and lightweight runtime reference**, plus corroboration through an independently implemented adapter. Shared-upstream limitations must be recorded; a second client hitting the same backend is not an independent quota oracle.

High-value lessons:

- Compact / Medium / Expanded detail levels preserve hierarchy while changing density live;
- horizontal/vertical bars, width tiers, provider-count growth, palette, opacity and brightness show how one semantic view can adapt without becoming multiple unrelated widgets;
- the taskbar panel and two-ring tray indicator keep useful state visible when the main floating widget is hidden;
- one renderer, one layout system and one theme source reduce visual drift;
- provider fetches run independently, last-known data survives transient failure, and stale age is visible;
- monotonic freshness logic is separated from wall-clock reset/countdown presentation;
- performance claims are backed by a reproducible audit rather than intuition.

Boundary: AI Limits is GPL-3.0-or-later. Treat implementation code as research unless a future license decision explicitly permits reuse. Visual/interaction ideas may be reinterpreted originally in Cerith's native component system; do not transplant code or branding.

### TokenTracker

Repository: https://github.com/xiufengsun/TokenTracker

Pinned current reference revision: `a8520617ece3f15a07c342e2c7909eb311234522` (v1.1.3 release commit). CER-M3-001 quota research used the earlier `daa6c55099d34458ff773f6c1edd777f16fb7693`; re-check current code before implementation-significant reuse.

Primary Cerith role: **broad AI acquisition, zero-config onboarding, local analytics and dashboard reference**.

High-value lessons:

- 42 AI tools are discovered through provider-appropriate passive readers, structured local databases/files, official hooks or provider APIs instead of forcing one universal acquisition technique;
- 17 real-time usage-limit integrations coexist with broader token/activity tracking while remaining a separate product concept;
- local-first/zero-config behavior auto-installs narrowly scoped hooks where appropriate and otherwise reads existing local state;
- the Windows/macOS/Linux product surfaces share normalized usage data across dashboard, desktop widgets and native shells;
- the dashboard demonstrates useful information architecture for trends, model breakdown, cost, activity heatmaps and project attribution;
- per-provider parsers have dedicated tests and TokenTracker's own lessons document several real normalization failures, reinforcing that token/cost semantics must be verified rather than copied blindly;
- last-good caching and provider-specific setup hints keep partial provider availability from making the whole product unusable.

Boundary: TokenTracker's broad Node/dashboard architecture is not Cerith's runtime architecture, cloud/leaderboard/skills/pet features are not automatically Cerith requirements, and activity/cost telemetry is not quota. Its license is MIT at the pinned revision, so focused code adaptation may be considered after explicit license/provenance review and semantic validation; wholesale copying is neither necessary nor desired.

### Additional quota references

CodexBar and OpenUsage remain useful independent research implementations where current and relevant.

Canonical repositories for the CER-M3-001 comparison: [CodexBar](https://github.com/steipete/CodexBar) and [OpenUsage](https://github.com/robinebers/openusage). Other repositories named OpenUsage are distinct projects and were not substituted. Exact audited revisions, file links, source classifications and disagreements for all five monitors are maintained only in [AI_QUOTA_REFERENCE_MATRIX.md](AI_QUOTA_REFERENCE_MATRIX.md). No source code was copied or dependency added; future reuse still requires a separate license review.

CER-M3-001 also uses first-party [openai/codex](https://github.com/openai/codex) for protocol/backend/auth-lifecycle evidence, official [Claude Code statusline documentation](https://code.claude.com/docs/en/statusline) for event-fed quota semantics, and official Antigravity product documentation for published UI/plan behavior. First-party source observations do not make undocumented backend endpoints supported APIs. Concrete endpoint provenance and local-probe limits belong to the matrix; normalized Cerith behavior belongs to `PROVIDERS_AI_QUOTAS.md`.

The original master brief also named **Claude Code Usage Monitor** as a research reference. The historical material available to Cerith does not preserve a sufficiently certain canonical repository URL for that name, so this document deliberately records the reference without inventing a link. If its exact provenance is re-established later, add the verified repository and revision here.

These references do not outrank official provider sources merely because they are listed in Cerith docs.

## AI quota validation hierarchy

Research provenance and validation authority are different concepts.

For a concrete provider, prefer:

1. official documented API/UI/CLI behavior where available;
2. official local application/CLI structured data;
3. stable authenticated provider behavior that can be independently verified;
4. multiple independent current implementations, including Gengchou, AI Limits, TokenTracker, CodexBar/OpenUsage as applicable;
5. Cerith's sanitized observations and golden fixtures.

A mismatch is investigated, never averaged away.

## Legacy WidgetOSD

WidgetOSD is historical evidence only.

Preserve knowledge, not implementation.

Potentially reusable only after independent validation:
- sanitized response shapes/fixtures;
- sensor mappings;
- edge cases and failure modes;
- DPI/multimonitor lessons;
- QA/security lessons.

Do not port its architecture, AI quota calculations, fallback logic, cache semantics or quota UI semantics.

## UI/UX research

The detailed design-reference workflow belongs in `docs/UI_UX.md`.

External web/component sources are interaction and visual references only. Production Cerith remains native unless a future measured, recorded decision changes that.

## Provenance rule for future research

When an external project materially shapes a durable Cerith decision, record at minimum:

- project/repository URL;
- area of Cerith it informed;
- concrete lesson/evidence;
- what remains untrusted or must be revalidated;
- exact upstream revision when implementation details matter;
- relevant license/provenance constraints if code reuse is contemplated.

Do not store private chat transcripts, raw account data or generated context dumps as project memory.

## Shadow evidence audit — 2026-10-02

Shadow is a separate research laboratory, not the production Antigravity quota provider. This audit reads the preserved `Cerith-research` artifacts; it does not import code, adopt architecture, change the implementation queue or begin M4/M7. Production contracts remain authoritative.

Method: bounded `Get-Content`/`rg` source inspection, Python JSON inventory/status counts, `Get-FileHash`, Git head/status reads and two public GitHub ref checks. Research tests/harnesses were **not rerun**: several write/delete artifacts, and their stored reports are not independently reproduced acceptance receipts. Source trees inspected: `shadow/Cerith` at `58aa0ba73269a1804743300af2755b3610528144` and `shadow-m3b/Cerith` at `fbdf1dff0c0f8545966eaaf9c94b9ca9db2827ba`, both clean. `production-rehearsal/Cerith` is based on canonical main `72b43b268b4992d0b0ea45b6e29067d10450274f` but has a modified solution and two untracked collector/test directories; all preserved untouched. Loose artifacts have the hashes below, rather than an assumed Git revision.

Classification attaches to the specific claim, not every byte in an artifact. TRUSTED_EVIDENCE means the bounded inspection fact is reproducible; USE_WITH_REVIEW means a useful idea/source needs verification; UNTRUSTED means the claimed proof is absent or contradicted; RETRACTED means the producer's own ledger withdrew it. No class authorizes direct production adoption.

| Claim / classification | Artifact / source inspected | Scope actually proven | Limitations | Safe for Codex? / remaining verification |
| --- | --- | --- | --- | --- |
| Corpus inventory — TRUSTED_EVIDENCE | `golden-corpus/manifest.json`; JSON path/scenario counts | 14 entries, all referenced paths present: six valid, six privacy, one missing-data and one malformed scenario; 24 physical files including the manifest. | Not the handoff's claimed 38 fixtures. Presence does not establish source fidelity or expected normalization. | Inventory only; independently verify schema, provenance, sanitized content and expected outputs before reuse. |
| Golden source provenance — UNTRUSTED | Corpus entries plus `upstream-source-manifest.json`; `gh api repos/openai/codex/commits/13116b9e224b6d740bed02aa3a1fa123452af094` | The Codex pin is a Cerith revision, paired with a TokenTracker JavaScript path/function; official Codex lookup returns HTTP 422, no commit found. | A fixture can still be useful synthetic data; this is not an official Codex schema receipt. | Do not treat as ground truth; repair repository/commit/path/schema attribution and compare actual first-party output. |
| Upstream manifest authority — UNTRUSTED | Eight manifest targets; `gh api repos/anthropics/claude-code/git/ref/tags/claude-code-v1.0.12` | Sample Claude tag lookup returns HTTP 404; several entries use unverified descriptive tags; Gemini names `google/gemini-cli`, rather than the inspected official `google-gemini/gemini-cli`. | Only the two sample ref failures were remotely checked; do not claim every target was independently disproved. | Rediscovery hints only; verify each repository, immutable revision, path, function and license separately. |
| Differential parity — UNTRUSTED | `tools/differential_runner.py`; `reports/differential_disagreement_report.json` | Stored report says 12/14 pass, two disagreements. Runner counts raw JSON/JSONL/SQLite records. | Calls neither reference parser nor Cerith parser; no normalized token/model/timestamp parity. Missing files can be skipped; a detected privacy canary does not fail the comparison. | Useful inventory idea; replace with two actual parsers and field-level expected results, fail closed on missing input/privacy disagreement. |
| Upstream unchanged/drift proof — UNTRUSTED | `tools/drift_check.py`; `reports/upstream_drift_report.json` | Codex is reported UNCHANGED; seven other targets CHANGED/REMOVED. | Missing repositories can fall back to TokenTracker; missing pinned revision falls back to function-name presence and can report UNCHANGED. | Source-search aid only; require verified upstream pin, exact file/hash and semantic comparison without fallback. |
| Full mutation campaigns — UNTRUSTED | `verification/stryker-m3b-core/reports/mutation-report.json`; JSON mutant-status counts | Only located mutation report contains 44 mutants: 18 in ActivityMetricMapping and 26 in ActivityRecord, **all Ignored**; collector entries have no mutants. | No killed/survived/executed mutants in this report; no second full report located in the inspected artifact inventory. | The status count is trusted inspection evidence; it proves neither two full campaigns nor collector mutation adequacy. Require executed, scoped reports and command/version receipts. |
| Required M3-B duration gates — UNTRUSTED | Lab/rehearsal `ActiveSoakTests.cs`; handoff and master status | Inspected lab streaming/mixed tests use 8/6-second cancellation; rehearsal SQLite uses 15 seconds, streaming uses a finite 500-record workload. | No located raw timing receipts demonstrate 45-minute streaming, 45-minute SQLite or 60-minute mixed active soaks. Same-process reinstantiation is not forced process death. | Short adversarial test ideas only; independently run and retain target-duration, workload, restart and acceptance receipts when this becomes a real task. |
| Real crash/checkpoint test idea — USE_WITH_REVIEW | `shadow-m3b/Cerith/tests/Cerith.M3B.ReferenceLab.Tests/ProcessRestartAndCheckpointTests.cs` | Source launches CollectorHost with redirected output, waits for event/checkpoint acknowledgements, calls Process.Kill and restarts; asserts only the new record is emitted. | Not rerun; depends on Debug host path and lab schema. Acknowledged checkpoint case does not cover every kill window or production IPC. | Reuse the test design after reviewing host/checkpoint code, cleanup/deadlines and abrupt-write windows; do not port its architecture automatically. |
| Python durable restart proof — UNTRUSTED | `reusable-harnesses/restart-harness/restart_harness.py` | In-process simulation passes a watermark from phase one to phase two. | No child spawn/kill; written checkpoint is not reloaded to recover phase two. A sleep labelled force-kill is not a crash. | Scenario sketch only; replace with real child lifecycle and on-disk checkpoint recovery before claiming restart behavior. |
| Streaming adversary input ideas — USE_WITH_REVIEW | `reusable-harnesses/streaming-adversary/streaming_adversary.py` | Generates partial UTF-8/BOM/oversized-line/truncation/rotation/burst stimuli. | `parsed_simulated` increments while writing; no candidate reader consumes them. Stored zero-loss/deduplication counters do not prove parser behavior. | Stimuli only; connect actual collector, verify byte/record checkpoints, expected rejects and dedupe independently. |
| SQLite adversary ideas — USE_WITH_REVIEW | `reusable-harnesses/sqlite-adversary/sqlite_adversary.py`; seven-pass report | Actual Python SQLite connections exercise WAL/lock/corrupt-header cases. | No Cerith collector or child process. The labelled crash recovery commits/closes normally; replacement happens after closing the connection. | General SQLite cases only; add actual reader/process/kill/replacement behavior before accepting lifecycle claims. |
| Privacy CLEAN / zero leaks — UNTRUSTED | `reusable-harnesses/privacy-harness/privacy_harness.py`; zero-leak report | Contains canary patterns and text scans. | Excludes reports, including the default reports root, plus fixture directories; skips missing/unreadable/binary input. No demonstrated positive-control coverage. | Pattern ideas require review; verify scanned-file inventory and injected positive controls, then scan actual publishable artifacts. |
| Slop gate completeness — USE_WITH_REVIEW | `tools/collector_slop_check.py` | Five regular-expression checks provide bounded triage. | Not AST analysis; accepts undeclared ai.* names, misses some empty catches and advertised memory/cancellation rules. | Manual review aid only; canonical registry, lifecycle and diff review remain necessary. |
| M4 native/runtime proof — USE_WITH_REVIEW | `WindowStyleAndFocusTests.cs`; `reports/m4_benchmark_results.txt` | Dummy Win32 STATIC window tests style/focus flags; two-second CPU sample. Stored microbench: 100,000 binding updates in 156 ms with ~7,068 KB memory delta. | Not actual production WinUI shell, 30-minute active runtime, zero CPU or leak measurement; loop timing is not render latency. | Useful test ideas and lab receipt; verify actual window/runtime/device scope and sustained workload before performance claims. |
| M7 multiprocess soak — UNTRUSTED | `ConcurrencyTests.cs` | Inspected 5/6-second tests use Task.Run writers/readers/rollup in one process. | Method naming does not make separate OS processes; no 60-minute multiprocess receipt located. | Concurrency scenario ideas only; require independent processes, real elapsed time and fault/recovery evidence. |
| M7 benchmark receipt — USE_WITH_REVIEW | `reports/m7_benchmark_results.txt`; `reports/m7_scaling_results.txt` | Stored one/two-million-row runs report ~216,739/~199,655 samples/s; two-million footprint 138,479,728 bytes, 24-hour query 24.921 ms, aggregate 107.95 ms, rollup 573,274 ms. | Not rerun or production measurement. Point queries can be sub-millisecond while broader queries/rollup are not; rollup took ~9.55 minutes. No zero-leak proof. | Preserve bounded numbers/context; verify benchmark code/environment, workloads and recovery before any production sizing/design decision. |
| Drop-in task packets — USE_WITH_REVIEW | Five `codex-packets/M3B_PACKET_*.md`: Aider, Continue, Windsurf, Cody, Tabnine | Discovery hints and illustrative parser/test recipes exist. | Recipe types/metrics are lab-only; Aider defaults missing counters to zero; Continue/Cody read entire files. Pins/schema/product units and adversarial tests remain unverified. A 27-test, ~1.38-second report is not broad readiness proof. | Discovery only; no new canonical queue. Verify first-party schema/pins, missing != zero, bounds, units and real production consumer before implementation. |
| Shared primitives/direct adoption — UNTRUSTED | `CODEX_TRUST_MAP.md`, `CODEX_VERIFIED_ACCELERATOR.md`, future-work handoff | They recommend StreamingLineReader/SQLite primitives and direct porting. | Research reuse does not establish multiple accepted production integrations with identical responsibility/semantics. Types/history/storage decisions are not authorized by passing lab tests. | Reuse facts/vectors/test ideas after review; no automatic helper/framework/history/schema port. |
| All gates/production readiness/time saved — UNTRUSTED | `MASTER_STATUS.md`, both marathon handoffs, `CODEX_SAVINGS_LEDGER.md` | Preserved claimed suite totals differ (438 versus 464); savings are estimates. | No located raw receipts independently establish all durations, two mutation campaigns, zero leaks, perfect fidelity or every uncertainty solved. | Do not repeat completion language. Revalidate only evidence required by a concrete future task; no fabricated saved-time measurement. |
| WSL and zero-idle-CPU claims — RETRACTED | `verification/DEFECT_LEDGER.md`: M3B-007 and CPU defect; master status | Producer withdrew WSL implementation claim and acknowledges polling/nonzero CPU. | This audit did not inventory every possible WSL implementation; two-second CPU test does not establish broad idle performance. | Keep retraction visible; require real WSL acquisition/actual runtime measurements before restoring claims. |

Snapshot hashes (SHA-256) for repeatable follow-up:

| Artifact | SHA-256 |
| --- | --- |
| golden-corpus/manifest.json | b905b39f10a0df727722ea35da9c77f4ced04f3b291dc0f1f40e4edfbf6d8c32 |
| upstream-source-manifest.json | 5baafc9598f85582f42255a472ebc1a6b67758afb8ded937bbce012e7bb9a1ff |
| tools/differential_runner.py | 9a06b73e1ad4e927747c58ed48ce3f28b8081a68a5bf7fca97bf01a69813647d |
| tools/drift_check.py | 5e731b45888e4633ba0a2a0ed6ab376ae564c0d5f8a9838ee48ae6c9e6a5f9c4 |
| verification/stryker-m3b-core/reports/mutation-report.json | ce9a0503c9596b35da0a51dc307e59ae3b81c7d2da2efa6f8d99a867da74c38f |
| reports/m7_scaling_results.txt | 13a229a330c128214e840197a246486436bed01e76f8bb5982f70c821a37f591 |

Outcome: useful adversarial designs were separated from unsupported proof. No Shadow code/dependency/private payload was copied, no harness was executed and no production architecture decision changed. The quota-wave candidate remains gated by fresh independent review; this audit accelerates future evidence discovery without approving future implementation.
