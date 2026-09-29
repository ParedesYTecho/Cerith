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

## Diagnostic sessions

Future Test / Diagnostic Session capture is explicit, bounded and user-initiated.

By default it stays local and may record all Cerith-observable metric samples for the selected session plus the sanitized diagnostics needed to interpret them. It must not expand “capture everything” into unrestricted collection of:

- credentials or secrets;
- cookies or authorization material;
- arbitrary process command lines;
- unnecessary file-system paths;
- unrelated window titles;
- unrelated user activity;
- raw private provider payloads.

The UI should clearly indicate when capture is active. Before export, the bundle should be inspectable and have a defined schema/content inventory. External comparison traces are imported or referenced deliberately; Cerith does not silently upload session data to a third party.

## Product analytics

No analytics by default.

If telemetry is ever added, it must be explicit, minimal, inspectable and opt-in.
