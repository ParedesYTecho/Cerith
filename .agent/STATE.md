# State — CER-M3-004 remaining-work closure

Current disposition: MISSION_CANDIDATE, pending fresh independent review and Advisor/user decision; M3 ACTIVE / INCOMPLETE. PR #47 must remain draft. Canonical origin/main remains 72b43b268b4992d0b0ea45b6e29067d10450274f; local main is untouched. Sole owner/routing remains GPT-6.1 Sol Medium/native Codex/concurrency one for this run.

Latest bounded unit: reproducible isolated Grok 1.0.34/1.0.46 protocol qualification rejects unsupported credential classes before backend access but fails owner-switch observation (both exit 1). Official child refresh/termination safety and existing-owner leader attachment remain unresolved; Grok stays EVIDENCE_BLOCKED, not LIVE_NOT_AVAILABLE. Claude's missing local subscription does not restrict other subscribers; an approved quiet renewable owner-safe seam is still unproven. No new provider is registered. Eleven accepted/staged providers are preserved; Qoder International/Kimi/Devin remain deferred, Ark Coding remains staged.

The only new code is a manual stdlib development probe under tools/, with synthetic fixtures and loopback endpoints, no bearer/model request or real auth writes. Runtime/dependencies/MetricIds unchanged. Fresh local closure build: 0 warnings/errors; Core 39, Telemetry 356, Integration 10 (405/405), zero skipped. Prior hosted f1531e2 quality succeeded and CodeQL was skipped. The new checkpoint needs exact-head CI. Graph was incrementally refreshed for the tool: 3,292 nodes / 8,795 edges / 153 communities, old corpus/edges preserved, no missing files or self-loops; docs and historical labels were not re-extracted.

Next action: fresh independent hostile review of the exact final PR head using .agent/CER-M3-004_REVIEW_PACKET.md, after confirming current hosted checks. This execution context cannot provide independent approval or merge main.

## Preserved previous state record

# Estado actual

Proyecto: Cerith
Nombre: provisional
Hito: M0 — Foundation DONE; M1 — Hardware MVP DONE; M2 — Gaming MVP DONE; M3 — AI Quotas MVP ACTIVE / INCOMPLETE
Rama canónica: `main`
Estado: CER-M3-004 MISSION_CANDIDATE; task remains IN PROGRESS pending fresh independent global review and Advisor/user decision. M3 ACTIVE / INCOMPLETE. Validated packet/code head 991187e605657256f0cff8f8d14af3998fbd8989 has green Build and Test / Repository Quality; CodeQL SKIPPED. Ten non-Codex providers plus Codex remain staged. Claude, enterprise Gemini CLI / Code Assist and Grok Build are EVIDENCE_BLOCKED; Qoder International, Kimi and Devin are DEFERRED_AFTER_M3_QUOTA_WAVE with exact branch tips preserved and all previously local-only blocker commits now backed up remotely. Current source recheck and review packet are canonical. Build 0 warnings/errors; Core 39/39, Telemetry 356/356, Integration 10/10; coexistence 1/1; supervisor injection 11/11. No secrets, new dependency, auth/inference/paid/reset operation or live-proof upgrade. PR #47 DRAFT; origin/main unchanged at 72b43b268b4992d0b0ea45b6e29067d10450274f; local main unchanged at 3f5ca74a26edf88a02e6bda05f759ee3c74032cd. Read-only Shadow evidence audit completed in docs/REFERENCE_PROJECTS.md: unsupported duration/mutation/privacy/provenance/readiness claims downgraded; useful adversarial designs preserved as USE_WITH_REVIEW. No harness executed or code/architecture ported. Final documentary checkpoint requires exact-head hosted CI; next production action is fresh independent hostile review, not M4/M7.

Latest follow-up: authorized GitHub/provider source research completed; nine reference snapshots and official product/source findings in AI_QUOTA_REFERENCE_MATRIX.md. Claude subscriber coverage is required regardless of host access. Grok shared-weekly product semantics and source-level x.ai/billing API-key exclusion are established; deployed-version/owner/renewal gates still pending, provider branch not imported. No runtime/source/test/dependency/auth change. Prior 43aef2e exact-head CI passed; new documentation needs its own hosted CI and fresh-context review.

## Listo

- Esqueleto de solución y referencias entre proyectos conforme a `docs/ARCHITECTURE.md`.
- Contratos fundamentales de métricas y sus pruebas integrados en `main`.
- `MetricRegistry` y sus pruebas están integrados en `main` mediante PR #8.

## Verificación de CER-M0-002

- PR #6: review del Advisor aprobada; merge mediante squash en `e895e1666d376a22c15a31d6b809c5a6b716307d`.
- `dotnet build Cerith.sln --configuration Release`: PASS, 0 advertencias y 0 errores.
- `Cerith.Core.Tests`: PASS, 17/17.
- Telemetry.Tests e Integration.Tests siguen vacíos; cada uno conserva una tolerancia temporal individual.
- Una ejecución de Core filtrada a cero pruebas devuelve MTP `ZeroTests` (8) y falla sin tolerancia.
- GitHub Actions: Build and Test PASS; Repository Quality PASS; CodeQL SKIPPED según D-013.
- Los PR Dependabot #3 y #4 siguen siendo mantenimiento separado y no forman parte de CER-M0-002.

