# Security policy

FrostLock blocks keyboard and mouse input, so a bug can leave someone unable to use their PC. Reports about a freeze that will not release are welcome and get priority.

## Supported versions

| Version | Supported |
| --- | --- |
| 1.4.x | Yes |
| 1.3.x | Yes |
| 1.2.x and earlier | No |

## Reporting a vulnerability

Use GitHub's private reporting form:

https://github.com/FurtherSecrets24680/FrostLock/security/advisories/new

Please do not open a public issue for an unpublished vulnerability. If private reporting is unavailable, open an issue that says only that you have a security report and how to contact you, without details.

Include the FrostLock version, Windows build, freeze mode, and the steps that reproduce the problem. A screen recording or the exact `--status` output helps.

## What is in scope

- A freeze that cannot be released through the documented paths, or input that stays blocked after FrostLock exits or crashes.
- A way to release a PIN lock without the PIN, the emergency phrase, an administrator, or the documented Windows escape routes.
- A PIN or emergency phrase that is stored or logged in a readable form, or a way to recover it from `settings.json`.
- Update-download tampering that causes FrostLock to install an asset whose bytes do not match GitHub's published SHA-256 digest, or to install automatically when the digest is missing or invalid.
- Privilege escalation through the elevated relaunch or the installer.
- Unlocking or bypassing the lock screen by a non-administrator who does not know the secret.

## What is out of scope

These are documented behaviour, not vulnerabilities:

- `Ctrl+Alt+Del`, Task Manager on the secure desktop, safe mode, and killing the process.
- A local administrator ending a freeze.
- Physical access, including unplugging the machine or booting another OS.
- USB key spoofing. The volume serial is an identifier, not a secret.
- Hard mode releasing through the safety cap or its watchdog.
- The unsigned installer showing a SmartScreen warning.

## Update verification

The updater reads GitHub's `sha256:<hex>` digest for the release installer and hashes the bytes while downloading. A mismatch deletes the downloaded file and blocks installation; if the digest is missing or malformed, automatic installation is refused. The build script also writes a `.sha256` sidecar for manual verification. This detects accidental corruption and asset substitution when the release metadata is trusted. It does not protect against a compromised GitHub account, GitHub, or a malicious publisher; code signing would provide a stronger publisher identity check.

For releases, publish the installer through GitHub Releases so its asset digest is available to the updater. The `.sha256` sidecar can be published alongside it or its value can be included in the release notes for manual checks. The updater does not trust a separately downloaded sidecar.

## Response

You can expect an acknowledgement within a few days. Fixes ship in the next release, and the advisory is published after users can update. Credit is given unless you ask otherwise.
