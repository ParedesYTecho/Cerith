# Cerith Task Prompt Template

Work on task:

```text
<TASK-ID>
```

Before editing:

- read `AGENTS.md`;
- read `.agent/STATE.md`;
- read `.agent/HANDOFF.md`;
- read the task in `.agent/TASKS.md`;
- inspect `git status --short`;
- query Graphify/code graph for relevant symbols if configured;
- read only relevant code/docs.

Goal:

```text
<goal>
```

Acceptance criteria:

```text
- ...
- ...
```

Allowed scope:

```text
- ...
```

Do not:

- modify unrelated modules;
- create unneeded files;
- create speculative abstractions;
- add dependencies without justification;
- invent telemetry;
- duplicate canonical docs;
- commit secrets/private information.

Verification:

```text
- ...
```

Before handoff:

- build/test;
- review diff;
- justify each new file/dependency;
- remove AI slop;
- update task/worklog/state/handoff;
- refresh graph if structural.
