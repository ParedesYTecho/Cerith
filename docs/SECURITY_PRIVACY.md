# Security & Privacy Architecture

## Default stance

Local-first.

Cerith must not require a cloud account to monitor the local machine.

## Secrets

Never store raw secrets in:

- repository;
- logs;
- screenshots;
- SQLite;
- telemetry history;
- crash reports.

Use Windows credential facilities for supported secrets.

## Provider authentication

Prefer read-only reuse of official local authentication where legally/technically appropriate.

Never expose provider credentials through plugin APIs by default.

## Logs

Sanitize:

- tokens;
- cookies;
- authorization headers;
- personal identifiers when unnecessary;
- raw provider payloads containing private information.

## Plugins

Plugins are a future trust boundary.

Public plugin design should include:

- declared permissions;
- install source;
- signature/hash metadata;
- disable/remove;
- diagnostics.

## Network

Use:

- TLS;
- bounded responses;
- timeout;
- backoff;
- explicit user-agent;
- no arbitrary untrusted endpoint execution.

## Product analytics

No analytics by default.

If telemetry is ever added, it must be explicit, minimal, inspectable and opt-in.
