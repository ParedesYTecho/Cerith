# Cerith UI / UX

## Goal

Cerith should eventually be recognizable from a screenshot.

External references expand the design vocabulary; they do not define the product.

## Principles

- dense but calm;
- clear hierarchy;
- immediate feedback;
- honest states;
- purposeful motion;
- excellent DPI support;
- keyboard and mouse first-class;
- accessible;
- premium without noise;
- highly configurable without requiring design expertise.

## Visual editor

Conceptually inspired by professional design tools.

Core interactions:

```text
canvas
layers
selection
drag/drop
resize
snap
alignment
group
duplicate
copy/paste
undo/redo
property inspector
data binding
rule editor
preview
```

## Design tokens

Centralize:

```text
Typography
Spacing
Radius
Border
Shadow
Material
Opacity
State colors
Metric colors
Motion
Chart styling
```

No scattered arbitrary magic values.

## Motion

Motion communicates cause, state and continuity.

Tokens:

```text
motion.instant
motion.fast
motion.standard
motion.emphasized

ease.enter
ease.exit
ease.move

spring.soft
spring.snappy
```

Rules:

- interruptible;
- compositor-friendly where possible;
- never block input unnecessarily;
- respect reduced motion;
- avoid animation merely for spectacle.

## ComponentLab

Maintain an internal development gallery:

```text
Buttons
Toggles
Cards
Metric blocks
Gauges
Graphs
Sparklines
Tooltips
Menus
Drag handles
Resize handles
Dialogs
Notifications
Loading
Measured
Estimated
Stale
Unavailable
Error
Themes
Motion
```

Test:

```text
light/dark
100–200% DPI
keyboard
mouse
reduced motion
long text
missing data
loading
stale
error
disabled
hover
pressed
```

## Compact monitoring surfaces

Cerith should support a continuum of information density rather than treating each shell location as a separate product.

Reference roles:

- **Gengchou**: taskbar badges, floating rows, tray indicators and a detail popup that expose one quota model at different densities; warning takeover, hover detail, Explorer/RDP recovery and deterministic render fixtures are especially valuable.
- **AI Limits**: Compact / Medium / Expanded detail levels, responsive width tiers, horizontal/vertical bars, adaptive provider count, palette/opacity controls and a minimal taskbar/tray fallback.
- **TokenTracker**: information-rich dashboard and desktop-widget composition for usage, activity, model breakdown and limits.

Cerith interpretation:

```text
MetricId / history
      ↓
semantic component
      ↓
density/layout policy
      ↓
desktop · floating · taskbar · tray · dashboard
```

Rules:

- the same metric state must mean the same thing on every surface;
- a compact surface may select a headline/urgent metric, but it must not invent or rewrite provider semantics;
- hover/click/expanded views reveal detail rather than cramming every field into the smallest surface;
- stale, unavailable, error and action-required states remain visually honest at every density;
- width/DPI/theme changes reflow deliberately instead of overlapping or truncating silently;
- taskbar/tray work must survive ordinary Explorer rebuilds and multi-monitor changes;
- surface implementations remain MetricId-driven; no Claude/Codex-specific widget classes.

## Diagnostic Test session (future)

ControlCenter should eventually expose a clear user-invoked Test / Diagnostic Session entry point for validating telemetry during a real workload.

The interaction should make the session boundary obvious:

```text
Start test
→ choose/confirm target or active session
→ visible recording state + elapsed time
→ Stop
→ summary of captured metrics/diagnostics
→ inspect/export/analyze
```

The UI must show when diagnostic capture is active and must not imply that ordinary monitoring is collecting the same dense diagnostic evidence. A future comparison view may align Cerith data with an independently captured trace from another telemetry tool and highlight semantic/timing differences instead of forcing false agreement.

This is future validation tooling, not part of the current M2 UI or exit criteria.

## Reference library

High-value references:

- 21st.dev;
- UI Skills;
- 60fps.design;
- transitions.dev;
- Emil Kowalski UI material;
- Mobbin;
- Refero;
- Component Gallery;
- Beautiful UI;
- Rare UI;
- ReUI;
- Fancy Components;
- Motion Primitives;
- toggles.dev.

Use them to study:

