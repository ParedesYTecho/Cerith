# Model Routing

Operational preference only. This may change frequently.

## Default

Use the cheapest/fastest model that reliably solves the bounded task.

### High-throughput implementation

Current preference:

```text
GPT-6 Luna Max
```

for:

- bounded implementation;
- tests with explicit acceptance;
- DTOs/contracts;
- straightforward refactors;
- simple adapters;
- mechanical migrations.

### Strong review/architecture

Use strongest available reasoning for:

- concurrency;
- IPC;
- security;
- credential handling;
- plugin isolation;
- public contracts;
- difficult provider integrations;
- architectural review.

### Independent second worker

Antigravity can take independent non-overlapping tasks or perform second-pass review.

## Rule

Premium reasoning should not be spent on mechanical work.

Cheap throughput should not be trusted blindly on high-risk architecture.
