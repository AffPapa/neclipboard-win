# Подготовка GitHub

Состояние на 11 октября 2026 г.: [репозиторий AffPapa/neclipboard-win](https://github.com/AffPapa/neclipboard-win) создан в подтверждённом аккаунте владельца. Версия 0.2.0 — кандидат; загрузка исходников, успешный GitHub CI и доступный пользователям EXE ещё не подтверждены. Наличие workflow не означает, что релиз опубликован.

## Первый push

1. Используйте созданный репозиторий **AffPapa/neclipboard-win**, только для NeClipboard Win. Не включайте соседние проекты. Имя и email автора коммита должны быть выбраны владельцем; GitHub noreply-email позволяет не публиковать личный адрес.
2. Проверьте исходники до коммита и после staging:

```powershell
.\scripts\public-preflight.ps1
git status --short
git diff --cached --stat
```

3. Проверьте полный staged diff локально, затем создайте коммит. Подтверждённый адрес remote — `https://github.com/AffPapa/neclipboard-win.git`. Не отправляйте токены в чат и не вставляйте их в URL. Авторизация выполняется штатным GitHub/Git credential manager или через авторизованный GitHub-интерфейс.
4. Отправьте `main` и дождитесь зелёного **Verify NeClipboard Win** на нужном commit. Workflow использует `global.json`, .NET 10, clean Windows runner, builds/test/publish и isolated smoke.

Если remote уже настроен, сначала подтвердите, что он относится именно к этому продукту. Не заменяйте адрес, владельца или существующую историю без проверки.

## Настройки репозитория

- Включите доступные GitHub secret scanning и push protection.
- Защитите `main` проверками **Verify NeClipboard Win** и ревью по принятому процессу команды.
- Разрешите Actions; права записи выдаются только release-job. PR-код не должен получать signing/deployment credentials.
- Добавьте в About подтверждённый [сайт](https://neclipboard-win.mail782861.chatgpt.site), описание продукта, Windows и clipboard topics.
- Если доступна private vulnerability reporting, включите её и добавьте фактический контакт в `SECURITY.md`.

## Готовые файлы для людей

В Git отправляются исходники и документация. EXE хранится в **Releases**, а не в Git-истории.

1. Запустите **Prepare draft release** с `v0.2.0`; предварительно нужен `docs/releases/v0.2.0.md` и зелёный CI.
2. Проверьте скачанный из draft файл по процедуре [RELEASING.md](RELEASING.md).
3. После desktop/upgrade-приёмки запустите **Publish verified draft**, указав SHA-256 именно проверенного файла.
4. Workflow проверит публичное скачивание без авторизации. Только затем добавьте реальные download/release URL в README и сайт и опубликуйте changelog с фактической датой.

Имена assets постоянные: `NeClipboardWin-win-x64.exe`, `NeClipboardWin-win-x64.exe.sha256`, `release-manifest.json`, `NeClipboardWin-LICENSES.txt`. Последний файл сопровождает EXE при распространении и содержит лицензии проекта и bundled .NET. После настоящего релиза постоянный download-адрес — `https://github.com/AffPapa/neclipboard-win/releases/latest/download/NeClipboardWin-win-x64.exe`. Пока файл не опубликован и не проверен, этот адрес не является рабочей кнопкой скачивания.

Сертификаты, пароли и токены не входят в исходники или release notes. Для будущей подписи используются предусмотренное secret storage или сервис подписи; подписанный файл проверяется и хешируется заново.
