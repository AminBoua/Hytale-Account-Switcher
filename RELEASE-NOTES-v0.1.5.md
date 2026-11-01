# v0.1.5 — pending setup/storage patch

**Unpublished. Blocked on focused repair integration, fresh combined checks and genuine packaged older-version upgrade acceptance. Not security-validated.**

- Pending: integrate and review setup sharing-conflict and storage-permission diagnostics. The original setup lock owner remains unconfirmed; unsafe storage ACLs must continue to reject. Do not claim either reported failure is resolved from this coverage branch alone.
- Prepare consistent launcher/Windows Apps version 0.1.5. Strengthen exact recovery/data permission preservation and static package inspection, including duplicate ZIP rejection and retained resource/sidecar hashes.
- Use the complete **HytaleAccounts-Setup.zip** and **SHA256SUMS.txt** from the same release. Verify and extract all files together; close Hytale/launcher and run setup normally as the existing installation's Windows user. Keep the existing shortcut/search entry, accounts, keys, game data, journals and original shortcut backups. Remove through Windows Settings > Apps.

The source fixtures use real Windows PowerShell and native file handles with redirected paths, mock tasks/registration and intercepted elevation. They do not establish actual UAC/cross-user enforcement, Windows Settings acceptance or a genuine packaged upgrade. Both retained v0.1.4 candidates are unpublished; pushing source does not replace a release download. The installer is unsigned. Keep experimental prerelease status under the existing security policy.

Before publication, replace the pending statements with the integrated commit, package hashes, focused report conclusions and actual validation results from [SETUP-RELEASE-REGRESSIONS.md](SETUP-RELEASE-REGRESSIONS.md). Record all remaining limits, including actual VM and live login/switch/logout/Play checks if unfinished.
