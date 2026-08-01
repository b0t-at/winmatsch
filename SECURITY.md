# Security policy

## Supported versions

winmatsch is pre-1.0. Only the latest release (and the current `main`
branch) receive security fixes.

## Reporting a vulnerability

Please report suspected vulnerabilities **privately** through
[GitHub security advisories](https://github.com/b0t-at/winmatsch/security/advisories/new).
Do not open public issues or pull requests for security problems.

Include, where possible: the version (`winmatsch --version`), platform, a
reproduction (with secrets redacted), and your assessment of impact. You
should receive an acknowledgement within a week.

## Scope notes

The threat model and hardening measures (token storage, redaction,
untrusted-input bounds, mutation guarantees) are documented in
[docs/security.md](docs/security.md). Reports about installers themselves
being malicious are out of scope — winmatsch performs static analysis only
and never executes installers — but bypasses of the documented bounds
(archive-limit escapes, redaction gaps, secret leakage) are very much in
scope.
