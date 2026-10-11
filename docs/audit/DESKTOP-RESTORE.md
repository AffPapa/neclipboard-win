# Native UI backup and restore — 2026-10-11

Candidate: `artifacts/release-28957d4/NeClipboardWin-win-x64.exe`, SHA-256 `e09942e870045c4b0f3eca5906577357cb7b279acc3a525e8afa23f8ac112042`.

## Isolation and setup

- Used only the candidate's `--qa-isolated <temp-profile> --qa-interactive --qa-seed` mode. The temporary profile was a new directory beneath the user's Temp folder, seeded with synthetic text/file history. No ordinary NeClipboard profile, registry autostart, external clipboard data, or third-party app was used.
- Through the app UI, set max history to 50 and hotkey to Ctrl+Alt+V; capture remained enabled; autostart and AutoPaste remained off. Added `NeClipboard QA source` with `Строка восстановления 😀\r\nВторая строка`.
- Created `restore.neclipboard-backup` using the application's Save Backup button and native Save dialog. File exists in that QA profile and is 1,990 bytes. App reported “Резервная копия создана и защищена для текущей учётной записи Windows.”

## Restore test

1. Changed max history from 50 to 75 in Settings and saved.
2. Added a second synthetic snippet, `NeClipboard QA extra` / `Временная строка`.
3. Selected the backup through the app's Restore Backup button and native Open dialog. The app showed its warning that history, snippets and settings would be replaced. Confirmed only for this disposable synthetic QA profile.
4. App reported “Резервная копия восстановлена.” The Settings UI showed max 50, Ctrl+Alt+V, capture enabled, autostart off and AutoPaste off. The snippets UI contained the original named multiline Unicode/emoji snippet and no `NeClipboard QA extra`; History showed the two seeded synthetic items (text plus file paths).
5. Closed the app through its own window close control. Relaunched the exact same EXE against the same isolated profile without reseeding. The window reopened in QA mode; AX/UI again showed both history rows and the original snippet (no temporary snippet), while Settings showed max 50, Ctrl+Alt+V, capture enabled, autostart off and AutoPaste off.
6. In the same disposable profile, created a separate 64-byte invalid `.neclipboard-backup` fixture; the valid 1,990-byte backup was left unchanged. Selected the invalid file in the native Restore dialog. The app returned `Не удалось открыть копию: файл повреждён, слишком большой или создан для другой учётной записи Windows.`, did not show the destructive-replacement confirmation, and the UI still showed max 50, the original snippet and both seeded history items.

## Boundaries

This proves one successful user-interface round trip and graceful restart, plus rejection of one obviously malformed short file without data replacement, on the exact release candidate for the current Windows account/profile. It does not prove portability to another Windows account or PC (DPAPI-bound), all corruption classes, behavior on disk-full/write failures, or production-profile restore. Clipboard file entries are paths; backup does not package file contents. No user's real data was replaced.
