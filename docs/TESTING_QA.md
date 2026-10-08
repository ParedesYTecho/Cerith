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
