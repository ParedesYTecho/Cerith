# Plugin System

## Goal

Allow community integrations without modifying Cerith Core.

Potential plugin categories:

```text
Metric Provider
Widget Component
Rule Action
Data Sink
Remote Source
Theme Pack
```

## Principles

- versioned contracts;
- explicit capabilities;
- graceful failure;
- diagnostics;
- measurable performance;
- no implicit secret access;
- isolation where practical.

## Permissions

Future plugin permissions may include:

```text
network
process list
hardware
filesystem
credential access
notifications
local server
```

Permissions must be visible to users.

## M0/M1 scope

Do not block the foundation while designing a perfect plugin sandbox.

Initial requirement:

- internal provider boundary clean enough to evolve into public contracts later.

Public plugin SDK comes after the core contracts stabilize.
