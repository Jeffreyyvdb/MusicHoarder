# Security Policy

## Supported versions

MusicHoarder is released as a single rolling line via semantic-release. Only the
latest release on the [Releases page](https://github.com/Jeffreyyvdb/MusicHoarder/releases)
receives security fixes. Please upgrade to the latest version before reporting an issue.

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues,
discussions, or pull requests.**

Instead, use GitHub's private vulnerability reporting:

1. Go to the [Security tab](https://github.com/Jeffreyyvdb/MusicHoarder/security) of the repository.
2. Click **"Report a vulnerability"** to open a private advisory.

Please include as much of the following as you can:

- A description of the vulnerability and its impact.
- Steps to reproduce (proof-of-concept code, requests, or configuration).
- The affected version or commit.
- Any suggested remediation, if you have one.

You can expect an initial response within a few days. We'll keep you informed as we
work on a fix, and we're happy to credit you in the release notes once the issue is
resolved (let us know if you'd prefer to remain anonymous).

## Scope and handling secrets

This is a self-hosted application. Operators are responsible for the secrets they
configure (Postgres password, AcoustID/Spotify/Resend keys, etc.). All such values
are sourced from environment variables, AppHost parameters, or user-secrets — never
from tracked files.

If you discover a credential, key, or other secret accidentally committed to this
repository's history, please report it privately using the process above rather than
opening a public issue.
