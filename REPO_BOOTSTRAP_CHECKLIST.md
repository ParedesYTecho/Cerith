# Cerith Repository Bootstrap Checklist

## Before first commit

- [ ] clean project directory
- [ ] no WidgetOSD source copied in
- [ ] `git init`
- [ ] `.gitignore`
- [ ] `.gitattributes`
- [ ] `.editorconfig`
- [ ] `CODE_QUALITY.md`
- [ ] `SECURITY.md`
- [ ] `CONTRIBUTING.md`
- [ ] scan working tree for secrets/private paths
- [ ] inspect `git status`
- [ ] inspect staged diff before commit

## GitHub repository

Start private.

Configure:

- [ ] no public repository until Public Readiness Audit passes
- [ ] default branch `main`
- [ ] disable direct development on `main`
- [ ] ruleset/branch protection when available
- [ ] require PRs/checks once CI exists
- [ ] Dependabot config
- [ ] CodeQL workflow
- [ ] push protection / secret scanning when available
- [ ] private vulnerability reporting before public launch

## Development agents

- [ ] Codex opens repository root
- [ ] Antigravity opens repository root
- [ ] Graphify/shared code graph configured after skeleton exists
- [ ] both read the same canonical docs
- [ ] task ownership verified
- [ ] `.agent/private/` remains gitignored

## .NET bootstrap

- [ ] `Cerith.sln`
- [ ] `Cerith.Core`
- [ ] `Cerith.TelemetryService`
- [ ] `Cerith.WidgetHost`
- [ ] `Cerith.ControlCenter`
- [ ] test projects
- [ ] build succeeds
- [ ] no unnecessary packages

## First milestone

- [ ] synthetic provider
- [ ] MetricSnapshot
- [ ] MetricBus
- [ ] WidgetHost displays synthetic value
- [ ] tests
- [ ] baseline resource measurement
