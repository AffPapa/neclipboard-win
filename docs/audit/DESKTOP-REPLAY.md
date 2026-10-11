# Native clipboard replay — 2026-10-11

Application source `28957d4a3645b0b8c945b753ec409986c516da7c`, exact EXE SHA-256 `e09942e870045c4b0f3eca5906577357cb7b279acc3a525e8afa23f8ac112042`, checked unchanged after the run. Actual Windows UI actions used the supported Computer Use runtime, not a mocked clipboard API.

## Safety and method

The original clipboard had UnicodeText, Locale, Text and OEMText formats. Fixture Begin successfully copied all supported values into RAM. No original contents, paths, pixels or hash were logged. Unknown/unavailable formats still refuse the test; that protection was not removed.

The v5 fixture replaces its own synthetic payload with a DIFFERENT fixed synthetic sentinel before each replay. It does not clear the clipboard for replay. Preparation requires ownership, and successful verification requires a different nonzero sequence plus exact expected payload. Text additionally compares the receiving field. PASS consumes the preparation gate. Sequence checks refuse observed newer third-party changes. They are not an atomic transaction: a strictly concurrent copy between the check and WinForms write/read can still race. Do not treat this fixture as protection against all simultaneous clipboard writers.

Fixture implementation DLL SHA-256 `506c60d191ed6e5556a47a559b31bd463ac30e5f4c207e398c35532f71412ef3`; build had zero warnings/errors. Pure snapshot/sequence self-tests run without accessing the system clipboard. The apphost EXE hash alone does not identify the implementation DLL; the helper is not the distributed product.

## Observed results

| Scenario | Result |
| --- | --- |
| Manual text | Publish synthetic Unicode/CRLF/emoji → sentinel → global Ctrl+Alt+V → search `tail-search-token` → Enter → receiver Ctrl+V → exact buffer and receiving-field comparison PASS. |
| Image | Publish known 2×2 bitmap → sentinel → hotkey → search by image type → Enter → all four pixels exact PASS. |
| Files | Publish two own temporary file paths, one Unicode → sentinel → hotkey → search second filename → Enter → both paths and order exact PASS. |
| Automatic text | Enable experimental auto-paste only in QA settings and save → sentinel, empty receiving field → hotkey → search → Enter, WITHOUT manual Ctrl+V → exact clipboard and field comparison PASS. |
| Isolation/persistence | After graceful app exit, profile inspection reports text=2, image=1, files=1, autoPaste=true, hotkey=3/86. All text has the synthetic prefix and all sources are the fixture. No unfinished `.tmp` was present. |
| Original clipboard | Graceful fixture Finish logged `PASS original-clipboard-restored` at 14:49:35 UTC. The original was not logged or hashed; this is the supported-format restoration result, not a claim about every possible OLE format. |

Ctrl+Alt+V was chosen in the fresh QA profile because a previous QA process retained the default hotkey. Its settings and process were not changed. Initial fixture launch was hidden and had no targetable window; it was stopped BEFORE Begin or clipboard writes, then relaunched visibly. The test app and RAM-holding fixture were closed gracefully. A second, unused helper was opened solely to bring the hidden palette back via its own hotkey and close it; it never called Begin or changed the clipboard.

Local ignored evidence: `desktop-fixture-results.log` in the run's isolated `NeClipboardWin-QA-desktop-*` TEMP profile, with fixed labels/timestamps/booleans only. The synthetic profile is retained for follow-up inspection, not uploaded.

## Draft distribution check

The existing UNPUBLISHED v0.2.0 draft was refreshed to the same e099 EXE and source28957d4, without rebuilding, signing, publishing or changing a tag. Remote draft state and exact four asset names were checked first; no public v0.2.0 tag existed. Only three changed assets were replaced by explicit names (EXE, checksum, manifest); the identical license bundle was retained. The rejected ad2db EXE/manifest remain in the earlier local download evidence for recovery.

All four draft assets were downloaded into a new empty `artifacts/draft-e099-download-5e4aa66b3ab649b2be9b53bd38ddbd9c` directory and matched local hashes AND sizes. Downloaded EXE passed enhanced isolated light and dark smoke (current DPAPI history committed, no unfinished writes). Final remote state remains `isDraft=true`, target28957d4; no public tag. Anonymous asset HEAD, without a token, returned HTTP404. This is verified draft preparation, NOT public release acceptance or anonymous-download success.

## Remaining scope

This is one bounded desktop QA session, NOT full release acceptance. Alpha/PNG transparency, Notepad/Explorer/browser/other receivers, pause/privacy/exclusions, hotkey conflict rollback, production lifecycle, UI backup restore, clean Windows without desktop runtime, prolonged stress and leak attribution remain separate checks. No public binary release was promoted on the strength of this session alone.
