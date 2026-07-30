# Security Policy

## Reporting a Vulnerability

Please use this repository's **Security** tab and submit a private vulnerability
report. Do not open a public issue for authentication bypasses, token exposure,
unsafe command routing, path traversal, local repository tampering, screenshot
disclosure, replay attacks, or other vulnerabilities.

Include the affected commit or version, the operating conditions, a minimal
reproduction, and the security consequence. Redact player identity, access
tokens, local paths, screenshots, logs, and plugin configuration before
attaching evidence.

## Supported Code

Security fixes target the current `local-dev` integration branch and are carried
to `main` when released. Older commits and locally modified builds may not
receive fixes.

## Security Boundary

DAB is a local development tool. Its HTTP surface must remain loopback-only,
bridge tokens must remain protected for the current Windows user, and every
agent action must pass both DAB's policy and the target plugin's declared
capability boundary. A local-only deployment is not permission to weaken
authentication, validation, or data minimization.
