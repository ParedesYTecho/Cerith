# Testing & QA

## Verdict vocabulary

Use:

```text
PASS
FAIL
BLOCKED
NOT RUN
```

Avoid “looks fine”.

## Layers

### Unit

- metric normalization;
- expressions/rules later;
- provider parsing;
- cache logic.

### Contract

- provider conformance;
- IPC schemas;
- plugin contracts later.

### Integration

- provider → MetricBus;
- MetricBus → WidgetHost;
- process reconnect;
- history later.

### UI

- states;
- DPI;
- keyboard;
- reduced motion;
- layout.

### Performance

- idle CPU;
- memory;
- provider wakeups;
- overlay impact.

## Provider QA

Parsing tests are not enough.

AI quota providers require live/independent validation where possible.

When testing worker failures with `TimeProvider`, allow `PeriodicTimer` construction to finish before injecting a timestamp fault. Throwing from `CreateTimer` during construction can trigger a partial-construction disposal/finalizer crash on .NET 10.0.12; this was reproduced without Cerith code and independently surfaced by the Antigravity and ZCode test fixtures. Keep the correction test-only and preserve production cadence.

## Diagnostic validation sessions (future)

A Test / Diagnostic Session is evidence-generation tooling, not a substitute for deterministic tests.

When implemented, a session should make it possible to reconstruct what Cerith observed over a bounded run:

- timestamped metric samples with MetricId, value when present, state, confidence and source;
- sanitized error codes and relevant provider/runtime lifecycle transitions;
- sampling/poll cadence and version information needed to interpret the run;
- enough target/session identity to align samples without logging unnecessary private process metadata;
- an explicit start and stop boundary.

For cross-tool validation, compare against an independently captured reference trace when useful. Align clocks/windows and normalize units before comparing. Record both agreement and disagreement, and never assume the reference application is automatically correct. A meaningful mismatch should trigger source/semantic investigation rather than being hidden by tolerance or post-processing.

Diagnostic captures must remain reproducible and inspectable. They complement unit, contract and integration tests; they do not make hardware-dependent CI mandatory.

## Visual changes

Capture representative states for meaningful UI work.

## Multi-monitor

Eventually test:

- primary/secondary;
- mixed DPI;
- unplug/replug;
- negative coordinates;
- taskbar variations.

## WidgetHost composition and reconnect regression coverage

CER-M4-006 links the non-WinUI production subscription, connection-attempt and presentation sources into the existing Integration project. Deterministic tests synchronize connection delegates instead of asserting CPU or scheduling thresholds: one unavailable-service attempt, follower release on success/failure, owner/follower cancellation, stale completion, and cancellation preserving healthy availability. Real pipe peers verify accepted connection counts for duplicated component IDs and exact case-sensitive independent IDs. Fan-out tests cover consumer removal/failure isolation, clean EOF, descriptor-free reconnect, Waiting reset and late descriptor delivery. Client tests reject value-kind mismatches against both accepted and previously delivered late metadata.

Presentation tests cover both renderers' value/state/confidence rules, explicit ranges, real zero, stale values, valueless/Unlimited states, overflow, missing descriptor, source independence and non-finite/nonnumeric fill suppression. These are transport/presentation tests, not automated WinUI control interaction. A background local-service probe corroborates real connection counts/recovery and a quiet OpenCode stream; native themes/layout/close and paired WinUI CPU/Working Set/Private Bytes remain separate acceptance checks on the currently available display. A missing secondary monitor does not substitute for a test result. Native descriptor-free startup checks must accept exact MetricId labels until semantic metadata arrives.

Native layout regression: at the actual available DPI, resize to the native minimum, scroll to the bottom and verify the final component's range text is fully contained in the client rectangle and scroll viewport. Check all four bounds beneath the identified component, not partial visibility of a parent or the outer frame. Repeat dark/light and default/minimum. Minimized performance samples must minimize after healthy startup and verify actual native minimization through warm-up and sampling. CER-M4-006 passed the layout checks at 96 DPI; higher physical scales remain unverified.

## Definition of done

A feature is done when:

```text
contract implemented
tests added
error behavior defined
diagnostics available
performance impact understood when relevant
docs updated if public behavior changed
```

## Native long-state/value regression procedure

