# AI Quota Reference Matrix

This is a research planning document, not an implementation claim.

## Purpose

For each provider, Cerith should compare multiple independent sources before trusting semantics.

| Provider | Official source to investigate | Reference implementations | Initial Cerith status |
|---|---|---|---|
| Claude | Official account/OAuth/CLI behavior | CodexBar, OpenUsage, others | Not implemented |
| Codex | Official authenticated usage source / CLI | CodexBar, OpenUsage, TokenTracker | Not implemented |
| Antigravity | Official local app/language server quota source | CodexBar, OpenUsage, TokenTracker | Not implemented |
| Copilot | Official GitHub/Copilot usage surfaces | CodexBar, TokenTracker | Future |
| Cursor | Official usage/billing surfaces | CodexBar, TokenTracker | Future |
| Grok | Official xAI/Grok usage/billing surfaces | CodexBar, TokenTracker | Future |

## Rule

A project being listed here does not mean its behavior is automatically correct.

Cerith must verify semantics independently.
