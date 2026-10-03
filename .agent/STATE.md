# State — CER-M3-004 final ZCode closure

Current disposition: the remaining ZCode finding is corrected and locally verified; candidate readiness requires Build and Test and Repository Quality to succeed on the exact pushed PR head. The independent re-review of `0b9daad6534623d256b79bbd30f39387405793d2` → `84fbf84d1065ba859605cdd2dc83019a1b51fe69` returned `FIXES STILL REQUIRED — NO-GO FOR MERGE`: findings 1–4 and 6–9 are closed, and finding 5 (ZCode auth becoming unverifiable between owners) remained partially unresolved. That verdict stays attached to that range. This owner has not issued independent approval. CER-M3-004 remains IN PROGRESS; M3 remains ACTIVE / INCOMPLETE; PR #47 remains OPEN / DRAFT. Canonical `origin/main` remains `72b43b268b4992d0b0ea45b6e29067d10450274f`, and local `main` remains untouched at `3f5ca74a26edf88a02e6bda05f759ee3c74032cd`.

Authorized execution route: GPT-6 Luna Xhigh, native Codex, one writer, concurrency one. No subagents, Sol, Luna Max escalation or Astra. The only functional delta is ZCode: clear the old poll admission deadline and wake when a verified owner becomes unverifiable, then give a newly verified owner fresh admission and wake. A→A retains its existing retry deadline; repeated invalid reads do not repeatedly wake.

Final local verification: Release build 0 warnings/errors; Core 39/39; Telemetry 384/384; Integration 10/10; total 433/433, zero skipped. ZCode focused suite 28/28; eight targeted transition, backoff, cancellation and publication checks passed across the focused selections and complete ZCode suite. Shared-bus coexistence 1/1; supervisor failure isolation 11/11. `git diff --check`, repository filename/path hygiene (137 tracked paths), bounded sensitive-added-diff scan (zero credential/private-path matches) pass; dependency/project diff is empty. The final pushed SHA and exact-head Build/Test, Repository Quality and CodeQL dispositions are pinned in the current PR #47 description.

Next action: run one fresh bounded read-only independent re-review of only the ZCode delta from `84fbf84d1065ba859605cdd2dc83019a1b51fe69` to the exact PR head, its regressions, documentation reconciliation and exact-head CI. Preserve the eight prior closure decisions unless this ZCode-only delta changes their basis. PR #47 remains DRAFT; no merge or `main` change is authorized. THIS IS NOT THE FINAL INDEPENDENT RE-REVIEW.

## Preserved previous state record

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

Closure self-review expanded the manual probe to an admitted xAI-issuer OIDC fixture: both binaries acquired only the loopback mock quota, but neither observed replacement owner in that case either. Six owner observations fail; this is still synthetic protocol evidence, not a valid subscription/renewal/live comparison. Hosted code/tool checkpoint 82da884 passed Build and Test (37042555549, 0 warnings/errors, 39/356/10 tests), Repository Quality (37042555581); CodeQL skipped (37042555555). The three-case tool checkpoint subsequently passed its own exact-head checks, recorded below.

Hosted code/tool checkpoint 113afc648c03b36c04c888b0c867d34f284ca958 (107 ahead / 0 behind main): Build and Test SUCCESS 37043424898, 0 warnings/errors, Core 39/39, Telemetry 356/356, Integration 10/10; Repository Quality SUCCESS 37043424940; CodeQL SKIPPED 37043424841, not a security pass. The final receipt-only documentation commit preserves that exact runtime/tool tree. Pin the current PR head and its checks for fresh review.
