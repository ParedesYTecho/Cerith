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

## Current composable surface (CER-M4-006)

The bounded native demonstration uses CPU total usage and RAM usage (Hardware), displayed FPS (Gaming), and Codex primary quota remaining (AI). CPU and quota each appear as both `MetricReadout` and `MetricBar`, sharing their exact MetricId stream. FPS remains textual. Native layout and the existing themed edge/state rail are retained; a ScrollViewer accommodates the larger content at the existing minimum size. The intended default is 420×620 logical pixels. This is not the Hardware page, a layout editor or a final container framework.

`MetricBarSpec` supplies a presentation spec plus an explicit finite increasing Minimum/Maximum. Range belongs to the component, not the descriptor; no MetricId, provider, source or unit inference chooses it. The current two percentage bars configure 0–100. A bar embeds the existing readout for descriptor label/unit, actual value, state and confidence, and adds a themed native fill plus range text. Fresh/Stale retain their finite value and confidence, with stale text/opacity; real zero shows an empty valid track. Unavailable/Stopped/Error/Unlimited have no numeric track; Unlimited remains textual. Values above/below range suppress fill and explicitly say Above range/Below range while preserving the actual numeric text. Non-finite or nonnumeric values likewise have no numeric fill. Connecting/Waiting and transport loss show no snapshot/value. Missing metadata uses the exact MetricId without an invented unit.

No component tree is rebuilt for numeric changes. Readout presentation/label equality and bar fill/range/Automation-name guards avoid unnecessary mutation. Native visual, theme, DPI and paired process-performance acceptance must be recorded separately from headless transport/presentation tests.

At 96 DPI, default and minimum sizes passed dark/light inspection and native scrolling. Minimum dimensions belong to the outer AppWindow presenter; do not duplicate them as client Grid minima, because the caption/frame reduce the visible client area and can clip the final component. The bottom range must be fully inside both the native client rectangle and scroll viewport. Higher physical DPI and live scale transitions remain separate F5 acceptance work.
