# v0.1.4 — unpublished upgrade candidate

Superseded locally by the pending [v0.1.5 notes](RELEASE-NOTES-v0.1.5.md). Retain this as historical context; do not publish these notes or the old candidate as the next patch.

**Publication blocked: a genuine older-installed-version to packaged-candidate upgrade in a disposable Windows environment has not been validated. Not security-validated.**

- Integrate all three upgrade repair branches. The reported `Replace` / "Unable to remove the file to be replaced" failure is reproduced by the released restrictive directory pin; the existing production repair permits journal publication while retaining directory replacement protection.
- Align setup and Windows Apps version metadata at 0.1.4. Inspect the actual built resources, versions, complete runtime sidecars and ZIP bytes before release.
- Download the complete **HytaleAccounts-Setup.zip** and **SHA256SUMS.txt** from the same release, verify the ZIP, extract all files together, close Hytale/launcher, and run setup normally from the same Windows account used for installation. Install over the existing addon; retain accounts, native keys, game data and all shortcut restoration records. Continue using the existing shortcut/search and Windows Apps removal entry.

The focused real-worker lifecycle passes fresh installation, lock rejection/retry, changed-payload upgrade and same-version reinstall in a synthetic fixture. Full safe checks, mutation controls and fresh packaging are run separately and recorded in their generated reports; none substitutes for packaged upgrade acceptance. See INTEGRATION-UPGRADE.md for source and evidence locations. No real UAC, cross-user VM enforcement, packaged upgrade, Windows Settings acceptance or live login/switch/logout/Play validation is established. The installer is unsigned. Do not publish until the required packaged upgrade gate is satisfied; retain experimental prerelease status until the remaining policy gates pass.