- hierarchy;
- spacing;
- interaction;
- motion;
- feedback;
- onboarding;
- editor patterns.

Do not transplant a web stack into the runtime.

## Reference workflow

```text
UX problem
→ collect 3–5 references
→ identify why they work
→ extract hierarchy/motion/feedback
→ design an original native version
→ implement in ComponentLab
→ accessibility/performance review
→ promote into product
```

## Avoid AI-slop UI

Never instruct an agent only:

> make this premium

Instead provide:

```text
goal
references
what to borrow conceptually
what not to copy
design tokens
native constraints
accessibility
acceptance states/screenshots
```

## Initial preset directions

- calm glass/bento;
- high-information tactical/HUD;
- compact minimalist.

Presets share one semantic component system.

## UI honesty

Measured, estimated, stale, unavailable, error and loading states must be visually distinguishable.

A beautiful wrong number is worse than `--`.

## Earlier composable demonstration (CER-M4-006)

The bounded native demonstration uses CPU total usage and RAM usage (Hardware), displayed FPS (Gaming), and Codex primary quota remaining (AI). CPU and quota each appear as both `MetricReadout` and `MetricBar`, sharing their exact MetricId stream. FPS remains textual. Native layout and the existing themed edge/state rail are retained; a ScrollViewer accommodates the larger content at the existing minimum size. The intended default is 420×620 logical pixels. This is not the Hardware page, a layout editor or a final container framework.

`MetricRangeSpec` supplies a presentation spec plus an explicit finite increasing Minimum/Maximum. Range belongs to the component, not the descriptor; no MetricId, provider, source or unit inference chooses it. The current two percentage bars configure 0–100. A bar embeds the existing readout for descriptor label/unit, actual value, state and confidence, and adds a themed native fill plus range text. Fresh/Stale retain their finite value and confidence, with stale text/opacity; real zero shows an empty valid track. Unavailable/Stopped/Error/Unlimited have no numeric track; Unlimited remains textual. Values above/below range suppress fill and explicitly say Above range/Below range while preserving the actual numeric text. Non-finite or nonnumeric values likewise have no numeric fill. Connecting/Waiting and transport loss show no snapshot/value. Missing metadata uses the exact MetricId without an invented unit.

No component tree is rebuilt for numeric changes. Readout presentation/label equality and bar fill/range/Automation-name guards avoid unnecessary mutation. Native visual, theme, DPI and paired process-performance acceptance must be recorded separately from headless transport/presentation tests.

At 96 DPI, default and minimum sizes passed dark/light inspection and native scrolling. Minimum dimensions belong to the outer AppWindow presenter; do not duplicate them as client Grid minima, because the caption/frame reduce the visible client area and can clip the final component. The bottom range must be fully inside both the native client rectangle and scroll viewport. CER-M4-008 additionally verifies the current surface at actual 144 DPI in both themes and live 96→144→96 transitions on one monitor. Explicit PerMonitorV2 belongs to the build-consumed application manifest; live XamlRoot scale changes update only presenter minima, preserving user sizing. Mixed-monitor hardware transitions remain unverified; detailed native evidence and failed attempts are recorded in TESTING_QA.

## Earlier native readout overflow conformance

This records the earlier accepted readout layout. The CER-M4-014 candidate below changes wrapping and state/confidence placement; the earlier native evidence does not validate that candidate.

State and confidence share finite Grid columns: state uses the remaining width and confidence reserves its native desired width. A long safe error code wraps without pushing confidence out of the row. The decorative separator is omitted to keep short states readable beside a long value at the minimum width. Values wrap within the existing width cap instead of ellipsis so a large out-of-range number retains its actual text. Labels retain intentional trimming and their full accessible name. Taller exceptional rows remain inspectable through the existing ScrollViewer; range inspection is separate from bringing the full readout into view. This is the same readout for standalone and bar consumers, with unchanged state/confidence and Automation-name semantics.

## Native ranged ring (CER-M4-010)

