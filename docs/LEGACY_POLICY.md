# WidgetOSD Legacy Policy

## Decision

Cerith starts from a clean repository.

WidgetOSD is not its architectural base.

## Quota subsystem

The legacy quota implementation is explicitly untrusted.

Do not port:

- quota calculations;
- percentage mapping;
- provider fallbacks;
- window classification;
- cache semantics;
- UI quota semantics.

Implement Cerith quota providers from zero.

## What may be reused after independent verification

- sanitized real response shapes;
- discovered edge cases;
- hardware sensor mappings;
- DPI/multi-monitor lessons;
- security lessons;
- QA scenarios.

## Reuse test

Before copying any legacy code/data ask:

```text
Does the requirement still exist?
Is behavior independently verified?
Does it fit Cerith architecture?
Is reuse better than reimplementation?
Is provenance/license clear?
```

If unclear, reimplement.

## Repository handling

Keep legacy outside the Cerith repository whenever possible.