Use explicitly labeled local protocol fixtures with the production controls when a real provider cannot produce a required state. Such runs validate native presentation, not live acquisition. Exercise zero, upper boundary, Stale, Unavailable, Error (including a valid 64-character code), Stopped, Unlimited, below/above range, a large finite number and return to zero; repeat standalone and embedded readouts at default/minimum size in both themes.

Wait with a bounded timeout for the exact expected presentation. Identify an embedded readout beneath its specific bar, not by tree order. Scroll and reacquire native elements until label, state, confidence and value can be inspected; confirm the exact text and separate bar-range containment. A clipped UIA BoundingRectangle can misleadingly fit inside the viewport. Check TextPattern document/range identity and visible character bounds, rejecting shortened glyph heights relative to the fully visible same-font reference. Intentional label ellipsis retains its full accessible name; actual value/state/confidence text must remain readable. Inspect native captures for complete glyphs; black or partial captures are inconclusive. Preserve candidate identity, explicit fixture labeling and all failed attempts outside Git. Missing physical higher-DPI coverage remains NOT VERIFIED.

Baseline CER-M4-006 controls failed confidence visibility for the 64-character error at both tested 96 DPI sizes. CER-M4-007 changes only the shared readout layout; final result and limits are recorded in its task receipt. Headless presentation/IPC tests do not substitute for this native regression.
## CER-M4-008 native DPI regression

The previous unpackaged WidgetHost executable embedded the SDK default manifest without DPI declarations. Real Windows 100→150% observations remained DPI_UNAWARE/GetDpiForWindow 96; apparent physical enlargement was bitmap virtualization, not 144 DPI native rendering. Explicit PerMonitorV2 preserves existing asInvoker/uiAccess=false security semantics. A manifest-only native candidate reached 144 DPI but reproduced stale live minima 360×340; newly launched 150% windows had correct 540×510 minima. The minimal XamlRoot.Changed handler therefore updates only scale-dependent presenter minima, leaves startup sizing separate and detaches before closing.

The combined production candidate with the actual service verifies PMv2 context identity, startup 100% 420×620/minimum 360×340, live 150% minimum 540×510, return 100% minimum 360×340, startup 150% 630×930/minimum 540×510. A user-size command of 500×700 before the transition yielded 750×1050, preserving logical sizing rather than resetting the startup default. A separate temporary App diagnostic records actual XamlRoot.RasterizationScale 1→1.5→1 and directly observes that user-resized geometry; its initial diagnostic-window sizing is inconclusive and excluded from startup acceptance. The production startup observations remain separate. CPU stayed Fresh/Measured, FPS Stopped/Unknown, and Codex Error/route_unsupported/Unknown.

At actual 144 DPI, labeled synthetic native fixtures using the unchanged production controls pass Dark 48/48 + Light 48/48 readout targets, plus 24/24 separate range-glyph targets per theme. Default/minimum sizes, full text identities, per-character glyph height and client/viewport containment are checked; intentional label ellipsis preserves its accessible name. The first Light range-only attempt failed large-value containment after an 80 ms scroll wait. A bounded three-second wait/reacquisition retry passed without weakening geometry/glyph conditions or changing product code. The failed attempt is not a PASS. These fixtures validate native presentation, not live acquisition. Private raw receipts/captures remain outside Git.

Record the original ordinary Windows Display scale, use scoped physical-coordinate-aware inspection, and verify actual window DPI and PMv2 context. Test live transitions separately from startup. Restore and verify the original setting after every transaction; preserve unexpected human changes. Nested finally restoration must not suppress runtime/context cleanup. The initial Settings UIA host-frame lookup failed after scaling; its directly owned native CoreWindow restored discovery and enabled verified rollback. Failed/partial transactions provide no PASS. All completed transactions restored the original 100% recommended setting and closed owned runtime processes. One physical monitor does not prove mixed-monitor transitions. Performance and final exact-head acceptance remain pending in the active task.

A separate actual-service 144 DPI stop/start transaction verifies all three representative readouts become transport-Unavailable with no snapshot, then recover CPU Fresh/Measured, FPS Stopped/Unknown and Codex route_unsupported/Unknown. Normal close, original-scale restoration and owned-process cleanup completed. This is a single bounded reconnect observation, not a recovery-time guarantee.

## Virtual-clock polling acceptance

