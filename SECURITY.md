# Security Policy

## Security posture

Cerith is developed as **public-ready from the first commit**.

Treat all committed content as potentially public.

## Reporting

Before the repository is public, security reports should be handled privately by the repository owner.

When the project becomes public, configure GitHub private vulnerability reporting before publishing a contact path here.

Do not publish a personal email address in this file until a project-specific contact is ready.

## Never commit

- API keys;
- OAuth tokens;
- refresh tokens;
- cookies;
- bearer headers;
- browser profiles;
- `.env` files containing secrets;
- credential exports;
- provider auth files;
- raw private screenshots;
- unsanitized provider responses;
- personal machine paths containing identifying information;
- local agent/chat context.

## Secret response

If a secret is committed:

1. revoke/rotate the secret first;
2. remove it from current files;
3. assess whether Git history must be rewritten;
4. assume it was exposed even if the repository was private;
5. document the incident without copying the secret.

Deleting a secret from the latest commit is not sufficient.

## GitHub security baseline

Before making the repository public, enable where available:

- secret scanning;
- push protection;
- dependency graph;
- Dependabot alerts;
- Dependabot security updates;
- CodeQL/code scanning;
- private vulnerability reporting;
- branch/ruleset protections for `main`.

## Build provenance

Official releases should eventually use:

- signed release artifacts;
- reproducible documented build steps where practical;
- checksums;
- release CI from protected branches/tags.

## Third-party plugins

Plugins are a future security boundary and must never inherit unrestricted credentials or filesystem access by default.
