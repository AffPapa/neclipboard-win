# Security policy

Status as of 2026-10-11: **0.2.0 is a release candidate**, not a confirmed public release. The repository destination and private reporting channel are not yet configured. No GitHub release or code-signing certificate is implied by the existence of release workflows.

## Reporting a vulnerability

Contact the project owner privately with a minimal synthetic reproduction. Once the public repository is configured, use its **Security → Report a vulnerability** option if enabled. A real contact link will be added when verified; do not post clipboard content, backups, tokens, private paths or exploit material containing user data in a public Issue.

Include the application version, Windows version, expected/actual behavior and impact. Do not attach production `history.dat`, `settings.dat`, recovery files, startup logs or screenshots without reviewing them for personal data.

## Security boundaries

- History, settings, snippets and backup content use Windows DPAPI CurrentUser. This protects stored data under Windows account protections; it does not defeat a process with sufficient privileges in that account or provide portable password-protected backups.
- The app has no account, background updater, telemetry or history-upload service. Other applications and Windows clipboard synchronization operate independently.
- Clipboard data and imported backups are untrusted. Exclusions and privacy markers reduce accidental capture but do not identify every password, especially browser extensions sharing the browser process.
- Optional automatic paste follows normal Windows focus/UIPI rules. Running the whole program elevated to bypass them is not a supported fix.
- 0.2.0 reads legacy data but writes schema-1 working documents. Downgrade requires compatible pre-upgrade data, not only an old EXE.

See [PRIVACY.md](PRIVACY.md) for retention and recovery details.

## Source and supply-chain controls

Run `scripts/public-preflight.ps1` before committing and again after staging. It checks exact index objects, publish-candidate worktree files and reachable history, rejecting generated/private paths and common credential patterns. It reports locations without secret values. It is a guardrail, not proof that every possible secret is absent; review images, binary release assets and unrecognized credential formats separately.

Do not commit local clipboard data, `.env.*`, backups, certificates, private keys, signing material, tokens, EXE/DLL/PDB files or archives. If a real credential was exposed, stop dependent publication and rotate it through the owning service. Removing the latest file does not remove old Git history; coordinate any public history rewrite separately.

Actions use pinned upstream commits. PR verification has read-only permissions; publishing credentials are scoped to the relevant release step. Never use privileged pull-request execution to run untrusted code with release secrets.

## Release integrity

- Keep the SDK/runtime on supported patches. The project targets .NET 10 LTS; self-contained EXEs require a new release to pick up runtime security fixes.
- Build and test the exact source commit on a clean runner. Create a draft bound to that SHA, then accept the exact downloaded EXE before promotion.
- Verify version, size, SHA-256 and Authenticode status. Check anonymous public download after publication.
- Do not replace existing published assets or move release tags silently. Preserve the previous accepted release and account for data-schema compatibility during rollback.
- Store signing credentials only in appropriate secret storage or a signing service. Compute SHA-256 after final signing/timestamping.

The current candidate is **unsigned**. Its checksum detects mismatched files but does not authenticate the publisher or guarantee absence of malware. Do not ask users to disable Defender or SmartScreen globally.