A snapshot publication is not proof that the background polling delay has registered. Before advancing manual time to the next poll, synchronize with the existing test-time timer-registration signal. Two Copilot backpressure tests now use the established sibling pattern before their first advance; their state/value/ownership assertions and watchdogs are unchanged. A deterministic timing-order check demonstrates that advancement before relative-delay registration leaves a new full interval outstanding, whereas registration-before-advance completes the delay. This closes a source-supported test synchronization gap; it does not prove the actual cause of the PR60 hosted timeout or eliminate every possible scheduling race. Native runtime/performance is not applicable to this test-only change; repeated/full/hosted receipts belong to009.

## CER-M4-010 — ranged native ring validation

The ring uses production MetricReadout and shared internal MetricRange semantics, with one existing CPU subscription serving readout/bar/ring. Release solution and current WidgetHost builds pass with zero warnings/errors. Final Integration passes67/67 with zero skipped, including updated shared-range text/unit/descriptor-free/overflow assertions. Headless fanout proves three consumers on one accepted stream without a third connection; it does not execute WinUI geometry.

Source-qualified local protocol fixtures, visibly labeled synthetic, verify Dark/Light at actual 96 and 144 DPI, default/minimum physical sizes derived from logical420×620/360×340 and the observed DPI. Each scale has24 ring-readout targets per theme and24 separate range targets per theme:192 total exact text/per-character full-height/native client+viewport checks. States include zero, upper endpoint, Stale, Unavailable, safe/long Error, Stopped, Unlimited, below/above range, a large actual value, and return to zero. Intentional label trimming retains the full accessible name.144DPI checks use the corrected geometry;96DPI text/range checks preceded only the Ellipse margin correction, with unchanged fixed control/readout bounds. Corrected96DPI captures inspect aligned zero/full/partial circles; corrected static10/11/12 raster checks verify distinct increments and history-independent repeated values. Production presentation/component source identities match the isolated fixture copy; only its App selects a theme and explicit synthetic title. Original100% display scaling is restored and verified.

The installed determinate ProgressRing prototype fails small-increase painted accuracy despite correct accessible Value: settled10→11 retains10's raster; zero→11 differs;11→12 retains11. It is rejected, not shipped. Initial prototype startup failed on missing TabViewButtonBackground; adding the normal WinUI resource dictionary resolved that prototype, but the final static shapes need neither the player nor that dictionary. Initial black captures, inherited oversized96 dimensions, partially clipped scroll samples, a same-shell duplicate Add-Type attempt and a missing screenshot from a temporary literal replacement are excluded from PASS evidence. The range harness detected11px clipped glyphs against a13px full reference; it now scrolls to a full-height position while retaining exact per-character bounds checks. Independent source review found an extra Ellipse inset; removing both margins aligns the track/full/partial circles, and the correction was independently verified.

A bounded actual-service96DPI run passes startup, service loss, recovery, reopening while absent and normal close. The CPU ring presents Fresh/Measured, collapses numeric space on loss (108→72 physical px) while presenting Unavailable/No snapshot and retained known unit, and restores it to108px on recovery. FPS remains Stopped/Unknown and Codex Error/route_unsupported/Unknown on this host. Normal connected/absent closes observed120.40/130.68ms in one scenario; these are observations, not latency guarantees. The first harness incorrectly expected known-unit metadata to disappear on loss; the corrected expectation follows the unchanged subscription contract. All owned runtime/fixture processes are cleaned between phases.

The new ring has no animation/player/callback and is not a tab stop; readout and composite accessible names retain value/state/confidence/range. No new animation requires a reduced-motion branch. Screen-reader interaction, high-contrast themes, mixed-monitor transitions and higher-than144DPI coverage are NOT RUN for this bounded task. General milestone UI coverage and F7 performance gates remain open. Paired actual-service process-performance evidence and independent acceptance belong in PERFORMANCE before declaring this task complete.

## CER-M4-011 — connection pacing regression coverage

Release build: zero warnings/errors. Initial Integration68/70 FAIL in two new availability/slow-ACK DataRows because the test compared deserialized MetricDescriptor objects by reference; the transport and ACK completed, but that run is not PASS. Corrected field assertions gave70/70 PASS. Additional busy cancellation, competing single-instance clients and stalled production acceptance deadline/follower-release cases gave final73/73 PASS, zero skipped (16.223s). Existing missing-server cancellation now waits through several probes before cancelling. No production timeout inflation or protocol/security/descriptor change.

