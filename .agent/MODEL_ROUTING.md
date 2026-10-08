# Model Routing

Operational preference only. This may change frequently.

## Historical CER-M3-004 routing

The final ZCode correction on 2026-10-03 used GPT-6.1 Sol Medium. That task-specific route is historical and does not override the current policy below.

## Core rule

Treat **model**, **reasoning effort** and **route** as separate decisions.

`Web` is a route, not a model family.

Choose the reasoning tier from the **technical risk and difficulty of the work**. Then choose the route that best fits the task's context, latency, tool and quota needs.

Task duration, token volume, native code, repeated iterations or a first failed probe are not escalation reasons by themselves.

## Default execution / write owner

Current default:

```text
model  = GPT-6 Luna
effort = Max
route  = native Codex
```

Use GPT-6 Luna Max by default as the normal sole repository write owner for:

- normal implementation after the semantics are understood;
- deterministic tests;
- routine debugging and evidence-driven retry loops;
- bounded runtime validation;
- CI/rebase/fix loops;
- straightforward refactors;
- documentation and repository maintenance;
- well-scoped work, including long-running tasks.

GPT-6 Luna Max can own a task for hours. Duration or token volume alone does not imply stronger reasoning is needed.

Ordinary low-risk subagents may also use GPT-6 Luna Max.

## Strong reasoning work

Use GPT-6.1 Sol when stronger reasoning materially reduces technical risk. This is not merely a quota preference.

Examples include:

- architecture or durable contract decisions;
- concurrency, cancellation, backpressure and lifecycle reasoning;
- IPC and trust-boundary review;
- security/privacy review;
- native ABI/interoperability;
- provider semantics where the upstream contract is subtle;
- difficult debugging with contradictory evidence;
- hostile review of high-risk changes;
- milestone-closing review where a semantic defect could invalidate the result.

Do not downgrade one of these gates to GPT-6 Luna solely to save quota.

## Integrated Web agents via Codex Web GPT

Codex Web GPT is an integration route that can provide Web models with the Codex workspace, terminal and local tools. Do not treat it as an isolated browser chat or infer missing repository context from the model name. Verify the installed route's actual capabilities and quota before routing. GPT-6 Sol Web may own review or implementation when suitable and supported; a Web writer must be the sole writer of its task.

```text
model  = GPT-6 Sol
effort = choose from supported efforts for the task risk
route  = Codex Web GPT integration
```

Good examples:

- inspect one risky function or compact diff;
- answer one subtle API or semantic question;
- investigate one bug hypothesis;
- review one bounded concurrency or security concern;
- interpret a small set of upstream sources;
- provide a high-quality second opinion.

Task size alone does not exclude this route. Use verified tool access, context limits, observed latency, model suitability and available quota to choose between integrated Web and native execution. When another agent owns the task, a Web reviewer remains read-only. Do not assume Web and native quotas are independent without evidence.

Prefer a compact evidence packet:

```text
exact question
+ relevant diff
+ minimum required contracts/source
+ runtime/test evidence
```

## Strong native reasoning: GPT-6.1 Sol Medium

Use:

```text
model  = GPT-6.1 Sol
effort = Medium
route  = native Codex
```

when the work warrants a strong reasoning model and native Codex context/tooling is appropriate, including:

- substantial architecture or contract work;
- high-impact concurrency/security/IPC reasoning;
- native interop/ABI review across meaningful context;
- difficult provider integration or semantic analysis;
- context-heavy hostile review;
- debugging where multiple components/evidence sources must be reconciled;
- a critical gate where the native harness or broader context materially helps.

After the difficult reasoning point is resolved, implementation normally returns to GPT-6 Luna Max when exposed by the active harness. If unavailable, choose a permitted verified route suitable for the task; do not pretend unavailable Luna executed.

CodexGPT Web and GPT-6.1 Sol native are alternative strong routes selected by task shape. There is no rule requiring Web first.

## Astra routing

GPT-5.6 Web and GPT-6 Astra are excluded during this execution under Fer's latest instruction.

## Preferred pattern

There is no single mandatory linear ladder.

Typical execution:

```text
GPT-6 Luna Max native executes
→ when stronger reasoning is useful, choose by task shape:
   - integrated GPT-6 Sol Web when verified capabilities and quota suit the task
   - GPT-6.1 Sol Medium native for substantial/context-heavy reasoning
→ GPT-6 Luna Max native normally implements/fixes/finishes
```

## Ownership

One repository write task has one write owner at a time.

Independent specialists/reviewers are read-only by default and must not silently mutate the implementation they are reviewing.

Antigravity may take independent non-overlapping work or a second-pass review when useful.

## Runtime continuity

Use the calling harness's authoritative runtime metadata when available; do not invent execution receipts.

## Quota and quality rule

Optimize quality per scarce native quota **without weakening high-risk gates**.

- Use GPT-6 Luna Max for execution and ordinary work.
- Consider integrated GPT-6 Sol Web for review and implementation after checking actual tools and quota; preserve single-writer ownership.
- Use GPT-6.1 Sol Medium native for substantial/context-heavy work where stronger reasoning materially reduces risk.

