# v0.1.5 — pending setup/storage patch

**Unpublished test candidate. Genuine packaged older-version upgrade acceptance and actual VM/live validation remain unverified. Not security-validated.**

- Integrated setup sharing-conflict and storage-permission diagnostics. Lock failures retain the attempted path, native error, PowerShell caller and PID. The original setup lock owner remains unconfirmed; this is not a confirmed fix for that failure.
- The reported storage directory has an explicit BUILTIN\Users Full Control grant and is correctly rejected. No live ACL was changed; reviewed, narrowly scoped remediation remains necessary. See STORAGE-PERMISSION-REJECTION.md.
- Launcher/Windows Apps version is consistently 0.1.5. Strengthened exact recovery/data permission preservation and static package inspection, including duplicate ZIP rejection and retained resource/sidecar hashes.
- Use the complete **HytaleAccounts-Setup.zip** and **SHA256SUMS.txt** from the same release. Verify and extract all files together; close Hytale/launcher and run setup normally as the existing installation's Windows user. Keep the existing shortcut/search entry, accounts, keys, game data, journals and original shortcut backups. Remove through Windows Settings > Apps.

The source fixtures use real Windows PowerShell and native file handles with redirected paths, mock tasks/registration and intercepted elevation. They do not establish actual UAC/cross-user enforcement, Windows Settings acceptance or a genuine packaged upgrade. Both retained v0.1.4 candidates are unpublished; pushing source does not replace a release download. The installer is unsigned. Keep experimental prerelease status under the existing security policy.

See [INTEGRATION-SETUP-STORAGE.md](INTEGRATION-SETUP-STORAGE.md) for the integrated source, candidate hashes and combined validation results. Publication is intentionally withheld at the user's request while they test the candidate. No actual packaged older-version upgrade, UAC/cross-user matrix, Settings removal or live login/switch/logout/Play acceptance has been established.
