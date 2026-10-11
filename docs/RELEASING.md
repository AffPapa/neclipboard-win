# Выпуск NeClipboard Win

Текущий **0.2.0 — кандидат, не подтверждённый публичный выпуск**. Подтверждённый репозиторий: [AffPapa/neclipboard-win](https://github.com/AffPapa/neclipboard-win). GitHub CI, публикация EXE и окончательная приёмка отслеживаются отдельно. Цель: один Windows 11 x64 EXE без установленного SDK или .NET у пользователя.

## Подготовка версии

1. Согласуйте конечный объём изменений и устраните блокирующие дефекты.
2. Обновите `Version`, `AssemblyVersion`, `FileVersion` в csproj. `global.json` выбирает SDK 10.0.401 и совместимые patch-версии; WPF target — `net10.0-windows`. Single-file и RID должны оставаться согласованы с `packages.lock.json`.
3. Подготовьте конкретные notes в `docs/releases/vX.Y.Z.md`: изменения, совместимость данных, ограничения. Помечайте их candidate до публикации; не пишите будущую версию в changelog как Released.
4. Проверьте реальные изменения и exact staged/history source через `scripts/public-preflight.ps1`. Сканер не гарантирует распознавание всех секретов: отдельно просмотрите публичные изображения, документы и состав assets.
5. Создайте коммит и получите зелёный **Verify NeClipboard Win** на том же SHA. Не включайте реальные данные, ключи или выходные файлы сборки в Git.

Локальная проверка той же цепочки:

```powershell
.\scripts\public-preflight.ps1
.\scripts\release-test-preflight.ps1
.\scripts\release-build.ps1 -Tag v0.2.0 -OutputDirectory .\artifacts\candidate-0.2.0
```

Выходная папка должна быть пустой. Локальное происхождение без commit отмечается в manifest; такой файл не публикуется автоматически как clean release.

## Draft и приёмка точного файла

1. В `main` откройте **Actions → Prepare draft release** и задайте тег `vX.Y.Z`.
2. Workflow делает checkout точного SHA, проверяет версию/notes, собирает оба проекта, выполняет verification, locked single-file publish и isolated light/dark smoke.
3. Отдельный job создаёт draft с `--target` проверенного SHA и скачивает assets обратно для сравнения. Existing release/tag не перезаписывается; новый draft ещё не становится Latest.
4. В draft должны быть ровно:

   - `NeClipboardWin-win-x64.exe`;
   - `NeClipboardWin-win-x64.exe.sha256`;
   - `release-manifest.json`;
   - `NeClipboardWin-LICENSES.txt` — лицензия проекта и полные уведомления bundled .NET; его размер и SHA-256 входят в manifest.

5. Скачайте этот EXE и проведите desktop/visual/upgrade-приёмку с синтетическими данными. Проверьте текст, изображения, файлы, hotkey, copy/manual paste, optional auto-paste, pause, snippets, settings, backup/restore, themes и exit/restart. Изолированный startup smoke не заменяет эти сценарии.
6. Запишите digest принятого EXE. Любая пересборка или подпись меняет файл: после неё обновляются hash и применимые проверки. Не меняйте asset под старой версией. Сопроводительные уведомления создаются `scripts/release-notices.ps1` из [проверенных исходных лицензий](../THIRD-PARTY-NOTICES.md) без пересборки EXE; сохраняйте их при дальнейшей передаче программы.

## Публикация и сайт

1. Запустите **Publish verified draft** с тегом и принятым SHA-256.
2. Workflow проверяет manifest, version, target commit/tag, assets, hash, checksum и реальный Authenticode status, затем выполняет smoke того же файла и публикует его как Latest.
3. После публикации выполняется скачивание по публичному URL **без токена**, сравнение hash и ещё один smoke. Ошибка здесь означает, что распространение не принято; сайт не продвигает такую сборку.
4. Из фактического GitHub Release возьмите дату, release URL и download URL. Из manifest — version, size, SHA-256, SDK и signature status. `publishedAtUtc` в build manifest намеренно null: build-time не является временем публикации.
5. Обновите существующий сайт, changelog, README и About, проверьте mobile/desktop и все ссылки. Основная кнопка ведёт на реально доступный EXE с постоянным asset name. Объявляйте только проверенные Windows/архитектуры.

Текущий кандидат **NotSigned**. До появления действительной подписи это указывается явно. SHA-256 — проверка соответствия файла, не подтверждение издателя. Не отключайте Defender/SmartScreen ради выпуска.

## Данные, обновление и откат

0.2.0 читает legacy 0.1.5 history/settings и записывает schema-1 envelopes. Возврат старого EXE поверх новых данных не поддерживается как безопасная операция: старая версия может не распознать формат. Перед обновлением сохраните backup, созданный 0.1.5, и прежний EXE. Проверенный downgrade восстанавливает совместимый pre-upgrade профиль; backup новой версии не объявляется пригодным для старой без отдельной проверки.

DPAPI backup рассчитан на ту же Windows-учётную запись и компьютер. Списки файлов сохраняют пути, не содержимое файлов. Рабочий процесс закрывается через трей перед заменой EXE; если portable-файл переносится, настройка автозапуска должна указывать новый путь.

Если обнаружен критический дефект после публикации: прекратите продвижение проблемного файла, верните Latest/сайт на предыдущий принятый релиз, сообщите об ограничении и исправьте новой версией. Не удаляйте историю, не переписывайте теги и не подменяйте опубликованный бинарник молча. Откат программы и откат формата данных — разные проверки.