La política temporal por proyecto de `ZeroTests` está registrada en D-012.

## Verificación de CER-M0-003

- PR #8: review del Advisor PASS; merge mediante squash en `00e8a9f1d35d9d96b754298a55ff4cfb416bcc71`.
- Build and Test: success; Repository Quality: success; CodeQL: skipped según D-013.
- `main` local y `origin/main` coinciden en el SHA del merge; working tree limpio durante la reconciliación.
- Los PR Dependabot #3 y #4 siguen siendo mantenimiento separado.

## CER-M0-004

`MetricBus` distribuye snapshots por `MetricId` exacto a múltiples suscriptores con buffers acotados y backpressure. Cancelación y disposal cierran las suscripciones; no hay replay ni dependencia de `MetricRegistry`. D-015 y `docs/METRICS.md` quedaron integrados. PR #10 pasó review del Advisor con PASS y se mergeó mediante squash en `bd305ae1b2975c8d3a81125688ec21639ab90817`. Build and Test: success; Repository Quality: success; CodeQL: skipped según D-013. Verificación local: Release build PASS (0 advertencias/errores), Core 39/39 PASS y `git diff --check` PASS. Graphify code-only: 286 nodos y 463 aristas; reextracción semántica documental pendiente.

## CER-M0-005

`SyntheticProvider` publica `synthetic.counter` determinista mediante `MetricBus`, registra su descriptor en `MetricRegistry` y usa `TimeProvider`. Los valores generados usan confidence `Unknown` porque no son mediciones ni estimaciones de telemetría real. PR #12 pasó review independiente del Advisor con PASS y se integró mediante squash en `main` (`729075dcfcef1ae849b9339a96b438d234e7ff37`). Build and Test y Repository Quality: success; CodeQL SKIPPED según D-013.

## CER-M0-006

Se compararon Named Pipes, TCP loopback y AF_UNIX con dos runs de 5000 round trips por candidato/payload (256 y 4096 bytes), cada uno precedido por 200 warmups. Las tres opciones pasaron reconnect/cancellation/cleanup. D-016 quedó integrada y elige Windows Named Pipes para la primera implementación; los resultados están en `docs/PERFORMANCE.md`. PR #14 pasó review independiente del Advisor con PASS y se mergeó mediante squash en `dc6c051f0734fc5eb8a301a16bda671d2ba94221`. No se implementó IPC de producto. La implementación futura debe conservar el requisito local-only y demostrar el rechazo de clientes remotos; `CurrentUserOnly` por sí solo no basta.

## Siguiente

Mantener PR #47 draft. Publicar el paquete de revisión ya preparado y exigir CI verde en ese SHA. La revisión global independiente corresponde a un contexto futuro nuevo y de solo lectura; este owner prepara el paquete y no cuenta su auto-revisión como aprobación. No merge a main. Preservar todas las ramas de proveedores y sus disposiciones. No imponer el gate 17/17 ni declarar CER-M3-004 o M3 completos.

PR #23 de context integrity/provenance se integró en `94de88be841968be18fd6d3225bd184044347af6`; CER-M1-001 se integró después en `b7e40447f48cd6afd2b015a94a49d825f28099f9`, sin conflicto.

Deuda explícita:

- Revisar la compatibilidad de Windows App SDK y sus transitivas con la licencia antes de fijar o publicar la licencia definitiva.
- El linked source de `NamedPipeMetricClient` en Integration.Tests es un workaround limitado de M0, no un patrón permanente.
- `Mono.Posix.NETStandard 1.0.0`: no se halló incompatibilidad conocida; la licencia/SPDX exacta sigue siendo una deuda de provenance/third-party-notice que debe resolverse o documentarse antes del public release.
- CER-M1-002 se probó localmente con una CPU y una GPU AMD; NVIDIA/Intel, sensores ausentes y múltiples GPUs no están validados en máquina real. `gpu.N` no garantiza identidad física persistente entre reinicios o reenumeración. CI no depende de hardware.

Dependabot #3/#4 se integraron posteriormente como mantenimiento de GitHub Actions, sin cambio de comportamiento de producto. Graphify label drift permanece separado.

Futuro no activo: `docs/ROADMAP.md` y los documentos de producto registran una expansión posterior de telemetría hardware/gaming (incluyendo CPU_FAN/GPU fan verificables, métricas CPU/GPU adicionales y separación de FPS) y un modo Test / Diagnostic Session para validación reproducible y comparación externa. Nada de ello cambia el alcance de M2 ni inicia M4; M3 está ACTIVE / INCOMPLETE.
