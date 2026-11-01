# v0.1.4 — pending combined validation

**Unreleased experimental candidate; not security-validated. Do not publish these notes as a completed fix until combined validation is recorded.**

- Prepare the next upgrade package for the reported `Replace` / "Unable to remove the file to be replaced" failure. The installer replacement fix is owned by a separate task and is not implemented or validated by this packaging branch.
- Align setup and Windows Apps version metadata at 0.1.4. Inspect the actual built resources, versions, complete runtime sidecars and ZIP bytes before release.
- Download the complete **HytaleAccounts-Setup.zip** and **SHA256SUMS.txt** from the same release, verify the ZIP, extract all files together, close Hytale/launcher, and run setup normally from the same Windows account used for installation. Install over the existing addon; retain accounts, native keys, game data and all shortcut restoration records. Continue using the existing shortcut/search and Windows Apps removal entry.

Combined installer/regression results, exact release source, artifact hashes, actual VM status and live upgrade/account acceptance are **pending**. Replace this paragraph with factual results for the final integrated artifact before publication; retain any outstanding limitations and the experimental warning. The installer is unsigned. See SECURITY-RELEASE.md for mandatory gates and known elevation limitations.
