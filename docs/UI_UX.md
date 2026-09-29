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