Quota optimization chooses between suitable routes; it does not redefine what level of reasoning the technical risk requires.

## Latest product priority

Fer prioritizes a coherent M4 vertical slice: configurable native composition, exact MetricId binding and persistent non-secret layout state, using existing components. Investigate pending canonical requirements before adding more graphics primitives. Preserve unintegrated work and its validation limits. M5/M6 still require preceding milestone independent exit/integration/documented closure; no M7. Reuse the existing single continuation; do not repeat verified capabilities or completed CER-M4-011.

Antigravity remains an additional authorized ordinary-quota route when an available installation offers a concrete advantage. Do not install, add expense or assume separate quotas. Any new Web write-capability probe must be small, reversible and isolated; existing historical actual task receipts remain evidence rather than being relabeled NOT RUN.

## Historical capability audit — 2026-10-08

Receipts below describe what actually ran before the latest routing restriction; they do not authorize further GPT-5.6 Web use.

Fer's correction supersedes earlier assumptions about missing Web local context. The installed subagent registry exposes GPT-6 Sol Web and GPT-5.6 Sol Web (Medium/High). Their Codex Native bridge can forward operations into this workspace.

| Capability | GPT-6 Sol Web | GPT-5.6 Sol Web |
| --- | --- | --- |
| Local terminal | PASS: forwarded codex_exec, exit0 | PASS: forwarded codex_exec, exit0 |
| Repository/Git status | PASS: actual root, branch, HEAD and pending files | NOT RUN separately |
| Canonical documentation | PASS: MODEL_ROUTING and ROADMAP read | PASS: TESTING_QA file SHA-256 via local terminal |
| Runtime/test evidence | PASS: native receipt file discovery; contents not checked in that probe | PASS: native JSON loss/recovery/close scalars; requested QA/CPU fields not exposed in initial mixed output |
| Complete bounded agent task | PASS: multi-step read-only probe | PASS: runtime read and subsequent single-command hash probe |
| Implementation writes | PASS: CER-M4-012 bounded source/test edits via native bridge; Release0warnings/errors, focused7/7 and fullIntegration80/80 | NOT RUN; apply-patch advertised |

GPT-5.6 Sol Web's initial heterogeneous PowerShell output exposed only the native timing table. The compact JSON follow-up was rejected before execution by automatic safety review because it could not determine the request's safety state; no exit code or JSON exists for that attempt. A safer single Get-FileHash command subsequently executed successfully (exit0), returning QA SHA-256 6CC2872C857CB03C00F171232A723A2EB452DDF8CE82E15297C7375C10C6AD2A. This is a specific rejected request, not evidence of unavailable local tools or exhausted quota. The requested QA73/CPU extraction remains unverified through that route.

Terminal, Git, documentation and runtime access are verified as indicated above. GPT-6 Sol Web subsequently completed CER-M4-012 bounded implementation through real apply-patch and local build/test calls (seven focused tests and80 Integration tests passed). It returned ownership before root edits/native validation; the writer was closed. This proves bounded agent implementation access, not unrestricted arbitrary tools or completion of native/performance/CI/merge gates. GPT-5.6 Sol Web implementation remains NOT RUN. No parent bridge token was invented; the Web agents used their issued integration context. Both reviewers were closed after the audit; one repository write owner remained throughout.

Native ordinary quota98% before probes and100% afterward/weekly55% was observed, with native root audit running in the same interval. Attribution is confounded, no separate Web quota counter is exposed, and quota independence remains UNVERIFIED. After the normal reset, ordinary usage allowed/primary1%/weekly55% was verified. No installation, reset-credit action or paid resource was used. Recheck actual capabilities and available ordinary capacity when selecting a route rather than inferring either from its name.

The subsequent GPT-6 Sol Web next-gap investigation encountered automatic safety rejection on one complex read request; separate single Get-Content reads then succeeded. Rejections are request-specific observations, not blanket evidence of missing workspace access.

### Antigravity availability audit — 2026-10-08

Existing manager2.19.1 and IDE2.5.5 installations were verified by executable metadata. The IDE launcher exists outside PATH and its read-only help exposes `chat` ask/edit/agent modes; this alone proves neither model access nor quota. The manager was launched for availability inspection, including a reversible accessibility launch: its own window remained blank, with no rendered agent/quota controls (UIAutomation and isolated PrintWindow corroboration). Only owned launched windows were closed. No Antigravity prompt, writer, model call, installation or reset was used. Authenticated capacity and quota independence remain UNVERIFIED; the route stays authorized but offers no demonstrated execution advantage in this audit. The community antigravity-agent-manager skill was evaluated, and its installation recommendations were not applied.

CER-M4-013 Web6 successfully edited candidate sources/tests and reported a Release build, then encountered an automatic read-inspection rejection (safe independent reads supplied) and a later stream-disconnection error without final answer. No completed final tests/handoff assumed. The Web writer was closed before Ampere/nativeSol6.1Medium took recovery ownership for outstanding interop/transaction/concurrency debugging. This observed execution failure does not establish a quota limit or revoke the route's verified environment/write capabilities; no quota independence inferred.
