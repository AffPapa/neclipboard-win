# Windows platform and release engineering audit

Audit date: 2026-10-11. Scope: .NET lifecycle, build/verification/release chain, public-source preflight. This document records engineering evidence; it is not evidence of a published release.

## Platform decision

The official [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) currently lists .NET 10 as active LTS through **2028-11-14**, with runtime 10.0.12. .NET 8 is in maintenance and reaches end of support on **2026-11-10**. The [.NET 10 download page](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) lists **SDK 10.0.401** and Desktop Runtime 10.0.12. The coordinator approved moving application and verification projects to `net10.0-windows`, with SDK selection in `global.json`.

Self-contained applications carry their own runtime: installing a Windows runtime update does not automatically service their bundled copy. Rebuild and release after applicable runtime security updates; record the actual SDK in the generated manifest. Keep dependency updates reviewed and automated through Dependabot. Avoid a preview runtime.

[WPF in .NET 10](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/whats-new/net100) contains maintained desktop, performance and accessibility work. It also unifies WPF/WinForms clipboard implementation and changes APIs related to unsafe serialization. Consequently a successful compile is insufficient to accept migration: text, images, file lists, clipboard privacy markers and the receiving applications need final-EXE testing. No new UI framework is required.

[Supported OS list](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md): the runtime supports current Windows 11 versions and several other Windows editions. This does not establish this application's compatibility with those extra editions or ARM64. The release target remains Windows 11 x64; CI on Windows Server 2025 is an additional build environment, not a substitute for desktop acceptance.

