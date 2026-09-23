# Contributing to Cerith

Cerith is currently in early foundation development.

## Principles

Contributions must preserve:

- truthful telemetry;
- low runtime overhead;
- native Windows-first UX;
- privacy;
- clean architecture;
- zero-AI-slop standards.

Read:

- `CODE_QUALITY.md`
- `SECURITY.md`
- `AGENTS.md`
- relevant docs under `docs/`

## Branches

Do not work directly on `main`.

Use focused branches such as:

```text
feat/CER-123-metric-registry
fix/CER-204-codex-reset
docs/CER-310-provider-contract
```

## Pull requests

A PR should:

- solve one coherent problem;
- explain why;
- list verification performed;
- identify new dependencies/files;
- include screenshots for visible UI changes;
- avoid unrelated formatting/refactors.

## AI-assisted contributions

AI assistance is welcome.

AI-generated output receives no lower review standard.

The contributor remains responsible for:

- correctness;
- license provenance;
- security;
- testing;
- code quality;
- understanding the change.

Do not submit model output you do not understand.

## External code

Do not copy external component/provider code without:

- identifying its license;
- checking compatibility;
- preserving required notices;
- documenting provenance where appropriate.

## Telemetry correctness

Never submit fabricated or inferred provider values as authoritative measurements.

If a provider does not expose a value, represent the missing state honestly.