Independent GPT-6.1 Sol Medium source review PASS, no further essential regression requested. The seven-second stalled-ACK watchdog verifies bounded expiry/follower release; source inspection, rather than a tighter timing assertion, verifies the original five-second deadline is not reset. Actual native96DPI production candidate passes connected/loss/recovery/absent-reopen/normal-close with existing truthful readouts and collapsed/recovered ring; close observations88.76/77.20ms are single observations, not latency guarantees. UI implementation is unchanged; no repeat of the prior full geometry/DPI suite is claimed. Identified-binary performance, exact-head independent review/CI/protected integration are pending. Disposable absent-pipe probes are synthetic attribution, not telemetry availability evidence.

011 additionally passes three timed actual-service native cycles after the initial lifecycle run; canonical timings and clock limits are in PERFORMANCE. Twelve identified production observations are complete (four absent-minimized long, four connected-minimized short and four targeted connected-minimized long); initial connected-positive signal retained, generalnonregressionINCONCLUSIVE/F7OPEN. First long harness parse failure is NOT RUN and excluded; corrected harness passed parsing before measurement. All owned runtime processes closed. Graphify AST refreshed three changed C# files using installed local tooling; known namespace coalescing warning remains, no external LLM/API calls. Independent final evidence/exact-head review and hosted CI/integration still required.

## CER-M4-013 — configurable composition acceptance

Release solution build: zero warnings/errors; Integration93/93 PASS, zero skipped, including20 focused composition/store regressions. Windows SQLite cases cover strict payloads/Unicode, foreign/unsupported/corrupt storage preservation, conflicts, rollback, private first publication, cancellation and identity-safe cleanup. They do not establish live telemetry or crash durability.

Actual production WinUI on the active nonprimary96DPI monitor passes exact MetricId/format/order/range edits, Save and apply, normal close, restart and exact persisted ordered rows. CPUBar restores range0–200, FPS text format0.00 and Ring binds RAM. Actual service loss clears values to Unavailable; recovery restores CPUFresh/Measured while gaming remains Stopped/Unknown and CodexError/route_unsupported/Unknown. A locked owned layout rejects saving without changing committed bytes or active rows; unlocking permits retry and actual RAMFresh/Measured. Controlled corruption remains byte-identical, disables overwrite and permits default live metrics; restoring valid bytes reloads normally. No preexisting user layout was replaced; seven defaults were restored explicitly through the UI.

A separately identified native fixture changes only theme selection and a controlled UI-apply exception. It verifies accurate valid-storage/application-failure classification, warning and enabled configuration, and Light/Dark96DPI minimum360x340 dialog actions/reachable MetricId through native scrolling. Final production DLL2DFA7FF8E97C53997797C14FF8D858451D72EE063578B8847B035C1F9AC06648 passes actual-service/configuration/default-reopen/normal close; one153.8664ms close observation is not a guarantee.

Opt-in --secondary-display selects an active nonprimary using Windows App SDK, positions before activation and bounds initial size/minima to its work area; absence exits before constructing/showing a window. Actual96DPI placement passes. FindAll LINQ enumeration failed with InvalidCastException on this SDK; indexed access avoids the failing projection. Early hidden-HWND assertions and summed absolute work-area coordinates are excluded failed attempts. A screen-region capture was occluded and excluded; owned-window PrintWindow captures remain private. Higher physical DPI/cross-DPI, screen-reader, high-contrast and long repeated-rebind retention coverage for013 remain NOT RUN. These limits do not replace M4 exit requirements.

A separately identified slow-save native fixture inserts a cancellable delay before SQLite mutation, waits until the operation enters, then closes the window. Close/cancellation exits normally in one124.9743ms observation and retains committed bytes. This tests the native tracked-operation close path under controlled delay; production timing, crash durability and an in-flight initial-load race are not inferred.

## CER-M4-014 — overview/detail candidate evidence

Final solution Release build PASS: zero warnings/errors, 9.43s. Integration PASS: 97/97, zero skipped, 16.571s. Four new pure regressions cover exact-case deduplication/order/first format without changing saved rows, measured page-height boundaries/four-item cap/all-index retention, explicit oversized/zero-space pages and recovery, and fresh selection/pages after composition or height changes. They do not prove WinUI Measure/arrangement, dispatcher behavior, live reconfiguration or window close. Repository artifact hygiene and diff checks passed; these are not security or performance acceptance.