Preserve self-contained single-file publishing with `IncludeNativeLibrariesForSelfExtract=true`: [Microsoft's single-file documentation](https://learn.microsoft.com/en-us/dotnet/core/deploying/single-file/overview) explains that native libraries otherwise stay separate and that extraction uses `%TEMP%/.net` on Windows. Test first extraction and launches from a path with spaces/non-ASCII characters. Keep `PublishTrimmed=false`; [WPF trimming is unsupported](https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities). Do not claim NativeAOT, trimming or compression as a free optimization.

The [SendInput contract](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput) restricts injection by integrity level. Publishing an elevated executable to bypass that limitation is not an acceptable release fix. Preserve a clear manual Ctrl+V fallback in the app.

## Confirmed findings and changes

| ID | Severity | Initial evidence | Change | Acceptance |
| --- | --- | --- | --- | --- |
| REL-01 | P0 | Release workflow read the project before checkout | Checkout exact workflow SHA first | Clean snapshot/build; GitHub runner pending access |
| REL-02 | P0 | Main csproj built, separate verification project run with `--no-build` | Explicit warnings-as-errors build of both projects before verification run | Clean snapshot includes no previous bin/obj |
| REL-03 | P1 | Multiline native calls could hide nonzero exits | Wrappers inspect every dotnet/gh exit code immediately | A failing compilation stopped local release-build before publish |
| REL-04 | P1 | Release tag defaulted to newest default-branch state | Manifest carries exact commit; draft `--target` matches it; any existing tag is resolved and checked | Remote execution remains untested until repository/access exists |
| REL-05 | P1 | Working-file scan could miss a staged secret or deleted historical secret | Scan exact index objects, public worktree, reachable history paths/objects; no ripgrep dependency | Six isolated detector regressions passed |
| REL-06 | P1 | EXE became Latest immediately after startup-only smoke | Draft preparation and publication are separate workflows; publication requires exact desktop-accepted hash and rechecks downloads | Desktop acceptance is explicitly required, not inferred from smoke |
| REL-07 | P1 | .NET 8 support ends about one month after this audit | Approved .NET 10 migration, pinned SDK family via global.json | Product migration owned and tested by coordinator |
| REL-08 | P2 | Versioned asset name prevents a stable direct-download URL | Fixed asset names and a generated manifest with version, commit, hash, size, SDK and signature status | Latest URL can be used once actual repository is known |

Changes are limited to the release toolchain. No running user's history, registry, clipboard, system policies, Git author identity or remote settings were changed by this audit.

## Implemented release process

1. `Verify NeClipboard Win` checks public source, scanner regressions, both project builds, verification tests, self-contained publish and isolated light/dark startup. Token permissions remain read-only for PR code.
2. `Prepare draft release` accepts `vX.Y.Z` from `main`, checks that it matches the csproj, and requires `docs/releases/vX.Y.Z.md`. It builds on the requested immutable SHA. A separate write-permission job downloads the tested artifact and creates a draft bound to that SHA.
3. The assets are exactly `NeClipboardWin-win-x64.exe`, `NeClipboardWin-win-x64.exe.sha256`, `release-manifest.json` and `NeClipboardWin-LICENSES.txt`. The license companion combines the product license and full exact-runtime upstream notices; its hash and size are recorded in the manifest. Release notes come from the version-specific source file; no wildcard upload includes tests, PDBs or local data.
4. Download the draft's exact EXE for the required desktop, visual and upgrade acceptance. Keep its digest as evidence. Never rebuild or resign it afterward without repeating relevant acceptance and updating the digest.
5. `Publish verified draft` accepts that SHA-256. It downloads and validates asset names, manifest, version, size, hash, checksum file, signature, tag and commit. It smoke-tests the same bytes, promotes the draft, resolves the published tag, then downloads the public EXE anonymously, checks its hash and smoke-tests it again.
6. Promote the public download on the existing site only after it succeeds. A truthful candidate-status page may be published earlier without a download button or a release claim. The stable direct URL will be `https://github.com/OWNER/REPOSITORY/releases/latest/download/NeClipboardWin-win-x64.exe`; do not substitute invented owner/repository values. Release-specific URLs retain the tag for reproducibility.

The workflows serialize releases and refuse automatic replacement of an existing release. Re-running an existing tag requires inspection, not overwriting. Actions are pinned to upstream commit SHA; Dependabot covers actions updates. The current runner is explicitly `windows-2025`.

The main project keeps `PublishSingleFile=true`, `PublishTrimmed=false` and `RuntimeIdentifiers=win-x64` as stable properties. This makes its NuGet lock consistent for normal builds, verification project references and publishing: single-file publishing introduces the SDK's build-time ILLink tasks even when trimming is disabled. Application build and publish use locked restore. A clean test reproduced NU1004 with the former changing properties; the stable-property version passed.

The build manifest's `publishedAtUtc` stays null because the immutable asset is generated before publishing. Read the actual publication time and public URLs from GitHub afterward when preparing the website record. This avoids pretending a build date is a release date or rebuilding the EXE to embed its own checksum.

If publication succeeds but public download verification fails, the workflow fails and the site must not promote the release. Inspect whether the failure is an asset, permissions, availability or actual hash issue, then restore the previous approved Latest release when necessary. Do not rewrite old tags or replace old published binaries silently.

## Source preflight coverage

`public-preflight.ps1` rejects generated/private paths at any depth, `.env.*`, certificates/private keys, database/backup files, binaries and archives in source. It reads exact index objects even when the working file has been sanitized. It scans reachable branch/tag history, including commit metadata and every historical changed path. Untracked, nonignored publish candidates are checked too. Deleted worktree files do not silently remove their staged blob from checking.

Supported pattern detection includes common GitHub/OpenAI/cloud credentials, private-key blocks, credential-like assignments and password-bearing HTTP URLs. Only location is printed on failure; matches and lines are not printed. Regex timeouts and a 16 MiB per-object review limit bound scanner work. A blob over the limit requires manual review; it is not silently skipped.

Limitations: regex is not a proof of absence of all secrets. It cannot recognize every proprietary token, image-visible text or encrypted archive content. Ignored/untracked local outputs are intentionally not public-source candidates. Review the final publish asset separately and enable GitHub secret scanning/push protection where available. The scanner does not rewrite history or rotate credentials.

## Verification evidence

- PASS: all four PowerShell scripts parsed successfully.
- PASS: actionlint **1.7.12** checked all three workflows. The downloaded developer-only lint tool's archive checksum was verified against its [upstream release](https://github.com/rhysd/actionlint/releases/tag/v1.7.12). It stays under ignored artifacts and is not an application dependency.
- PASS: safe synthetic repository is accepted.
- PASS: untracked credential-shaped fixture rejected.
- PASS: staged fake credential is rejected despite clean worktree content.
- PASS: force-staged `.env.production` rejected.
- PASS: EXE outside normal output folders rejected.
- PASS: quarantined legacy settings rejected even though the filename no longer ends in `.json` or `.dat`; recovery metadata, temporary files and `.bak` files are also excluded.
- PASS: removed fake credential is still rejected from reachable history.
- PASS: source preflight on current actual project (index plus modified/untracked public candidates).
- PASS: an incomplete concurrent source snapshot caused native `dotnet build` failure and stopped the release script immediately; no publish/upload followed.
- PASS: a fresh source snapshot with no `bin`/`obj` passed both warnings-as-errors builds, **29 verification checks**, locked self-contained publishing, and isolated light/dark startup. The smoke helper validates its temporary deletion target, quotes the profile path, hides helper windows and disposes process handles.
- Intermediate pipeline specimen: SDK 10.0.401, version 0.2.0, 173,089,849 bytes, `NotSigned`, SHA-256 `df517f658f433dda687cb5f6cb98f4db01adb9f110c505d105be776fa5bf3384`. This was an uncommitted source snapshot while application QA changes continued. It proves the pipeline; it is **not the final release artifact or final acceptance hash**. The generated manifest correctly records dirty/uncommitted provenance and the public release script refuses it.
- NOT RUN: authenticated GitHub draft/upload/publish/download chain; correct repository and connection are not yet available. Syntax/lint checks do not prove a live GitHub run.
- NOT RUN by this role: real desktop acceptance, final upgrade/rollback, code signing and website publication; owned by coordinator and the appropriate testing role.

## Resume verification, 2026-10-11

The release-testing role repeated the current-tree checks after the interrupted session: **30 verification groups passed**, with a Release warnings-as-errors build and locked restore on SDK 10.0.401 (zero warnings/errors). Public preflight accepted 51 candidate worktree files and 31 index objects at that snapshot; all six negative scanner fixtures passed. Parsing every `scripts/*.ps1` and `tests/*.ps1` script and actionlint 1.7.12 over all three workflows also passed. File counts describe that snapshot, not a fixed acceptance target.

The earlier 29-check clean snapshot remains historical evidence of the pipeline, not proof that later code changes were tested in a final binary. A new clean publish and final desktop acceptance remain pending until the coordinator freezes the code. Current consolidated gates are maintained in [ACCEPTANCE.md](ACCEPTANCE.md). No authenticated GitHub execution or public EXE download was established by these local checks.

Additional negative gates passed: `release-build` rejected the mismatching `v9.9.9` tag and rejected dirty/uncommitted source under `-RequireReleaseNotes`, before build/publish. Read-only Git inspection found no configured remote or branch refs at this checkpoint. These refusals are intended safeguards, not release failures to bypass.

The coordinator subsequently confirmed code freeze and **31 passing verification groups**, after adding fail-closed locked-file handling, excluding reserved F12 from hotkey validation and rechecking PNG source exclusions. A clean frozen-source build is separate from this current-tree verification and must have its own artifact evidence.

That clean build then passed both warnings-as-errors builds, 31 verification groups, locked self-contained publish and isolated light/dark startup. Candidate EXE: 173,098,041 bytes, `NotSigned`, SHA-256 `7ad56815518e07e1c9af37c2346d6ae2aeac06c507499e9bcaa4d9d06a6bf898`. Its manifest remains explicitly dirty/uncommitted; the file is a local desktop-QA candidate, not a published asset. See [ACCEPTANCE.md](ACCEPTANCE.md).

Runtime redistribution notices were reviewed separately after binary freeze. Five checked-in text files preserve complete upstream license/notices from runtime packs 10.0.12 and the Windows Desktop pack's exact `dotnet/dotnet` source commit. All five were compared to their source text with only newline normalization allowed: PASS. [Provenance](../../THIRD-PARTY-NOTICES.md) distinguishes bundled runtime from SDK/build/website dependencies. The release companion can be generated without changing EXE bytes; publication checks its separate manifest digest.
