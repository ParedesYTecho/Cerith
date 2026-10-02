# CER-M3-004 — independent candidate review packet

## Review target

Review the exact current head of draft PR #47 against origin/main at 72b43b268b4992d0b0ea45b6e29067d10450274f. Confirm the PR head SHA before starting; the CI-verified code/docs checkpoint is d3cfc24, and this packet will be committed afterward, so the review target must be the later PR head. Review read-only and report findings only. Do not edit files, request credentials, run provider auth commands, or merge.

PR: https://github.com/ParedesYTecho/Cerith/pull/47

## Scope

The wave stages ten non-Codex quota integrations: Cursor, Antigravity, ZCode, GitHub Copilot, OpenCode Go, Kiro, Command Code, Qoder CN, Ark Agent Plan, and Ark Coding Plan. Codex is the completed separate slice. Ark Coding Plan is imported from provider tip c5841bf2ee6140d61ee517db62d8b176a996ce21 by merge ed1a6ec.

Qoder International is excluded from the integration tree and classified DEFERRED_AFTER_M3_QUOTA_WAVE. Its branch remains preserved at 7a1a89e9f88ea1de57f5bf180f4e8bfd382c88fb; integration revert 9319474 removes the temporary import because its desktop IPC calls lack a first-party contract. The current documented CLI/SDK route would require a separate SDK/runtime evaluation.

Other provider dispositions are in .agent/TASKS.md and docs/PROVIDERS_AI_QUOTAS.md. In particular, the wave does not claim 17/17 implementation parity, M3 completion, or successful live validation for providers unavailable on this host.

## Validation evidence

At d3cfc24:

- Release solution build passed with 0 warnings and errors.
- Guarded direct MTP suites passed Core 39/39, Telemetry 355/355, Integration 10/10 (404 total).
- Hosted [Build and Test](https://github.com/ParedesYTecho/Cerith/actions/runs/37002245312) and [Repository Quality](https://github.com/ParedesYTecho/Cerith/actions/runs/37002245300) passed. [CodeQL](https://github.com/ParedesYTecho/Cerith/actions/runs/37002245169) was skipped by workflow policy.
- Exact filename hygiene passed for 134 tracked files; git diff --check passed. The scoped added-diff pattern scan found no personal paths, known token patterns or bearer values; its eight email matches were synthetic example.invalid fixtures. This is not a comprehensive secret scan.
- Graphify: 3,272 nodes, 8,816 edges, 153 communities; integrity diagnostics reported zero missing/dangling endpoints, self-loops or collapsed edges. No external LLM was used.
- Local arkcli, qoder, and qodercli were unavailable; Ark Coding Plan and Qoder International were not live-validated. No auth or account commands ran.

Fresh hosted CI must pass on the exact review target after this packet is committed.

## Review checklist

1. Inspect the full PR diff for regressions, unsafe provider ownership, shutdown/coexistence failures, metric semantic errors, or accidental auth/secret handling.
2. Verify Ark Coding Plan registration, six MetricIds, used-percentage/reset semantics, fail-closed identity checks, and supervisor isolation.
3. Verify Qoder International is absent from runtime registration and its provider branch remains preserved.
4. Check task, roadmap, metric catalog, provider contract, state, handoff, and worklog consistency.
5. Report only actionable findings, with file and line, or state that no blocking findings were found. Do not infer live provider correctness from tests alone.

## Stop boundary

Keep PR #47 draft. The review is the sole independent GPT-6.1 Sol Medium mission review for this candidate. Stop before final approval or canonical-main merge; no such authorization is included in this task.
