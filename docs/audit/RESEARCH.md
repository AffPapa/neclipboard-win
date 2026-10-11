# Исследование clipboard-менеджеров и дополнений

Дата проверки: 11 октября 2026 года. Предмет: NeClipboard Win, а не новый браузер.

## Вывод для ближайшего выпуска

Наиболее полезный небольшой объём: исправить сохранение и выпуск, убрать повторную обработку больших preview, сохранить поиск по полному содержимому, сделать очистку поиска заметной, показать паузу прямо в окне и добавить ограниченную по времени паузу. Существующие empty states, сниппеты, исключения, DPAPI, backup, темы и ручной copy уже есть; их следует проверить и улучшить, а не объявлять новыми.

Следующий полезный кандидат — настраиваемый hotkey с проверкой конфликта. Полезность высокая, но регистрация, rollback и сохранение настройки требуют самостоятельной приёмки. Фильтр типа/закреплённых и преобразование записи в сниппет дешевле полноценного редактора rich text. Plugin marketplace, облако, AI, OCR и собственное браузерное расширение в текущий выпуск не входят.

Важная поправка к исходной мотивации: Clipy возобновил выпуск; версия 1.3.0 от 20 июня 2026 поддерживает Apple Silicon и macOS 13+. Утверждение «Clipy скоро перестанет работать, потому что только Intel» больше нельзя использовать как факт. Ценность NeClipboard — простой локальный продукт для Windows. [Официальный релиз Clipy](https://github.com/Clipy/Clipy/releases/tag/1.3.0)

## Методика и пределы

Изучены 14 основных решений и 5 смежных инструментов по официальным сайтам, документации, репозиториям и страницам издателей в магазинах. Последние GitHub-релизы дополнительно проверены через публичный API `repos/{owner}/{repo}/releases/latest`; даты ниже — `published_at`, а не дата индексации поиском. Это кабинетное исследование: сторонние программы не устанавливались, личный clipboard им не передавался.

«Есть» означает подтверждение первичным источником, а не выполненный нами E2E. «Не подтверждено» не означает отсутствие функции. Точный состав зависит от версии/плана/платформы. Для всех конкурентов **не измерены** RAM, CPU, cold start, задержка поиска, DPI и screen reader. Слова поставщика «быстро», «лёгкий», «защищённый» не приняты за результаты независимого теста. Размер загружаемого установщика не сравнивается с размером self-contained EXE как показатель эффективности.

Источники проверены на указанную дату. Там, где дата/версия/лицензия не установлена, это прямо отмечено. Код, названия и assets конкурентов не копировались. Лицензия продукта не автоматически распространяется на его сторонние компоненты.

## Сравнительная матрица

| Решение | Основная модель UI | Что полезно заимствовать как идею | Сеть/приватность по источникам | Лицензирование/ограничение |
| --- | --- | --- | --- | --- |
| Windows Win+V | Системный popup | Мало действий, pin | Sync опционален; локальная история ограничена | Компонент Windows |
| Clipy | Меню/popup macOS | История + сниппеты, ясный выпуск | Marker types поддержаны; актуальные сетевые границы отдельно | MIT; macOS |
| Ditto | Tray + searchable popup | Быстрый сценарий и portable download | Издатель заявляет local-first без аккаунта/telemetry | GPL-3.0 |
| CopyQ | Окно с вкладками | Поиск, tags, разные форматы | Есть правила исключений; scripts расширяют риск | GPL-3.0 |
| ClipboardFusion | Окно истории | Hotkeys, pinned, преобразования | Sync через аккаунт; Pro | Free/Pro, proprietary |
| ClipClip | Paste Menu + редакторы | Preview и понятный поиск | Local clips; cloud/actions требуют отдельной оценки | Условия тарифа/лицензии не установлены полностью |
| ClipAngel | Список + preview | Type filters, clear filter, источник | Заявлены encrypted storage и privacy marker | GPL-3.0 |
| ArsClip | Настраиваемый popup | Portable, постоянные clips | Возможен Global Clipboard; модель хранения не подтверждена | Freeware, исходники включены; условия reuse отдельно |
| Clipboard Master | Список/pinned menu | Templates, collections | Есть сетевые функции/password safe | Free download; точная лицензия не проверена |
| 1Clipboard | История + favorites | Понятные search/favorites | Google Drive sync; offline mode есть | Beta; лицензия исходников не подтверждена |
| Maccy | Нативный keyboard-first popup | Узкая задача, type-to-search | Локальное хранение, password-manager cooperation | MIT; macOS |
| Paste | Визуальная история/pinboards | Preview, retention, exclusions | Опциональные интеграции/синхронизация меняют модель | Коммерческий, trial |
| Raycast | Командная палитра | Copy/Paste отдельно, type filters | Есть exclusions; AI/URL preview отдельно | Free/Pro; набор зависит от платформы |
| Alfred | Палитра + snippets | Retention, max clip, exclusions | Clipboard включается пользователем; concealed marker | Clipboard/snippets требуют Powerpack |

Доказательства матрицы и конкретные ограничения находятся в карточках ниже.

## Карточки основных решений

### R01 — Windows clipboard history

- Источник: [Microsoft Support](https://support.microsoft.com/en-gb/windows/apps/using-the-clipboard). Версия привязана к Windows, отдельного релиза clipboard нет.
- Сценарий/установка: встроенная история через Win+V; отдельная установка не нужна. Текст, HTML, bitmap, закрепление; лимит 25 записей и 4 MB на запись. Незакреплённое очищается при перезапуске.
- Приватность: sync между устройствами включается отдельно и связан с Microsoft/work account; ручной sync тоже передаёт выбранный текст в облако. Отдельный переносимый backup, plugins и полноценный search в этой статье не подтверждены.
- Решение: сохранить краткость первого сценария и честно объяснить пределы NeClipboard. Не обещать заместить системный clipboard и не пытаться перехватить Win+V. Доступность и темы системного popup в данном исследовании не тестировались.

### R02 — Clipy

- Источники: [репозиторий и лицензия](https://github.com/Clipy/Clipy), [1.3.0](https://github.com/Clipy/Clipy/releases/tag/1.3.0). Последний релиз: 2026-06-20; MIT (дополнительно проверено GitHub repository metadata). Разработка возобновлена.
- Платформа/сценарий: macOS 13+, Apple Silicon; clipboard history и snippets в меню. Windows-сборки нет. Distribution Site связан с GitHub Releases.
- Надёжность/приватность: релиз содержит миграцию Realm → SQLite, обработку pasteboard markers и предупреждение заранее экспортировать важные snippets. Также упомянут Firebase; из release notes нельзя вывести ни отсутствие сети, ни передачу clipboard — нужен отдельный анализ реализации.
- Решение: заимствовать простую модель вызова и связку сайт→Release. При своей миграции проверять восстановление. Не повторять архитектуру macOS, не использовать чужие assets. Исправления thumbnail/dark-mode в release notes — полезные направления QA, но не доказательство текущих дефектов.

### R03 — Ditto

- Источники: [сайт](https://ditto-cp.sourceforge.io/), [репозиторий](https://github.com/sabrogden/Ditto), [3.25.113.0](https://github.com/sabrogden/Ditto/releases/tag/3.25.113.0). Последний отмеченный stable: 2025-09-27; GPL-3.0, публичная разработка продолжается.
- Windows: installer, portable, Store, winget. Захватывает text/images/HTML/custom formats; tray/hotkey вызывает список, Enter/двойной клик вставляет в предыдущее окно.
- Издатель заявляет local-first: без login/cloud/telemetry. Не следует из этого предполагать криптографическую защиту всех данных. Подпись Windows через SignPath описана отдельно.
- Решение: понятный выбор готовой загрузки и узкий цикл поиска/вставки. История релиза содержит изменения dark text, DPI и prompts — включить такие состояния в свою QA-матрицу. Не переносить весь богатый набор функций без сценария.

### R04 — CopyQ

- Источники: [README](https://github.com/hluk/CopyQ), [17.0.0](https://github.com/hluk/CopyQ/releases/tag/v17.0.0), [scripting API](https://copyq.readthedocs.io/en/latest/scripting-api.html). Релиз 2026-10-06; GPL-3.0.
- Windows/Linux/macOS; на Windows installer и portable ZIP. UI с вкладками, фильтрацией, tags/notes, редактированием. Поддерживает text/HTML/images/custom formats, настраиваемые shortcuts и exclusions.
- Восстанавливает сохранённые записи после запуска. Scripting и команды могут исполнять пользовательскую логику; отсутствие внешней сети для произвольного script не гарантируется. Шифрование/backup по умолчанию здесь не подтверждались.
- Решение: изучать search, доступность команд с клавиатуры и ясный формат импорта. Не вводить вкладки, tags и командный runtime одновременно. Цена поддержки расширяемости существенно выше обычного списка — это инженерная оценка, не измерение RAM CopyQ.

### R05 — ClipboardFusion

- Источники: [Features](https://www.clipboardfusion.com/Features/), [Help](https://www.clipboardfusion.com/Help/), [Download](https://www.clipboardfusion.com/Download/). 6.3 от 2025-12-20. Free/Pro; закрытый продукт, Pro trial 30 дней.
- Windows 10/11; сайт указывает x86/x64/ARM64 compatibility, .NET 10 Desktop и WebView2 runtime. Installer устанавливает недостающее; ZIP издатель считает тестовым способом.
- UI: история, pinned, online items; поиск/редактирование. Настраиваемые hotkeys, C# macros, triggers и text scrubbing. Sync через Binary Fortress account требует Pro.
- Решение: отдельная настройка shortcut полезнее макроредактора. Наш self-contained путь должен сохранять обещание запуска без самостоятельной установки runtime. Наличие WebView2 у конкурента не обосновывает его добавление в NeClipboard. Реальные performance и local encryption не проверены.

### R06 — ClipClip (VITZO)

- Источники: [Features](https://www.clipclip.com/features), [Security help](https://helpdesk.clipclip.com/en/article/security-ojxe3h/), [сайт](https://www.clipclip.com/). Не путать с одноимёнными GitHub-проектами. Точная текущая версия/дата stable и условия платных функций не установлены.
- Windows-инструмент с Paste Menu, pinned clips, search/preview, text/RTF/HTML/image editors, форматными преобразованиями, OCR, screenshot/recorder, сетевыми actions.
- Privacy: новая features-страница говорит об encrypted local history; help 2023 описывает saved clips как обычные файлы и password-protected folders отдельно. Эти утверждения относятся к разным категориям/датам; нельзя объявить весь продукт одинаково зашифрованным. CloudConnect передаёт данные провайдеру.
- Решение: preview и осознанное copy действие полезны; полный набор editors, recorder и сервисов чрезмерен для текущего scope. Не повторять маркетинговое «всё offline» для функций, явно вызывающих внешние платформы.

### R07 — ClipAngel

- Источники: [страница автора на SourceForge](https://sourceforge.net/projects/clip-angel/), [репозиторий](https://github.com/tormozit/ClipAngel). Последняя активность файлов на странице: 2026-06-04; точный stable tag не установлен. GPL-3.0.
- Windows, .NET Framework 4.5.2+, portable mode. Список с preview, incremental filter, type/favorite filters, исходное/обычное paste, поиск в полном тексте, source app/URL, history limits.
- Автор заявляет encrypted history и `Clipboard Viewer Ignore`. Есть update, translate, external compare; отсутствие сети для всех функций не предполагается. Заявление об elevated paste не переносится к нам как требование обходить UIPI.
- Решение: видимое состояние фильтра, доступный clear filter и наглядные форматы. Source URL может содержать секреты; не сохранять расширенные метаданные по умолчанию. Accessibility и реальные гарантии crash recovery нами не проверены.

### R08 — ArsClip

- Источник: [официальная страница](https://www.joejoesoft.com/vcms/97/). 5.46, публикация 2026-06-18. Freeware; включён Delphi source, но право переносить код требует проверки условий, а не только наличия исходников.
- Portable ZIP с optional installer; страница заявляет XP–Windows 10, Windows 11/ARM64 в этой формулировке не подтверждены. Hotkey/right-click popup; permanent clips/macros, images, RTF, files, Unicode/HTML.
- Global Clipboard — отдельная опция. Шифрование, переносимость backup и privacy exclusions актуальной версии подробно не проверены.
- Решение: заимствовать компактный popup и выделение постоянных записей. Изменения 5.46/5.45 про расположение, scale и pin races полезны для тест-кейсов. Не строить JS/macros и множественные paste modes в простом core.

### R09 — Clipboard Master

- Источники: [Features](https://www.clipboardmaster.com/features/features), [Download](https://www.clipboardmaster.com/download/download), [главная](https://www.clipboardmaster.com/). Download показывает **6.0 beta 6 от 2026-09-14**; это beta, а не подтверждённый stable.
- Windows; сайт заявляет 32/64-bit и широкий диапазон старых ОС. Список/pinned menu, до 10 000 entries, разные форматы, collections, templates и горячие клавиши. Есть screenshot tool, macros, password safe и network clipboard.
- Free download/free program — подтверждено; точные ограничения новой лицензии, portable, encryption всего history и restore не установлены.
- Решение: удобные шаблоны могут быть отдельным этапом; password safe — другая ответственность и угроза. Не увеличивать лимит до 10 000 ради сравнения без бюджета памяти. Утверждение «во всех программах» не заменяет нашу совместимость/E2E.

### R10 — 1Clipboard

- Источник: [официальный сайт](https://1clipboard.io/). Сайт продолжает обозначать beta; номер и свежая дата release не найдены. Это **не доказательство прекращения разработки**, но слабая прозрачность поддержки.
- Windows/macOS; history, favorites, search. Google Drive sync требует Google account, offline mode предусмотрен. Сайт прямо указывает Electron/Angular/Node.js.
- Лицензия исходников продукта, шифрование local storage, переносимость backup, ARM64, доступность и темы не подтверждены выбранной страницей.
- Решение: использовать простоту search/favorite, но не переносить web runtime и account-first sync. Нельзя писать, что Electron здесь «медленный», без измерений; для NeClipboard он исключён требованиями и отсутствием необходимости.

### R11 — Maccy

- Источники: [сайт](https://maccy.app/), [2.7.1](https://github.com/p0deje/Maccy/releases/tag/2.7.1). Релиз 2026-08-10; MIT. Текущий сайт требует macOS Sonoma 14+; App Store/direct download.
- Нативный keyboard-first интерфейс: открыть, набрать запрос, Enter. Узкая задача истории. Издатель заявляет локальное хранение и реакцию на удаление password manager содержимого из clipboard.
- Это хороший ориентир минимализма, но не доказательство конкретного бюджета RAM/latency. Detailed backup, plugin support, DPI и screen reader в этом исследовании не подтверждены.
- Решение: весь core должен работать клавиатурой и без аккаунта. Privacy marker проверяется по Windows API, а не механическим переносом macOS поведения. Не добавлять постоянный polling, чтобы имитировать чужой подход.

### R12 — Paste

- Источники: [сайт](https://pasteapp.io/), [pricing](https://pasteapp.io/pricing), [exclusions](https://pasteapp.io/help/exclude-apps), [retention](https://pasteapp.io/help/control-history-retention). Точный текущий номер релиза не подтверждён.
- macOS/iPhone/iPad; коммерческий продукт с 7-day trial, подпиской или lifetime. На проверенной pricing-странице annual personal — $29.99 без налогов; цены/регионы изменяемы. History, pinboards, search, sync.
- На Mac доступны app exclusions; ранее сохранённые записи исключение не удаляет. Retention настраивается. Сайт предлагает интеграции с AI-инструментами: это отдельный opt-in риск доступа к истории, не необходимость для нас.
- Решение: retention и понятная граница исключений полезны; визуальные previews должны оставаться лёгкими. Не переносить cloud/AI/подписочную модель. Требования мобильного capture не относятся к Windows.

### R13 — Raycast

- Источники: [Clipboard manual](https://manual.raycast.com/clipboard-history), [Windows](https://www.raycast.com/windows), [pricing](https://www.raycast.com/pricing). Windows-страница показывает 2.7.3.0, Windows 10+; точная дата release не зафиксирована. Free/Pro, закрытый app.
- Palette: type filters, pin, явные Copy/Paste, format choice, retention. Pro расширяет retention. Есть snippets, extensions, OCR, AI и sequential paste; полное равенство функций Windows/macOS не предполагается.
- Excluded apps предусмотрены. Link previews могут загружать favicons/social cards; даже без sync это сетевое действие. Доступность клавиатурных команд документирована, screen reader/DPI не тестировались.
- Решение: ясная primary action и лёгкий filter полезны. Не копировать launcher, AI, extensions marketplace; обычные clipboard items не должны вызывать URL fetch у нас.

### R14 — Alfred

- Источники: [Clipboard History](https://www.alfredapp.com/help/features/clipboard/), [Snippets](https://www.alfredapp.com/help/features/snippets/). macOS; clipboard/snippets входят в платный Powerpack. Актуальный номер/datetime последнего приложения в этих страницах не установлен.
- Searchable popup для текста/картинок/file links. Настраиваются hotkey, срок хранения, предельный размер; copy history можно превратить в snippet. Auto-expansion и snippet export описаны отдельно.
- Clipboard первоначально выключен; пользователь даёт разрешения. Есть ignore apps и concealed-data markers. Шифрование диска/переносимость backup этими материалами не доказаны.
- Решение: max payload, exclusions и history→snippet. Не внедрять глобальный text expansion до проверок IME, парольных полей, разных раскладок и гонок focus. Удобство keyboard-first сохраняется без такой функции.

## Смежные инструменты и расширения

| ID / инструмент | Подтверждённая ценность и источник | Права/данные/поддержка | Решение для NeClipboard |
| --- | --- | --- | --- |
| E01 Espanso | Cross-platform text expansion, YAML snippets, forms, packages. [Репозиторий](https://github.com/espanso/espanso), [package docs](https://espanso.org/docs/packages/basics/) | GPL-3.0; 2.4.1 от 2026-09-02. Local/no tracking заявлены; scripts/shell из package способны выполнять код. Hub говорит о ручной проверке пакетов, что не является гарантией безопасности | Позже рассмотреть data-only импорт snippets; script execution и пакетный менеджер не нужны |
| E02 AutoHotkey v2 | Hotstrings, context-aware substitutions, automation. [Официальные исходники](https://github.com/AutoHotkey/AutoHotkey), [официальные docs в GitHub](https://raw.githubusercontent.com/AutoHotkey/AutoHotkeyDocs/v2/docs/Hotstrings.htm) | 2.0.30 от 2026-10-09; GPL-2.0. Hotstrings используют keyboard hook, script — исполняемый код. Основной docs-сайт вернул 403, использован официальный docs repo | Не встраивать AHK engine. Синтетическая совместимость с отдельно запущенным expander — будущий тест; импорт только текста после отдельной спецификации |
| E03 Copy as Markdown | Selection, title+URL, ссылки/изображения, список tabs. [Repo](https://github.com/yorkxin/copy-as-markdown), [manifest](https://raw.githubusercontent.com/yorkxin/copy-as-markdown/master/chrome/manifest.json) | MIT; manifest 3.7.0/MV3, Chrome minimum116; activeTab, clipboardWrite, scripting, offscreen и др.; tabs/tabGroups optional. Chrome/Edge/Firefox | Действительная дополнительная ценность — структура страницы, недоступная обычному text clipboard. Сначала использовать совместимость с готовым Markdown, не создавать обязательное расширение |
| E04 Copy PlainText (erosman) | Убрать formatting/лишние пробелы, find/replace, copy/paste plain text. [Страница издателя в Firefox Add-ons](https://addons.mozilla.org/en-US/firefox/addon/copy-plaintext/) | MPL-2.0; 1.15 от 2023-07-25. clipboardRead/write, notifications; доступ ко всем сайтам optional. Свежесть поддержки ограничена старой датой, это не доказательство вредоносности | Обычный текст у нас уже plain. Отдельную команду plain-text вводить лишь вместе с реально сохранёнными rich formats |
| E05 Text Blaze | Snippets по shortcut, формы/динамические команды, folders. [Quick start](https://blaze.today/guides/quickstart/), [data collection](https://blaze.today/datacollected.html) | Chrome/account/sync; proprietary. Поставщик сообщает о local shortcut matching, но application data/snippets и usage metadata обрабатываются сервисом. Текущий номер extension не установлен | Заимствовать понятный редактор шаблона; не навязывать аккаунт и не заявлять «не собирает ничего» лишь потому, что не логирует keystrokes |

## Решение об интеграциях

Для захвата и вставки обычного текста/картинок/файлов browser extension не нужен: приложение работает через системный clipboard. Browser-only search/snippets дублируют core и добавляют отдельные разрешения, магазин, update channel и QA.

Узкая будущая гипотеза — «сохранить цитату с названием страницы и ссылкой» по одному явному действию. Сначала достаточно принять Markdown-текст от существующего Copy as Markdown. Если появится подтверждённый спрос на автоматическое структурированное provenance, нужен отдельный прототип: activeTab/user gesture, минимальные permissions, без `<all_urls>` по умолчанию, без фонового чтения всех страниц. Native messaging оправдан лишь если стандартного clipboard недостаточно; тогда versioned bounded message schema, allowlisted origins и отсутствие произвольного исполнения обязательны. Это проектное решение, не утверждение об уже готовой интеграции.

Плагины с исполняемым кодом, AI-доступом ко всей истории и автоматическим открытием URL отклонены для core. Поддержка plain text/Markdown как **данных** не требует markdown engine или WebView. Темы и иконки остаются нативными ресурсами WPF.

## Сопоставление с исходной NeClipboard 0.1.5

По чтению исходников на старте аудита: `Models.Preview` нормализует полное содержимое при каждом чтении; `MatchesSearch` ищет в этом preview. Это создаёт повторные аллокации, но **не доказывает отсутствие поиска в хвосте**, поскольку исходный preview не обрезан. Если вводится bounded cached preview, поиск нужно перевести на полный текст отдельно и закрепить regression-тестом. Преобразование переносов/пробелов для поиска должно иметь явную семантику.

Empty/no-results и Escape→clear уже существуют. Полезная доработка — видимая кнопка очистки и текст, объясняющий отсутствие capture при паузе. История/настройки/backup используют DPAPI; большой список конкурентных возможностей не отменяет необходимости сначала доказать сохранность и согласованность snapshot/restore.

Runtime: .NET 8 завершает поддержку 2026-11-10, .NET 10 LTS поддерживается до 2028-11-14. Для self-contained приложения обновление runtime — обязанность издателя. Поэтому миграция на поддерживаемую LTS с clean build/E2E приоритетнее добавления rich text. [Microsoft support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)

## Что исследование не доказывает

- NeClipboard не назван быстрее любого конкурента: одинаковые сравнительные benchmarks не проводились.
- Feature parity на Windows/macOS, ARM64 и compatibility с каждым приложением не установлены.
- Открытая лицензия не означает автоматически безопасные binaries; закрытая — автоматически небезопасные.
- Ограниченное число источников не позволяет подтвердить полный security design, backup или accessibility каждого продукта.
- Публичный release и обновлённый сайт NeClipboard подтверждаются [сводкой приёмки](ACCEPTANCE.md) и фактическими ответами сервисов, а не этим исследованием.

Приоритизированные задачи и критерии приёмки: [BACKLOG.md](BACKLOG.md).