One independent integrated Sol Web reviewer (Banach) returned CONDITIONAL SOURCE PASS and closed, with no demonstrated P0/P1 or required source correction. The review was qualified against actual confidence presentation: enum.ToString() or the literal No snapshot, not arbitrary provider strings. Three bounded native-evidence checks remain OPEN:

1. State/confidence width with the bounded confidence strings, long safe error codes and large values/units at the supported minimum width.
2. Actual viewport and navigation arrangement: four default IDs on one readable page at the normal viewport; complete pages at small sizes; long labels/values, themes/DPI and explicit oversized-row access through details.
3. Dispatcher enqueue failure/coalescing, page updates during reconfiguration and safe close without late UI/subscription work.

Native/usability/lifecycle and performance are NOT RUN because the secondary display is absent. Earlier CER-M4-013 native evidence does not validate this changed layout. CER-M4-014 CI/publication remain pending; this candidate is not native accepted and M4 remains INCOMPLETE.

## CER-M4-012 — configurable sparkline candidate (integration pending)

Recovered source is byte-identical to the preserved original candidate. Release solution build has zero warnings/errors; Integration103/103 PASS, zero skipped, includes seven sequence and three added composition/store regressions. The existing version1 definition adds an explicit Sparkline renderer; only configuration is stored, never samples. Normal seven defaults remain unchanged. Independent Sol6.1Medium source review found no P0/P1/P2 findings; this is not final committed-head or milestone approval.

Actual production WinUI on the nonprimary96DPI monitor passes configured Sparkline selection/Save, close/restart, real CPUFresh/Measured observations, actual service loss with zero retained samples, recovery and explicit default restoration. A separately built native synthetic fixture passes24 range scenarios per Dark/Light theme at96DPI across normal/minimum sizes, including wrapped large values and state/range text. The corrected harness validates every character inside its scroll retry rather than accepting a partially visible text element. These48 synthetic checks do not resolve the original144DPI failure or constitute actual-provider validation.

Private Light96 instrumentation records21 native geometry frames,11 visible: a zero trace is inset one DIP, actual0/9/10-second instants place the middle point at90% of the available width, and recorded cleared states collapse/remove points. This checks computed native geometry, not raster stroke visibility, full capacity or native stale-opacity proof. The original pure tests cover retention/state changes independently. Two earlier private fixture starts failed because copied restore assets omitted Cerith.Core; regenerating restore fixed that environment. Failed attempts are preserved, not counted as PASS.

The secondary monitor subsequently disappeared from the active topology. A guarded performance launch refused before changing storage or launching a window. Current full-readout/theme conformance, physical144DPI range acceptance, native raster checks and paired performance remain BLOCKED by secondary-display availability or NOT RUN as described. Primary fallback, monitor-topology/resolution edits and unreviewed undocumented scale setters were not used. CER-M4-012 and M4 remain incomplete; no merge is permitted from this evidence alone.

## CER-M4-015 — integrated candidate 2026-10-09 continuation

PR66 head `6e04ec3` plus staged PR65 Sparkline source `12d58e2` is compiled together with the preserved composition/persistence contract. Solution Release build PASS, zero warnings/errors. Direct installed MSTest Release Core39/39, Telemetry398/398 and Integration107/107 PASS, 544/544 with no skips; the normal SDK10.0.401 `dotnet test` no-tests exit5 is excluded. The staged source tree was `0b0019d3`. Separate native integrated receipt on the actual DISPLAY2 at 96 DPI verifies the four overview metric IDs on Page1/1, Save/rebind/restart, real CPU Fresh/Measured, FPS Stopped/Unknown, Codex quota Error/route_unsupported/Unknown, service loss Unavailable and recovery, plus preservation/restoration of the pre-existing layout hash. This receipt does not prove every long-value/page/accessibility/theme state.

A further disconnected minimized four-run baseline/candidate comparison at 96 DPI verifies the actual secondary-only HWND placement, distinct Release hashes, normal close, and byte-identical layout SHA256 before/after; measurements and limitations are in PERFORMANCE.md under CER-M4-015. Higher physical 144 DPI Sparkline glyph/range containment, native raster and Stale opacity, complete Overview keyboard/accessibility/long-state/theme checks, connected configured-Sparkline paired performance, independent final integrated review and exact-head PR checks/merge are OPEN. CodeQL is SKIPPED on the two source draft PRs and cannot be counted as security PASS. M4 remains ACTIVE/INCOMPLETE.
