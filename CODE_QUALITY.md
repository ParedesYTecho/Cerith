# Code Quality Policy — Zero AI Slop

Cerith is expected to become a public open-source project.

The codebase must look intentional even if much of the implementation is assisted by AI.

## 1. Definition of AI slop

AI slop is code or documentation that exists because a model generated it, rather than because the product needs it.

Examples:

```text
unnecessary layers
redundant wrappers
duplicate docs
generic filler comments
unused files
speculative interfaces
unnecessary dependencies
tests with no behavioral value
verbose boilerplate
fake completeness
```

## 2. File existence rule

**Every new file must justify its existence.**

A new file is appropriate when it has a clear, cohesive responsibility that would make an existing file materially worse if merged into it.

A new file is NOT justified by:

- “clean architecture” aesthetics;
- one tiny type that is only used once;
- a model preferring many small files;
- hypothetical future use;
- mirroring a folder structure from another framework.

During review, any reviewer may ask:

> Why does this file exist?

The answer must be concrete.

## 3. Abstraction budget

Start concrete.

Extract an abstraction when one of these becomes true:

- there are multiple real implementations;
- testing requires a boundary around external effects;
- a stable public/plugin contract is needed;
- coupling measurably harms maintainability.

Do not create abstractions based solely on predicted future needs.

## 4. Dependency budget

New dependencies require justification.

For each package record:

- purpose;
- why built-in/existing APIs are insufficient;
- runtime cost;
- maintenance/security implications;
- license.

Prefer native Windows/.NET capabilities when they reasonably solve the problem.

## 5. Comment policy

Good comments explain:

- why;
- non-obvious constraints;
- external contracts;
- dangerous edge cases;
- performance reasoning.

Bad comments explain what the next line visibly does.

Bad:

```csharp
// Increment counter
counter++;
```

Good:

```csharp
// Keep the sequence monotonic across reconnects so subscribers can detect replayed snapshots.
sequence++;
```

## 6. TODO policy

No anonymous TODOs.

Use one of:

```text
TODO(CER-123): ...
FIXME(CER-123): ...
```

or a task reference in `.agent/TASKS.md` during pre-issue bootstrap.

## 7. Testing policy

Tests must protect behavior or a meaningful contract.

Avoid tests for:

- automatic properties;
- framework behavior;
- trivial constructors;
- implementation details with no user/system consequence.

Prefer:

- invariants;
- edge cases;
- serialization contracts;
- provider normalization;
- concurrency behavior;
- regression cases.

## 8. Documentation policy

One fact has one canonical home.

Do not create:

```text
README2.md
ARCHITECTURE_SUMMARY.md
NEW_ARCHITECTURE.md
FINAL_ARCHITECTURE_FINAL.md
```

Update the canonical document instead.

Agent session notes belong in `.agent/`, not in permanent product docs.

## 9. Naming policy

Avoid vague names:

```text
DataManager
CommonHelper
Utils
Stuff
BaseService
GenericProvider
```

Names should describe responsibility.

## 10. PR quality gate

A change should not merge if it contains:

- unexplained new files;
- unexplained new packages;
- dead code;
- placeholder telemetry;
- secrets/private data;
- duplicated canonical documentation;
- unverified performance claims;
- unresolved warnings introduced by the change;
- broad unrelated rewrites.

## 11. Diff discipline

Prefer small reviewable diffs.

If an agent changes hundreds of unrelated lines due to formatting, file moves or refactoring, revert/split unless those changes are the task.

## 12. Human readability

The codebase should remain understandable to a competent developer without reading AI conversations.

If meaning exists only in a prompt or handoff, it is not sufficiently encoded in the project.
