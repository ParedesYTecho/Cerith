# Public Readiness Checklist

Run before changing the repository from private to public.

## History

- [ ] scan full Git history for secrets
- [ ] scan full Git history for personal identifiers
- [ ] scan for private screenshots/logs/payloads
- [ ] scan for machine-specific private paths
- [ ] verify no hidden legacy artifacts

## Code

- [ ] build from clean clone
- [ ] tests pass
- [ ] no hard-coded credentials/endpoints that should be private
- [ ] no fake/mocked telemetry presented as real
- [ ] no private provider fixtures
- [ ] no unexplained generated code

## Quality

- [ ] run zero-AI-slop review
- [ ] every tracked file is intentionally public
- [ ] every dependency has known purpose/license
- [ ] no duplicate/filler docs
- [ ] README describes reality, not aspirations as shipped features

## Licensing

- [ ] final application license chosen
- [ ] plugin SDK license chosen
- [ ] dependency licenses compatible
- [ ] third-party notices present
- [ ] external code provenance documented
- [ ] brand/logo policy defined

## GitHub security

- [ ] secret scanning
- [ ] push protection
- [ ] Dependabot alerts
- [ ] dependency review if available
- [ ] CodeQL/code scanning
- [ ] branch/ruleset protections
- [ ] private vulnerability reporting
- [ ] release permissions minimal

## Identity

- [ ] final project name cleared sufficiently for intended launch
- [ ] official logo/assets original or licensed
- [ ] no Tolkien/other franchise assets copied
- [ ] project-specific security contact ready
