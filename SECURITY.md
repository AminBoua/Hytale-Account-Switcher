# Security policy

## Supported versions

Only the newest experimental release receives fixes. Versions 0.1.0 through 0.1.2 are unsupported and lack the latest account recovery and installer fixes. Do not use those installers.

The current release is experimental. Synthetic tests do not establish real UAC, SYSTEM-task, unrelated-user enforcement or live Hytale compatibility. See [the release gate](SECURITY-RELEASE.md) for the outstanding validation.

## Reporting concerns

Use [GitHub Discussions](https://github.com/AminBoua/Hytale-Account-Switcher/discussions) for support and to request a private security-reporting channel. There is no email contact.

**Discussions and issues are public once the repository is public.** For a suspected vulnerability, initially say only that you need a private reporting channel. Do not post exploit details, proof-of-concept code, private paths, account identities or unreviewed logs there. When GitHub private vulnerability reporting is enabled, use [Report a vulnerability](https://github.com/AminBoua/Hytale-Account-Switcher/security/advisories/new) for those details. This feature is unavailable while the repository is private; if the link is unavailable, wait for a maintainer to establish a private channel before sharing details.

Never upload `account.dat`, `.keys`, `Accounts`, session snapshots, or browser authentication URLs. A useful private report includes the addon/Windows/launcher versions, a description of the impact, and reproduction steps using synthetic data.

## Publication settings

Before public announcement, the maintainer must enable and verify GitHub private vulnerability reporting, and require the `Windows safe checks` status check on `main`. GitHub currently refuses branch protection on this private repository's plan; enable it after changing visibility or upgrading the plan. Keep force pushes and branch deletion disabled, require pull requests, and apply the rule to administrators.

Publish installers as prereleases until the documented VM and live checks have passed. Keep the installer, its matching checksum, source revision and license notices together. Do not describe CI or simulated checks as a real VM pass.