MetricRing embeds the same MetricReadout and uses the same internal MetricRangeSpec/Result/Presentation as MetricBar. The current CPU ring explicitly configures 0–100 and shares CPU's existing subscription with its readout and bar: seven components over four exact MetricIds. No provider/unit/MetricId inference selects a range. An in-range zero keeps an empty themed circular track; values between zero and full range use one clockwise static WinUI ArcSegment, and the upper endpoint uses a full Ellipse. Stale retains value and confidence with subdued fill. Valueless, Unlimited, nonnumeric/nonfinite and out-of-range states have no numeric visual; actual text and range explanations remain. Existing range validation/text owns the shared semantics; the ring owns only its fixed native geometry and presentation.

The installed native determinate ProgressRing was tested before choosing geometry. At 96 DPI its accessible Value tracked the input, but the painted result depended on the previous value: 10→11 retained the exact 10% raster, whereas zero→11 produced another raster; 11→12 likewise retained 11%. Captures were taken after 1.2 s settling, with source-qualified synthetic protocol values. The static candidate produces distinct 10/11/12 rasters and identical repeated 11/12 rasters irrespective of the preceding value. This observed defect is the reason for the original minimal geometry; no upstream code/template is copied. There is no player, repeating animation, animation callback or second component framework. Native theme/DPI, minimum-size glyph conformance, real lifecycle and paired process-performance gates remain separate from this rendering check; bounded native/lifecycle/performance evidence is recorded in TESTING_QA and PERFORMANCE; exact-head review/CI/integration remain required.

## Basic composition configuration (CER-M4-013)

WidgetHost provides one native configuration dialog for the ordered Text/Bar/Ring composition. Select a row and edit component kind, exact MetricId, numeric format and explicit range where required; Add/Remove and Up/Down change the draft order. Save and apply validates and persists the draft before replacing active subscriptions; Cancel leaves the active and saved composition untouched. This is bounded M4 configuration, not the M5 visual editor. Standard WinUI resources, keyboard access, named fields and scrolling support the compact window and available DPI.

Missing storage uses the existing unsaved seven-component default. Corrupt, unsupported or unreadable existing storage remains preserved; defaults display with a persistent warning and saving over that file is blocked. Sanitized errors distinguish failed saving from a successfully saved composition whose live application failed. Metric labels, units, values and states continue to come from exact-ID descriptor/snapshot streams; none are persisted as configuration.

CER-M4-013 native acceptance verifies ordered Text/Bar/Ring configuration, exact bindings/formats/ranges, Save/restart restoration and truthful storage/application warnings on a secondary96DPI monitor. Existing WinUI controls scroll at360x340; separately qualified theme probes verify Light/Dark action bounds and reachable fields. --secondary-display provides opt-in placement before visibility without monitor changes or an ordinary-launch change. Higher physical/cross-DPI013 validation remains unverified; this is not M4 visual exit.

## Overview and composition details candidate (CER-M4-014)

The normal native Overview uses one existing MetricReadout per distinct exact-case MetricId, in saved first-occurrence order and with that row's configured numeric format. Composition details explicitly opens the full ordered Text/Bar/Ring composition. Both views consume the same prepared subscriptions; switching views adds no read loops and changes neither the saved definition nor the seven-component default.

Overview has no horizontal or vertical scrolling. Pages contain at most four complete readouts, using native measurements at the available width and the viewport height after navigation is arranged. Page/count navigation retains every selected ID; smaller viewports or taller presentations may require more pages. The current metric anchor survives repagination, while applying a new composition resets selection. An oversized single readout has an explicit page and a resize/details warning rather than cropped telemetry. This fallback does not establish that every payload fits the normal viewport.

Labels and values wrap; state and confidence occupy a full-width row with 12-DIP text and unchanged semantic/accessible-name behavior. Details and configuration may scroll. This is a source-reviewed candidate, not native acceptance: default-four-on-one-page, bounded state/confidence width, actual navigation/viewport geometry and native lifecycle evidence remain open in TESTING_QA.

The CER-M4-012 candidate extends this same dialog with Sparkline and an explicit finite range. Its component retains at most60 Fresh observations privately, uses actual observation instants on the horizontal axis, freezes Stale and starts anew on the next Fresh observation, and clears valueless/invalid/Unlimited/out-of-range sequences. Existing readout states/range text remain visible; fewer than two points show no curve. Configuration stores only kind, exact MetricId, format and range. This candidate remains unmerged pending native144DPI conformance and other acceptance gates; it introduces no history subsystem or editor model.
