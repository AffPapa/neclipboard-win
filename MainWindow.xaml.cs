using Microsoft.Win32;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Input;
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;
using WpfTextDataFormat = System.Windows.TextDataFormat;

namespace NeClipboardWin;

public partial class MainWindow : Window
{
    private const int MainHotkeyId = 101;
    private const int MaximumHistoryBytes = 15 * 1024 * 1024;
    private const int MaximumImageBytes = 3 * 1024 * 1024;
    private const int MaximumTextCharacters = 500_000;
    private const int MaximumImagePixels = 8_000_000;
    private readonly SettingsService _settingsService;
    private readonly EncryptedHistoryStore _historyStore;
    private readonly BackupService _backupService = new();
    private readonly System.Windows.Threading.DispatcherTimer _historySaveTimer;
    private readonly SemaphoreSlim _historyWriteGate = new(1, 1);
    private readonly bool _isolatedQaMode;
    private readonly bool _desktopQaMode;
    private readonly string? _qaFixtureDirectory;
    private readonly ObservableCollection<ClipboardEntry> _entries = [];
    private readonly ICollectionView _filteredEntries;
    private AppSettings _settings;
    private Forms.NotifyIcon? _trayIcon;
    private HwndSource? _windowSource;
    private IntPtr _previousForegroundWindow;
    private uint _programmaticClipboardSequence;
    private bool _exiting;
    private bool _shutdownApproved;
    private string? _storageRecoveryNotice;
    private long _historyPayloadBytes;
    private bool _storageBusy;
    private bool _recoveryRequired;
    private long _historyEpoch;
    private Task<bool>? _historySaveTask;
    private bool _historySaveRequested;
    private bool _compactPalette = true;
    private bool _imageCapturePending;
    private (string Source, uint Sequence)? _latestImageCapture;
    private int _registeredHotkeyId = MainHotkeyId;
    private bool _hotkeyRegistered;
    private uint _registeredModifiers;
    private uint _registeredKey;
    private uint _pendingHotkeyModifiers = 6;
    private uint _pendingHotkeyKey = 0x56;
    private readonly System.Windows.Threading.DispatcherTimer _pauseTimer = new();
    private Forms.ToolStripMenuItem? _pauseMenuItem;
    private string _searchQuery = string.Empty;
    private long _pasteGeneration;

    public ICollectionView FilteredEntries => _filteredEntries;

    public MainWindow(string? appDataDirectory = null, bool isolatedQaMode = false, bool seedQaData = false, bool desktopQaMode = false)
    {
        _isolatedQaMode = isolatedQaMode;
        _desktopQaMode = isolatedQaMode && desktopQaMode;
        if (_desktopQaMode)
            _qaFixtureDirectory = Path.GetFullPath(Path.Combine(appDataDirectory ?? throw new ArgumentNullException(nameof(appDataDirectory)), "fixtures")) + Path.DirectorySeparatorChar;
        _settingsService = new SettingsService(appDataDirectory);
        _historyStore = new EncryptedHistoryStore(appDataDirectory);
        if (!_settingsService.RecoverPendingRestore())
            throw new IOException("Не удалось завершить восстановление локальных данных. Файлы сохранены для восстановления.");
        _historySaveTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
        _historySaveTimer.Tick += HistorySaveTimer_Tick;
        InitializeComponent();
        DataContext = this;
        var version = typeof(MainWindow).Assembly.GetName().Version;
        VersionText.Text = $"NeClipboard Win {version?.ToString(3) ?? "—"} · Windows x64";

        _settings = _settingsService.Load();
        _pendingHotkeyModifiers = _settings.HotkeyModifiers;
        _pendingHotkeyKey = _settings.HotkeyVirtualKey;
        var loadedEntries = _historyStore.Load();
        foreach (var entry in loadedEntries.OrderByDescending(SortKey)) _entries.Add(entry);
        if (seedQaData && _entries.Count == 0)
        {
            _entries.Add(CreateEntry(ClipboardEntryKind.Text, "Пример текста для изолированной проверки", "NeClipboardWin.QA.exe"));
            _entries.Add(CreateEntry(ClipboardEntryKind.Files, "C:\\Документы\\пример.pdf\nC:\\Изображения\\макет.png", "NeClipboardWin.QA.exe"));
        }
        _historyPayloadBytes = _entries.Sum(GetPayloadSize);
        if (_settingsService.RecoveredFromPreviousVersion || _historyStore.RecoveredFromPreviousVersion)
            _storageRecoveryNotice = "Не удалось прочитать один из локальных файлов. Восстановлена предыдущая локальная версия; проверьте историю и настройки.";
        else if (_settingsService.LastLoadRecovered || _historyStore.LastLoadRecovered)
            _storageRecoveryNotice = "Не удалось прочитать один из локальных файлов. Для него использованы начальные данные; проверьте историю и настройки.";
        _filteredEntries = CollectionViewSource.GetDefaultView(_entries);
        _filteredEntries.Filter = MatchesSearch;

        CaptureEnabledCheckBox.IsChecked = _settings.CaptureEnabled;
        StartupCheckBox.IsChecked = _settings.LaunchAtSignIn;
        AutoPasteCheckBox.IsChecked = _settings.AutoPasteAfterSelection;
        MaxHistoryTextBox.Text = _settings.MaxHistoryItems.ToString();
        ExcludedAppsTextBox.Text = string.Join(", ", _settings.ExcludedApplications);
        SnippetList.ItemsSource = _settings.Snippets;
        if (TrimHistory()) ScheduleHistorySave();
        _pauseTimer.Tick += PauseTimer_Tick;
        UpdateCaptureState();
        UpdateHotkeyLabels();
        RefreshHistory();
        if (!_isolatedQaMode || _desktopQaMode) CreateTrayIcon();
        UpdateActionState();
        SetCompactPalette(true);
        Loaded += MainWindow_Loaded;
    }

    private static DateTime SortKey(ClipboardEntry entry) => entry.IsPinned ? DateTime.MaxValue : entry.LastUsedUtc;

    private static bool IsSyntheticQaMarker(object? marker)
    {
        const string expected = "NeClipboardWin-QA";
        if (marker is string text) return text == expected;
        return marker is MemoryStream stream && stream.Length == expected.Length &&
            stream.ToArray().AsSpan().SequenceEqual("NeClipboardWin-QA"u8);
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        _windowSource = PresentationSource.FromVisual(this) as HwndSource;
        if (_windowSource is null) return;

        ThemeManager.ApplyWindowTheme(this);
        if (_isolatedQaMode && !_desktopQaMode)
        {
            SetStatus("Изолированный QA-режим: системные интеграции отключены.");
            return;
        }
        _windowSource.AddHook(WindowMessageHook);
        var listenerRegistered = NativeMethods.AddClipboardFormatListener(_windowSource.Handle);
        _hotkeyRegistered = NativeMethods.RegisterHotKey(
            _windowSource.Handle,
            MainHotkeyId,
            _settings.HotkeyModifiers | NativeMethods.ModNoRepeat,
            _settings.HotkeyVirtualKey);
        _registeredModifiers = _settings.HotkeyModifiers;
        _registeredKey = _settings.HotkeyVirtualKey;
        // Restored settings must agree with the actual Run entry after a crash recovery.
        var startupReconciled = _isolatedQaMode || ConfigureStartup(_settings.LaunchAtSignIn);

        SetStatus(listenerRegistered && _hotkeyRegistered
            ? $"{GetCaptureDescription()} · {FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey)} открывает историю"
            : !listenerRegistered ? "Не удалось подключиться к буферу Windows. Перезапустите программу."
            : "Горячая клавиша занята. Выберите другую в настройках; история доступна из трея.");
        if (!string.IsNullOrEmpty(_storageRecoveryNotice)) SetStatus(_storageRecoveryNotice);
        if (!startupReconciled)
            SetStatus("История доступна, но Windows не разрешила согласовать автозапуск. Проверьте его в настройках.");
        if (_desktopQaMode)
            SetStatus("Изолированная desktop QA: захватываются только помеченные синтетические данные тестового окна.");
    }

    private IntPtr WindowMessageHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message is NativeMethods.WmSettingChange or NativeMethods.WmThemeChanged)
        {
            ThemeManager.ApplyWindowTheme(this);
        }
        else if (message == NativeMethods.WmClipboardUpdate)
        {
            // A clipboard notification can race with another producer. Capture the
            // owner only when the sequence number is stable, otherwise the next
            // notification will provide a consistent pair.
            var sequenceBeforeOwnerLookup = NativeMethods.GetClipboardSequenceNumber();
            var sourceApplication = GetProcessNameForWindow(NativeMethods.GetClipboardOwner());
            var clipboardSequence = NativeMethods.GetClipboardSequenceNumber();
            if (sequenceBeforeOwnerLookup == 0 || sequenceBeforeOwnerLookup != clipboardSequence) return IntPtr.Zero;
            Dispatcher.BeginInvoke(() => CaptureClipboard(sourceApplication, clipboardSequence));
        }
        else if (message == NativeMethods.WmHotkey && wParam.ToInt32() == _registeredHotkeyId)
        {
            TogglePaletteFromHotkey();
            handled = true;
        }
        return IntPtr.Zero;
    }

    private async void CaptureClipboard(string sourceApplication, uint clipboardSequence, int attempt = 0)
    {
        if (_exiting || _storageBusy || !IsCaptureActive || IsExcludedSourceApplication(sourceApplication)) return;
        if (_desktopQaMode && !sourceApplication.Equals("NeClipboardWin.DesktopVerification.exe", StringComparison.OrdinalIgnoreCase)) return;
        if (clipboardSequence == 0 || NativeMethods.GetClipboardSequenceNumber() != clipboardSequence) return;
        if (clipboardSequence == _programmaticClipboardSequence)
        {
            _programmaticClipboardSequence = 0;
            return;
        }

        try
        {
            var clipboardData = System.Windows.Clipboard.GetDataObject();
            if (_desktopQaMode && !IsSyntheticQaMarker(clipboardData?.GetData("NeClipboardWin.SyntheticQa"))) return;
            // Windows and privacy-aware applications can publish this marker for sensitive data.
            if (ClipboardPrivacy.ShouldSkipCapture(clipboardData))
            {
                SetStatus("Конфиденциальный элемент буфера пропущен.");
                return;
            }

            ClipboardEntry? entry = null;
            if (System.Windows.Clipboard.ContainsFileDropList())
            {
                var fileDropList = System.Windows.Clipboard.GetFileDropList();
                if (fileDropList.Count > 100)
                {
                    SetStatus("Больше 100 файлов: элемент не сохранён. Исходный буфер Windows не изменён.");
                    return;
                }
                var files = fileDropList.Cast<string>().ToArray();
                if (_desktopQaMode && files.Any(path => !Path.GetFullPath(path).StartsWith(_qaFixtureDirectory!, StringComparison.OrdinalIgnoreCase))) return;
                if (files.Length > 0)
                    entry = CreateEntry(ClipboardEntryKind.Files, string.Join('\n', files), sourceApplication);
            }
            else if (System.Windows.Clipboard.ContainsImage())
            {
                if (_imageCapturePending)
                {
                    _latestImageCapture = (sourceApplication, clipboardSequence);
                    return;
                }
                var bitmap = System.Windows.Clipboard.GetImage();
                if (bitmap is null) return;
                if (_desktopQaMode && (bitmap.PixelWidth != 2 || bitmap.PixelHeight != 2)) return;
                if ((long)bitmap.PixelWidth * bitmap.PixelHeight > MaximumImagePixels)
                {
                    SetStatus("Изображение превышает лимит 8 миллионов пикселей.");
                    return;
                }
                var frozenBitmap = bitmap.Clone();
                frozenBitmap.Freeze();
                var epoch = _historyEpoch;
                byte[]? png;
                _imageCapturePending = true;
                try { png = await Task.Run(() => EncodeImagePng(frozenBitmap)); }
                finally
                {
                    _imageCapturePending = false;
                    if (_latestImageCapture is { } latest)
                    {
                        _latestImageCapture = null;
                        _ = Dispatcher.BeginInvoke(() => CaptureClipboard(latest.Source, latest.Sequence));
                    }
                }
                // Settings may have changed while PNG encoding ran off-thread.
                // Recheck the original clipboard owner, not the current foreground app.
                if (_exiting || _storageBusy || epoch != _historyEpoch || !IsCaptureActive || IsExcludedSourceApplication(sourceApplication)) return;
                if (png is { Length: > 0 and <= MaximumImageBytes })
                    entry = CreateEntry(ClipboardEntryKind.Image, Convert.ToBase64String(png), sourceApplication);
                else if (png is { Length: > MaximumImageBytes }) SetStatus("Изображение слишком большое для локальной истории.");
            }
            else if (System.Windows.Clipboard.ContainsText(WpfTextDataFormat.UnicodeText))
            {
                var text = System.Windows.Clipboard.GetText(WpfTextDataFormat.UnicodeText);
                if (_desktopQaMode && !text.StartsWith("NeClipboardWin-QA:", StringComparison.Ordinal)) return;
                if (!string.IsNullOrWhiteSpace(text) && text.Length <= MaximumTextCharacters)
                    entry = CreateEntry(ClipboardEntryKind.Text, text, sourceApplication);
                else if (text.Length > MaximumTextCharacters) SetStatus("Текст слишком большой для локальной истории.");
            }

            if (entry is not null)
            {
                if (clipboardSequence == _programmaticClipboardSequence)
                {
                    _programmaticClipboardSequence = 0;
                    return;
                }
                if (NativeMethods.GetClipboardSequenceNumber() != clipboardSequence) return;
                AddOrPromoteEntry(entry);
            }
        }
        catch (ExternalException)
        {
            if (attempt >= 3)
            {
                SetStatus("Буфер занят другим приложением — элемент не сохранён.");
                return;
            }
            await Task.Delay(120 * (attempt + 1));
            CaptureClipboard(sourceApplication, clipboardSequence, attempt + 1);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or IOException or NotSupportedException or FormatException)
        {
            SetStatus("Этот формат буфера не удалось прочитать. История осталась без изменений.");
        }
    }

    private ClipboardEntry CreateEntry(ClipboardEntryKind kind, string payload, string? sourceApplication = null)
    {
        var bytes = Encoding.UTF8.GetBytes($"{kind}\0{payload}");
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        return new ClipboardEntry
        {
            Kind = kind,
            Payload = payload,
            ContentHash = hash,
            SourceApplication = sourceApplication ?? GetForegroundProcessName(),
            CreatedUtc = DateTime.UtcNow,
            LastUsedUtc = DateTime.UtcNow
        };
    }

    private void AddOrPromoteEntry(ClipboardEntry entry)
    {
        var existing = _entries.FirstOrDefault(item => item.ContentHash == entry.ContentHash && item.Kind == entry.Kind && string.Equals(item.Payload, entry.Payload, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.LastUsedUtc = DateTime.UtcNow;
            MoveEntryToTop(existing);
            SetStatus("Элемент уже был в истории — перемещён наверх.");
        }
        else
        {
            _entries.Insert(_entries.TakeWhile(item => item.IsPinned).Count(), entry);
            _historyPayloadBytes += GetPayloadSize(entry);
            SetStatus($"Сохранено: {entry.TypeLabel.ToLowerInvariant()}.");
        }

        TrimHistory();
        ScheduleHistorySave();
        RefreshHistory();
    }

    private bool IsExcludedSourceApplication(string sourceApplication)
    {
        return _settings.ExcludedApplications.Any(app => string.Equals(app, sourceApplication, StringComparison.OrdinalIgnoreCase));
    }

    private static string GetForegroundProcessName()
    {
        try
        {
            return GetProcessNameForWindow(NativeMethods.GetForegroundWindow());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string GetProcessNameForWindow(IntPtr hwnd)
    {
        try
        {
            if (hwnd == IntPtr.Zero) return string.Empty;
            NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName + ".exe";
        }
        catch
        {
            return string.Empty;
        }
    }

    private static byte[]? EncodeImagePng(BitmapSource bitmap)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }

    private void PasteButton_Click(object sender, RoutedEventArgs e) => PasteSelectedEntry();
    private void HistoryList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => PasteSelectedEntry();

    private void PasteSelectedEntry()
    {
        if (_storageBusy || _exiting) return;
        if (HistoryList.SelectedItem is not ClipboardEntry entry)
        {
            SetStatus("Выберите элемент истории.");
            return;
        }
        PasteEntry(entry);
    }

    private void PasteEntry(ClipboardEntry entry)
    {
        if (_storageBusy || _exiting) return;
        var pasteGeneration = ++_pasteGeneration;
        _previousForegroundWindow = _previousForegroundWindow == IntPtr.Zero ? NativeMethods.GetForegroundWindow() : _previousForegroundWindow;
        try
        {
            var clipboardData = new System.Windows.DataObject();
            switch (entry.Kind)
            {
                case ClipboardEntryKind.Text:
                    clipboardData.SetText(entry.Payload, WpfTextDataFormat.UnicodeText);
                    break;
                case ClipboardEntryKind.Files:
                    var fileList = new System.Collections.Specialized.StringCollection();
                    foreach (var path in entry.Payload.Split('\n', StringSplitOptions.RemoveEmptyEntries)) fileList.Add(path);
                    clipboardData.SetFileDropList(fileList);
                    break;
                case ClipboardEntryKind.Image:
                    var bytes = Convert.FromBase64String(entry.Payload);
                    using (var imageStream = new MemoryStream(bytes))
                    {
                        var decoder = BitmapDecoder.Create(imageStream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        clipboardData.SetImage(decoder.Frames[0]);
                    }
                    break;
            }
            ClipboardPrivacy.ApplyPrivateReplayPolicy(clipboardData);
            System.Windows.Clipboard.SetDataObject(clipboardData, copy: true);
            _programmaticClipboardSequence = NativeMethods.GetClipboardSequenceNumber();
            var pasteClipboardSequence = _programmaticClipboardSequence;

            entry.LastUsedUtc = DateTime.UtcNow;
            MoveEntryToTop(entry);
            TrimHistory();
            ScheduleHistorySave();
            RefreshHistory();
            Hide();
            if (!_settings.AutoPasteAfterSelection)
            {
                SetStatus("Элемент скопирован. Вставьте его вручную через Ctrl + V.");
                return;
            }

            var targetWindow = _previousForegroundWindow;
            // Activate only as part of this explicit selection. Never steal focus again
            // after the delay: the user may have switched applications in the meantime.
            var targetAllowed = !_desktopQaMode || GetProcessNameForWindow(targetWindow).Equals("NeClipboardWin.DesktopVerification.exe", StringComparison.OrdinalIgnoreCase);
            var targetActivated = targetAllowed && targetWindow != IntPtr.Zero && NativeMethods.SetForegroundWindow(targetWindow);
            Dispatcher.BeginInvoke(async () =>
            {
                await Task.Delay(170);
                if (_exiting || pasteGeneration != _pasteGeneration) return;
                if (!targetActivated || NativeMethods.GetForegroundWindow() != targetWindow ||
                    pasteClipboardSequence == 0 || NativeMethods.GetClipboardSequenceNumber() != pasteClipboardSequence ||
                    Keyboard.Modifiers != ModifierKeys.None)
                {
                    SetStatus("Автовставка отменена: изменились окно, буфер или нажатые клавиши. При необходимости вставьте вручную.");
                    return;
                }

                var sentInputs = NativeMethods.SendPasteShortcut();
                SetStatus(sentInputs == 4
                    ? "Элемент скопирован и отправлен на вставку."
                    : "Элемент скопирован. Windows заблокировала автовставку — вставьте вручную.");
            });
        }
        catch (Exception)
        {
            SetStatus("Не удалось подготовить элемент. Он оставлен без изменений.");
        }
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        if (_storageBusy || _exiting) return;
        if (HistoryList.SelectedItem is not ClipboardEntry entry) return;
        entry.IsPinned = !entry.IsPinned;
        MoveEntryToTop(entry);
        ScheduleHistorySave();
        RefreshHistory();
    }

    private void DeleteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_storageBusy || _exiting) return;
        if (HistoryList.SelectedItem is not ClipboardEntry entry) return;
        _entries.Remove(entry);
        _historyPayloadBytes -= GetPayloadSize(entry);
        ScheduleHistorySave();
        RefreshHistory();
    }

    private async void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        if (_storageBusy || _exiting) return;
        if (WpfMessageBox.Show("Удалить всю локальную историю буфера? Закреплённые элементы тоже будут удалены.", "NeClipboard Win", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _historySaveTimer.Stop();
        _historySaveRequested = false;
        _historyEpoch++;
        SetStorageBusy(true);
        await _historyWriteGate.WaitAsync();
        var cleared = false;
        try
        {
            cleared = await Task.Run(_historyStore.TryClear);
            if (cleared)
            {
                _entries.Clear();
                _historyPayloadBytes = 0;
            }
        }
        finally
        {
            _historyWriteGate.Release();
            SetStorageBusy(false);
        }
        if (!cleared)
        {
            SetStatus("Не удалось очистить файл истории.");
            return;
        }
        RefreshHistory();
        SetStatus("История удалена.");
    }

    private async void CreateBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_storageBusy || _exiting) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Создать резервную копию NeClipboard Win",
            Filter = "Резервная копия NeClipboard (*.neclipboard-backup)|*.neclipboard-backup",
            DefaultExt = ".neclipboard-backup",
            FileName = $"NeClipboard-{DateTime.Now:yyyy-MM-dd}.neclipboard-backup",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != true) return;

        var settingsSnapshot = _settings.DeepClone();
        var entriesSnapshot = _entries.Select(entry => entry.DeepClone()).ToArray();
        SetStorageBusy(true);
        SetStatus("Создаётся резервная копия…");
        bool created;
        try { created = await Task.Run(() => _backupService.TryCreate(dialog.FileName, settingsSnapshot, entriesSnapshot)); }
        finally { SetStorageBusy(false); }
        SetStatus(created
            ? "Резервная копия создана и защищена для текущей учётной записи Windows."
            : "Не удалось создать резервную копию. Проверьте путь и свободное место.");
    }

    private async void RestoreBackupButton_Click(object sender, RoutedEventArgs e)
    {
        if (_storageBusy || _exiting) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Восстановить резервную копию NeClipboard Win",
            Filter = "Резервная копия NeClipboard (*.neclipboard-backup)|*.neclipboard-backup",
            Multiselect = false,
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) != true) return;
        SetStorageBusy(true);
        SetStatus("Проверяется резервная копия…");
        var read = await Task.Run(() =>
        {
            var valid = _backupService.TryRead(dialog.FileName, out var settings, out var entries);
            return (Valid: valid, Settings: settings, Entries: entries);
        });
        SetStorageBusy(false);
        if (!read.Valid)
        {
            SetStatus("Не удалось открыть копию: файл повреждён, слишком большой или создан для другой учётной записи Windows.");
            return;
        }
        if (WpfMessageBox.Show("Восстановить историю, сниппеты и настройки из копии? Текущие локальные данные будут заменены.", "NeClipboard Win", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;

        var restoredSettings = read.Settings;
        var restoredEntries = read.Entries;
        var previousSettings = _settings.DeepClone();
        if (!TryApplyNativeSettings(restoredSettings, out var nativeError))
        {
            var rollbackSucceeded = TryApplyNativeSettings(previousSettings, out _);
            SetStatus(rollbackSucceeded ? nativeError : $"{nativeError} Откат системных настроек также не завершён; проверьте сочетание и автозапуск.");
            return;
        }
        _historySaveTimer.Stop();
        _historySaveRequested = false;
        _historyEpoch++;
        SetStorageBusy(true);
        await _historyWriteGate.WaitAsync();
        var result = RestoreResult.Failed;
        var restoredCount = restoredEntries.Count;
        var trimmedCount = 0;
        try
        {
            result = await Task.Run(() => _settingsService.TryRestoreWithHistory(_historyStore, restoredSettings, restoredEntries));
            if (result == RestoreResult.Applied)
            {
                _settings = restoredSettings;
                _entries.Clear();
                foreach (var entry in restoredEntries.OrderByDescending(SortKey)) _entries.Add(entry);
                _historyPayloadBytes = _entries.Sum(GetPayloadSize);
                LoadSettingsControls();
                TrimHistory();
                trimmedCount = restoredCount - _entries.Count;
            }
        }
        finally
        {
            _historyWriteGate.Release();
            SetStorageBusy(false);
        }
        if (result != RestoreResult.Applied)
        {
            var nativeRolledBack = TryApplyNativeSettings(previousSettings, out _);
            if (result == RestoreResult.RecoveryRequired)
            {
                _recoveryRequired = true;
                _settings.CaptureEnabled = false;
                SetStorageBusy(true);
                SetStatus("Восстановление прервано. Данные защищены журналом восстановления; закройте и запустите программу снова.");
            }
            else SetStatus(nativeRolledBack ? "Восстановление не завершено. Исходные данные восстановлены." : "Данные восстановлены, но системные настройки требуют проверки.");
            return;
        }
        UpdateCaptureState();
        UpdateHotkeyLabels();
        RefreshHistory();
        UpdateActionState();
        SetStatus(trimmedCount > 0
            ? $"Резервная копия восстановлена. Удалено старых записей по лимиту истории: {trimmedCount}. Исходная копия не изменена."
            : "Резервная копия восстановлена.");
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => RefreshHistory();
    private void ClearSearchButton_Click(object sender, RoutedEventArgs e) { SearchBox.Clear(); SearchBox.Focus(); }
    private void HistoryFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshHistory();
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (e.Source != MainTabs) return;
        SearchPanel.Visibility = !_compactPalette && MainTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_compactPalette && IsLoaded)
            FocusActiveList();
        if (ShortcutHints is not null) UpdateActionState();
    }

    private void FocusActiveList()
    {
        if (!IsLoaded) return;
        Dispatcher.BeginInvoke(new Action(FocusActiveListCore), System.Windows.Threading.DispatcherPriority.Input);
    }

    private void FocusActiveListCore()
    {
        if (MainTabs.SelectedIndex == 0)
        {
            if (HistoryList.Items.Count > 0 && HistoryList.SelectedIndex < 0) HistoryList.SelectedIndex = 0;
            FocusSelectedListItem(HistoryList);
        }
        else if (MainTabs.SelectedIndex == 1)
        {
            if (SnippetList.Items.Count > 0 && SnippetList.SelectedIndex < 0) SnippetList.SelectedIndex = 0;
            FocusSelectedListItem(SnippetList);
        }
    }

    private static void FocusSelectedListItem(System.Windows.Controls.ListBox list)
    {
        if (list.SelectedIndex >= 0 && list.ItemContainerGenerator.ContainerFromIndex(list.SelectedIndex) is ListBoxItem item)
            item.Focus();
        else
            list.Focus();
    }

    private void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_compactPalette) FocusActiveList();
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionState();
    private void SnippetList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateActionState();

    private bool MatchesSearch(object item)
    {
        if (item is not ClipboardEntry entry) return false;
        var matchesType = HistoryFilterBox?.SelectedIndex switch
        {
            1 => entry.Kind == ClipboardEntryKind.Text,
            2 => entry.Kind == ClipboardEntryKind.Image,
            3 => entry.Kind == ClipboardEntryKind.Files,
            4 => entry.IsPinned,
            _ => true
        };
        return matchesType && entry.MatchesSearch(_searchQuery);
    }

    private void RefreshHistory()
    {
        if (_filteredEntries is null) return;
        _searchQuery = SearchBox.Text.Trim();
        _filteredEntries.Refresh();
        var hasVisibleEntries = _filteredEntries.Cast<object>().Any();
        if (hasVisibleEntries && HistoryList.SelectedIndex < 0) HistoryList.SelectedIndex = 0;
        HistoryEmptyState.Visibility = hasVisibleEntries ? Visibility.Collapsed : Visibility.Visible;
        var filtered = _searchQuery.Length > 0 || HistoryFilterBox.SelectedIndex > 0;
        HistoryEmptyTitle.Text = filtered ? "Ничего не найдено" : IsCaptureActive ? "История пока пуста" : "История на паузе";
        HistoryEmptyDescription.Text = filtered ? "Измените запрос или выберите фильтр «Всё»." : IsCaptureActive ? "Скопируйте текст, файл или изображение — оно появится здесь." : "Продолжите сбор истории кнопкой «Продолжить» или через меню в трее.";
        SearchPlaceholder.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        ClearSearchButton.Visibility = SearchBox.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateActionState();
    }

    private void MoveEntryToTop(ClipboardEntry entry)
    {
        if (!_entries.Remove(entry)) _historyPayloadBytes += GetPayloadSize(entry);
        _entries.Insert(entry.IsPinned ? 0 : _entries.TakeWhile(item => item.IsPinned).Count(), entry);
    }

    private bool TrimHistory()
    {
        var allowed = Math.Clamp(_settings.MaxHistoryItems, 10, 1000);
        _settings.MaxHistoryItems = allowed;
        var changed = false;
        while (_entries.Count > allowed || GetHistoryPayloadSize() > MaximumHistoryBytes)
        {
            var candidate = _entries.LastOrDefault(entry => !entry.IsPinned) ?? _entries.LastOrDefault();
            if (candidate is null) break;
            _entries.Remove(candidate);
            _historyPayloadBytes -= GetPayloadSize(candidate);
            changed = true;
        }
        return changed;
    }

    private static int GetPayloadSize(ClipboardEntry entry) => Encoding.UTF8.GetByteCount(entry.Payload);

    private long GetHistoryPayloadSize() => _historyPayloadBytes;

    private void ScheduleHistorySave()
    {
        if (_exiting) return;
        _historySaveTimer.Stop();
        _historySaveTimer.Start();
    }

    private async void HistorySaveTimer_Tick(object? sender, EventArgs e)
    {
        _historySaveTimer.Stop();
        await SaveHistoryAsync();
    }

    private Task<bool> SaveHistoryAsync()
    {
        _historySaveRequested = true;
        if (_historySaveTask is null || _historySaveTask.IsCompleted)
            _historySaveTask = DrainHistorySavesAsync();
        return _historySaveTask;
    }

    private async Task<bool> DrainHistorySavesAsync()
    {
        var lastSaveSucceeded = true;
        while (_historySaveRequested)
        {
            _historySaveRequested = false;
            var epoch = _historyEpoch;
            await _historyWriteGate.WaitAsync();
            try
            {
                if (epoch != _historyEpoch || _storageBusy) continue;
                var snapshot = _entries.Select(entry => entry.DeepClone()).ToArray();
                var saved = await Task.Run(() => _historyStore.TrySave(snapshot));
                lastSaveSucceeded = saved;
                if (!saved) SetStatus("Не удалось сохранить историю. Проверьте доступ к локальной папке приложения.");
            }
            finally { _historyWriteGate.Release(); }
        }
        return lastSaveSucceeded;
    }

    private void UpdateActionState()
    {
        var historySelection = !_storageBusy && !_exiting && HistoryList.SelectedItem is ClipboardEntry;
        PinButton.IsEnabled = historySelection;
        DeleteButton.IsEnabled = historySelection;
        PasteButton.IsEnabled = historySelection;
        if (HistoryList.SelectedItem is ClipboardEntry entry)
            PinButton.Content = entry.IsPinned ? "Открепить" : "Закрепить";

        var snippetSelection = !_storageBusy && !_exiting && SnippetList.SelectedItem is Snippet;
        EditSnippetButton.IsEnabled = snippetSelection;
        DeleteSnippetButton.IsEnabled = snippetSelection;
        PasteSnippetButton.IsEnabled = snippetSelection;
        var actionName = _settings.AutoPasteAfterSelection ? "Вставить" : "Скопировать";
        PasteButton.Content = actionName;
        PasteSnippetButton.Content = actionName;
        var enterAction = _settings.AutoPasteAfterSelection ? "вставить" : "скопировать";
        ShortcutHints.Text = _compactPalette
            ? MainTabs.SelectedIndex == 1
                ? $"Enter — {enterAction} · Esc — закрыть"
                : $"↑ ↓ — выбрать · Enter — {enterAction} · Esc — закрыть"
            : MainTabs.SelectedIndex switch
        {
            1 => $"Esc — скрыть · Enter — {enterAction} выбранную заготовку",
            2 => "Esc — скрыть · Настройки применяются кнопкой «Сохранить»",
            _ => $"Esc — очистить поиск / скрыть · Enter — {enterAction} · Delete — удалить"
        };
    }

    private async void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_storageBusy || _exiting) return;
        if (!int.TryParse(MaxHistoryTextBox.Text, out var limit) || limit is < 10 or > 1000)
        {
            SetStatus("Введите размер истории от 10 до 1000.");
            MaxHistoryTextBox.Focus();
            return;
        }
        var updated = _settings.DeepClone();
        updated.CaptureEnabled = CaptureEnabledCheckBox.IsChecked == true;
        if (!updated.CaptureEnabled) updated.CapturePausedUntilUtc = null;
        updated.LaunchAtSignIn = StartupCheckBox.IsChecked == true;
        updated.AutoPasteAfterSelection = AutoPasteCheckBox.IsChecked == true;
        updated.MaxHistoryItems = limit;
        updated.HotkeyModifiers = _pendingHotkeyModifiers;
        updated.HotkeyVirtualKey = _pendingHotkeyKey;
        updated.ExcludedApplications = ExcludedAppsTextBox.Text.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (await SaveSettingsAsync(updated, "Настройки сохранены.", applyNative: true, refreshSettingsControls: true))
        {
            TrimHistory();
            ScheduleHistorySave();
            RefreshHistory();
        }
    }

    private static bool ConfigureStartup(bool enabled)
    {
        try
        {
            var command = $"\"{Environment.ProcessPath}\" --autostart";
            using (var current = Registry.CurrentUser.OpenSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", writable: false))
            {
                var existing = current?.GetValue("NeClipboardWin");
                if (enabled ? string.Equals(existing as string, command, StringComparison.OrdinalIgnoreCase) : existing is null)
                    return true;
            }
            using var key = Registry.CurrentUser.CreateSubKey("Software\\Microsoft\\Windows\\CurrentVersion\\Run", true);
            if (enabled) key.SetValue("NeClipboardWin", command);
            else key.DeleteValue("NeClipboardWin", false);
            return true;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or System.Security.SecurityException or IOException) { return false; }
    }

    private async void AddSnippetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_storageBusy) return;
        var editor = new SnippetEditorWindow(validate: ValidateSnippetChange) { Owner = this };
        if (editor.ShowDialog() != true) return;
        var updated = _settings.DeepClone();
        var added = editor.Result.DeepClone();
        updated.Snippets.Add(added);
        if (!await SaveSettingsAsync(updated, "Сниппет добавлен.")) return;
        SnippetList.SelectedItem = _settings.Snippets.FirstOrDefault(item => item.Id == added.Id);
        FocusActiveList();
    }

    private async void EditSnippetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_storageBusy) return;
        if (SnippetList.SelectedItem is not Snippet selected) return;
        var editor = new SnippetEditorWindow(selected, ValidateSnippetChange) { Owner = this };
        if (editor.ShowDialog() != true) return;
        var updated = _settings.DeepClone();
        var edited = updated.Snippets.Single(item => item.Id == selected.Id);
        edited.Title = editor.Result.Title;
        edited.Content = editor.Result.Content;
        edited.UpdatedUtc = DateTime.UtcNow;
        if (!await SaveSettingsAsync(updated, "Сниппет сохранён.")) return;
        FocusActiveList();
    }

    private async void DeleteSnippetButton_Click(object sender, RoutedEventArgs e)
    {
        if (_storageBusy) return;
        if (SnippetList.SelectedItem is not Snippet selected) return;
        var previousIndex = SnippetList.SelectedIndex;
        var updated = _settings.DeepClone();
        updated.Snippets.RemoveAll(item => item.Id == selected.Id);
        if (!await SaveSettingsAsync(updated, "Сниппет удалён.")) return;
        if (_settings.Snippets.Count > 0) SnippetList.SelectedIndex = Math.Min(previousIndex, _settings.Snippets.Count - 1);
        FocusActiveList();
    }

    private string? ValidateSnippetChange(Snippet proposed)
    {
        var updated = _settings.DeepClone();
        updated.Snippets.RemoveAll(item => item.Id == proposed.Id);
        updated.Snippets.Add(proposed.DeepClone());
        return StorageSafety.ValidSettings(updated) ? null
            : "Лимит заготовок превышен: до 1000 записей и 2 МиБ текста суммарно. Сократите текст или отмените изменение.";
    }

    private void SnippetList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e) => PasteSelectedSnippet();
    private void PasteSnippetButton_Click(object sender, RoutedEventArgs e) => PasteSelectedSnippet();

    private void PasteSelectedSnippet()
    {
        if (_storageBusy) return;
        if (SnippetList.SelectedItem is not Snippet snippet) return;
        PasteEntry(CreateEntry(ClipboardEntryKind.Text, snippet.Content));
    }

    // A deadline alone never resumes capture: clearing it must persist successfully.
    // This keeps the privacy indicator and actual capture state identical on disk errors.
    private bool IsCaptureActive => _settings.CaptureEnabled && _settings.CapturePausedUntilUtc is null;

    private string GetCaptureDescription() => IsCaptureActive ? "Локальная история буфера" :
        _settings.CaptureEnabled && _settings.CapturePausedUntilUtc is { } until && until > DateTime.UtcNow
            ? $"Пауза до {until.ToLocalTime():HH:mm}" : "История на паузе";

    private void UpdateCaptureState(bool schedulePauseTimer = true)
    {
        CaptureStateText.Text = GetCaptureDescription();
        PauseButton.Content = IsCaptureActive ? "Пауза 15 мин" : "Продолжить";
        if (_pauseMenuItem is not null) _pauseMenuItem.Text = IsCaptureActive ? "Пауза на 15 минут" : "Продолжить историю";
        if (_trayIcon is not null) _trayIcon.Text = $"NeClipboard Win — {GetCaptureDescription()}";
        _pauseTimer.Stop();
        if (schedulePauseTimer && !_exiting && _settings.CaptureEnabled && _settings.CapturePausedUntilUtc is { } until)
        {
            var delay = until - DateTime.UtcNow + TimeSpan.FromMilliseconds(100);
            _pauseTimer.Interval = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 100, TimeSpan.FromHours(1).TotalMilliseconds));
            _pauseTimer.Start();
        }
        RefreshHistory();
    }

    private async void PauseTimer_Tick(object? sender, EventArgs e)
    {
        _pauseTimer.Stop();
        if (_exiting) return;
        if (_storageBusy)
        {
            _pauseTimer.Interval = TimeSpan.FromSeconds(2);
            _pauseTimer.Start();
            return;
        }
        if (!_settings.CaptureEnabled || _settings.CapturePausedUntilUtc is null || _settings.CapturePausedUntilUtc > DateTime.UtcNow)
        {
            UpdateCaptureState();
            return;
        }
        var updated = _settings.DeepClone();
        updated.CapturePausedUntilUtc = null;
        if (!await SaveSettingsAsync(updated, "Пауза завершена. История продолжена."))
        {
            UpdateCaptureState(schedulePauseTimer: false);
            SetStatus("История остаётся на паузе: не удалось сохранить возобновление. Проверьте доступ к данным и нажмите «Продолжить».");
        }
    }

    private async Task PauseForAsync(int minutes)
    {
        if (_storageBusy || _exiting) return;
        var updated = _settings.DeepClone();
        updated.CaptureEnabled = true;
        updated.CapturePausedUntilUtc = DateTime.UtcNow.AddMinutes(minutes);
        await SaveSettingsAsync(updated, $"История на паузе на {minutes} мин.");
    }

    private async void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_storageBusy || _exiting) return;
        if (IsCaptureActive) { await PauseForAsync(15); return; }
        var updated = _settings.DeepClone();
        updated.CaptureEnabled = true;
        updated.CapturePausedUntilUtc = null;
        await SaveSettingsAsync(updated, "История продолжена.");
    }

    private async Task<bool> SaveSettingsAsync(AppSettings updated, string success, bool applyNative = false, bool refreshSettingsControls = false)
    {
        if (_storageBusy || _exiting) return false;
        var preserveSettingsDraft = !refreshSettingsControls && HasSettingsDraft();
        SetStorageBusy(true);
        var previous = _settings.DeepClone();
        await _historyWriteGate.WaitAsync();
        try
        {
            if (applyNative && !TryApplyNativeSettings(updated, out var nativeError))
            {
                var rollbackSucceeded = TryApplyNativeSettings(previous, out _);
                SetStatus(rollbackSucceeded ? nativeError : $"{nativeError} Откат системных настроек также не завершён; проверьте сочетание и автозапуск.");
                return false;
            }
            if (!await Task.Run(() => _settingsService.TrySave(updated)))
            {
                var rollback = !applyNative || TryApplyNativeSettings(previous, out _);
                SetStatus(rollback ? "Настройки не сохранены. Проверьте значения и доступ к папке данных." : "Настройки не сохранены. Также проверьте горячую клавишу и автозапуск.");
                return false;
            }
            if (_settings.CaptureEnabled != updated.CaptureEnabled || _settings.CapturePausedUntilUtc != updated.CapturePausedUntilUtc)
                _historyEpoch++;
            _settings = updated;
            if (!preserveSettingsDraft) LoadSettingsControls();
            else RefreshSnippetItems();
            UpdateCaptureState();
            UpdateHotkeyLabels();
            SetStatus(success);
            return true;
        }
        finally
        {
            _historyWriteGate.Release();
            SetStorageBusy(false);
            // A settings operation must not discard a debounced history write.
            ScheduleHistorySave();
        }
    }

    private bool HasSettingsDraft() =>
        CaptureEnabledCheckBox.IsChecked != _settings.CaptureEnabled ||
        StartupCheckBox.IsChecked != _settings.LaunchAtSignIn ||
        AutoPasteCheckBox.IsChecked != _settings.AutoPasteAfterSelection ||
        MaxHistoryTextBox.Text.Trim() != _settings.MaxHistoryItems.ToString() ||
        ExcludedAppsTextBox.Text != string.Join(", ", _settings.ExcludedApplications) ||
        _pendingHotkeyModifiers != _settings.HotkeyModifiers || _pendingHotkeyKey != _settings.HotkeyVirtualKey;

    private void RefreshSnippetItems()
    {
        var selectedSnippet = (SnippetList.SelectedItem as Snippet)?.Id;
        SnippetList.ItemsSource = _settings.Snippets;
        SnippetList.SelectedItem = _settings.Snippets.FirstOrDefault(item => item.Id == selectedSnippet);
    }

    private void LoadSettingsControls()
    {
        CaptureEnabledCheckBox.IsChecked = _settings.CaptureEnabled;
        StartupCheckBox.IsChecked = _settings.LaunchAtSignIn;
        AutoPasteCheckBox.IsChecked = _settings.AutoPasteAfterSelection;
        MaxHistoryTextBox.Text = _settings.MaxHistoryItems.ToString();
        ExcludedAppsTextBox.Text = string.Join(", ", _settings.ExcludedApplications);
        _pendingHotkeyModifiers = _settings.HotkeyModifiers;
        _pendingHotkeyKey = _settings.HotkeyVirtualKey;
        RefreshSnippetItems();
    }

    private void SetStorageBusy(bool busy)
    {
        _storageBusy = busy;
        MainTabs.IsEnabled = !busy && !_exiting;
        PauseButton.IsEnabled = !busy && !_exiting;
        if (HistoryList is not null) UpdateActionState();
        if (!busy) ScheduleHistorySave();
    }

    private bool TryApplyNativeSettings(AppSettings settings, out string error)
    {
        error = string.Empty;
        if (_isolatedQaMode && !_desktopQaMode) return true;
        if (_windowSource is null) { error = "Окно ещё не готово к регистрации горячей клавиши."; return false; }
        if (!_hotkeyRegistered || _registeredModifiers != settings.HotkeyModifiers || _registeredKey != settings.HotkeyVirtualKey)
        {
            // Reserve the new chord before releasing the old one.
            var candidateId = _registeredHotkeyId == MainHotkeyId ? MainHotkeyId + 1 : MainHotkeyId;
            if (!NativeMethods.RegisterHotKey(_windowSource.Handle, candidateId, settings.HotkeyModifiers | NativeMethods.ModNoRepeat, settings.HotkeyVirtualKey))
            {
                error = "Это сочетание занято. Прежняя горячая клавиша сохранена; выберите другую.";
                return false;
            }
            if (_hotkeyRegistered) NativeMethods.UnregisterHotKey(_windowSource.Handle, _registeredHotkeyId);
            _registeredHotkeyId = candidateId;
            _registeredModifiers = settings.HotkeyModifiers;
            _registeredKey = settings.HotkeyVirtualKey;
            _hotkeyRegistered = true;
        }
        if (!_isolatedQaMode && !ConfigureStartup(settings.LaunchAtSignIn))
        {
            error = "Windows не разрешила изменить автозапуск. Настройки не применены.";
            return false;
        }
        return true;
    }

    private static string FormatHotkey(uint modifiers, uint virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & NativeMethods.ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & NativeMethods.ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & NativeMethods.ModShift) != 0) parts.Add("Shift");
        parts.Add(virtualKey is >= 0x70 and <= 0x7A ? $"F{virtualKey - 0x6F}" : ((char)virtualKey).ToString());
        return string.Join(" + ", parts);
    }

    private void UpdateHotkeyLabels()
    {
        HotkeyBadge.Text = FormatHotkey(_settings.HotkeyModifiers, _settings.HotkeyVirtualKey);
        EmptyHotkeyBadge.Text = HotkeyBadge.Text;
        HotkeyTextBox.Text = FormatHotkey(_pendingHotkeyModifiers, _pendingHotkeyKey);
    }

    private void HotkeyTextBox_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Tab) return;
        e.Handled = true;
        if (e.Key == Key.Escape)
        {
            _pendingHotkeyModifiers = _settings.HotkeyModifiers;
            _pendingHotkeyKey = _settings.HotkeyVirtualKey;
            UpdateHotkeyLabels();
            return;
        }
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);
        var modifiers = (uint)Keyboard.Modifiers & 7u;
        if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0 || (modifiers & 3u) == 0 ||
            !(virtualKey is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x70 and <= 0x7A))
        {
            SetStatus("Нажмите Ctrl или Alt вместе с буквой, цифрой или F1–F11. F12 зарезервирована Windows. Esc — отмена.");
            return;
        }
        _pendingHotkeyModifiers = modifiers;
        _pendingHotkeyKey = virtualKey;
        UpdateHotkeyLabels();
        SetStatus("Новое сочетание выбрано. Нажмите «Сохранить» для проверки и применения.");
    }

    private void CreateTrayIcon()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть историю", null, (_, _) => Dispatcher.Invoke(ShowPalette));
        _pauseMenuItem = (Forms.ToolStripMenuItem)menu.Items.Add("Пауза", null, (_, _) => Dispatcher.Invoke(() => PauseButton_Click(this, new RoutedEventArgs())));
        menu.Items.Add("Пауза на 15 минут", null, (_, _) => Dispatcher.Invoke(async () => await PauseForAsync(15)));
        menu.Items.Add("Пауза на 1 час", null, (_, _) => Dispatcher.Invoke(async () => await PauseForAsync(60)));
        menu.Items.Add("Очистить историю", null, (_, _) => Dispatcher.Invoke(() => ClearButton_Click(this, new RoutedEventArgs())));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(ExitApplication));
        _trayIcon = new Forms.NotifyIcon
        {
            Icon = LoadApplicationIcon(),
            Text = "NeClipboard Win",
            ContextMenuStrip = menu,
            Visible = true
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowPalette);
        UpdateCaptureState();
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        var executablePath = Environment.ProcessPath;
        using var icon = executablePath is null ? null : System.Drawing.Icon.ExtractAssociatedIcon(executablePath);
        return icon is null ? System.Drawing.SystemIcons.Application : (System.Drawing.Icon)icon.Clone();
    }

    private void ShowPalette()
    {
        if (_exiting) return;
        _pasteGeneration++;
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != _windowSource?.Handle) _previousForegroundWindow = foreground;
        SetCompactPalette(true);
        MainTabs.SelectedIndex = 0;
        SearchBox.Clear();
        var screen = Forms.Screen.FromPoint(Forms.Cursor.Position).WorkingArea;
        var transform = _windowSource?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(screen.Left, screen.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(screen.Right, screen.Bottom));
        Show();
        WindowState = WindowState.Normal;
        var cursor = transform.Transform(new System.Windows.Point(Forms.Cursor.Position.X, Forms.Cursor.Position.Y));
        Left = PalettePlacement.ClampOrigin(cursor.X + 12, topLeft.X, bottomRight.X - topLeft.X, Width);
        Top = PalettePlacement.ClampOrigin(cursor.Y + 12, topLeft.Y, bottomRight.Y - topLeft.Y, Height);
        Activate();
        Topmost = true;
        Topmost = false;
        FocusActiveList();
    }

    internal void ShowPaletteFromActivation() => ShowPalette();

    private void TogglePaletteFromHotkey()
    {
        if (_compactPalette && IsVisible && IsActive) Hide();
        else ShowPalette();
    }

    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_compactPalette)
        {
            SetCompactPalette(false);
            MainTabs.SelectedIndex = 2;
            SearchBox.Clear();
            Activate();
        }
        else
        {
            MainTabs.SelectedIndex = 0;
            SetCompactPalette(true);
            FocusActiveList();
        }
    }

    private void SetCompactPalette(bool compact)
    {
        _compactPalette = compact;
        SearchPanel.Visibility = !compact && MainTabs.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        BrandPanel.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SettingsTab.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        HotkeyBadgeBorder.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CaptureStateText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        HideButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        CompactClearButton.Visibility = compact ? Visibility.Visible : Visibility.Collapsed;
        StatusTextBlock.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        HistoryFilterBox.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        SearchGrid.ColumnDefinitions[2].Width = compact ? new GridLength(0) : new GridLength(142);
        MainTabs.Padding = compact ? new Thickness(8, 4, 8, 3) : new Thickness(10, 8, 10, 8);
        HistoryActions.Margin = compact ? new Thickness(0, 3, 0, 0) : new Thickness(0, 10, 0, 0);
        MainLayoutGrid.Margin = compact ? new Thickness(10) : new Thickness(22);
        HeaderPanel.Margin = compact ? new Thickness(0, 0, 0, 7) : new Thickness(0, 0, 0, 18);
        ContentBorder.Margin = compact ? new Thickness(0, 6, 0, 4) : new Thickness(0, 14, 0, 11);
        PauseButton.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        Width = compact ? 420 : 780;
        Height = compact ? 380 : 680;
        MinWidth = compact ? 380 : 560;
        MinHeight = compact ? 340 : 460;
        ModeButton.Content = compact ? "Настройки" : "Компактно";
        ModeButton.ToolTip = compact ? "Открыть полное окно" : "Вернуться к компактному списку";
        UpdateActionState();
        if (IsVisible) KeepWindowInCurrentWorkArea();
    }

    private void KeepWindowInCurrentWorkArea()
    {
        if (_windowSource?.Handle is not { } hwnd || hwnd == IntPtr.Zero) return;
        var workArea = Forms.Screen.FromHandle(hwnd).WorkingArea;
        var transform = _windowSource.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var topLeft = transform.Transform(new System.Windows.Point(workArea.Left, workArea.Top));
        var bottomRight = transform.Transform(new System.Windows.Point(workArea.Right, workArea.Bottom));
        Left = PalettePlacement.ClampOrigin(Left, topLeft.X, bottomRight.X - topLeft.X, Width);
        Top = PalettePlacement.ClampOrigin(Top, topLeft.Y, bottomRight.Y - topLeft.Y, Height);
    }

    private void HideButton_Click(object sender, RoutedEventArgs e) => Hide();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (HotkeyTextBox.IsKeyboardFocusWithin) return;
        if (e.Key == System.Windows.Input.Key.Escape)
        {
            if (HistoryFilterBox.IsDropDownOpen) return;
            if (MainTabs.SelectedIndex == 0 && !string.IsNullOrWhiteSpace(SearchBox.Text)) SearchBox.Clear();
            else Hide();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Enter && MainTabs.SelectedIndex == 0 && (HistoryList.IsKeyboardFocusWithin || SearchBox.IsKeyboardFocusWithin))
        {
            PasteSelectedEntry();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Enter && MainTabs.SelectedIndex == 1 && SnippetList.IsKeyboardFocusWithin)
        {
            PasteSelectedSnippet();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Down && MainTabs.SelectedIndex == 0 && SearchBox.IsKeyboardFocusWithin && HistoryList.Items.Count > 0)
        {
            HistoryList.Focus();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Tab && (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Control) != 0)
        {
            var direction = (System.Windows.Input.Keyboard.Modifiers & System.Windows.Input.ModifierKeys.Shift) != 0 ? -1 : 1;
            var tabCount = _compactPalette ? 2 : MainTabs.Items.Count;
            MainTabs.SelectedIndex = (MainTabs.SelectedIndex + tabCount + direction) % tabCount;
            if (_compactPalette) FocusActiveList();
            else if (MainTabs.ItemContainerGenerator.ContainerFromIndex(MainTabs.SelectedIndex) is TabItem tab) tab.Focus();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.Delete && HistoryList.IsKeyboardFocusWithin)
        {
            DeleteButton_Click(this, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownApproved) return;
        e.Cancel = true;
        if (_exiting) return;
        if (_isolatedQaMode) ExitApplication();
        else Hide();
    }

    private async void ExitApplication()
    {
        if (_exiting) return;
        if (_storageBusy && !_recoveryRequired)
        {
            SetStatus("Дождитесь завершения операции с данными, затем выйдите.");
            return;
        }
        _exiting = true;
        _pasteGeneration++;
        _historyEpoch++;
        _historySaveTimer.Stop();
        _pauseTimer.Stop();
        MainTabs.IsEnabled = false;
        PauseButton.IsEnabled = false;
        UpdateActionState();
        if (!_recoveryRequired && !await SaveHistoryAsync())
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            var exitWithoutSaving = WpfMessageBox.Show(this,
                "Не удалось сохранить последние изменения истории. Они пока доступны в открытой программе.\n\nВыйти без их сохранения? Выберите «Нет», чтобы остаться, исправить доступ к папке данных и повторить выход или создать резервную копию.",
                "NeClipboard Win — история не сохранена", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (exitWithoutSaving != MessageBoxResult.Yes)
            {
                _exiting = false;
                MainTabs.IsEnabled = !_storageBusy;
                PauseButton.IsEnabled = !_storageBusy;
                UpdateCaptureState();
                UpdateActionState();
                SetStatus("Выход отменён. Последние изменения ещё в памяти; исправьте доступ к данным и повторите выход или создайте копию.");
                return;
            }
        }
        if (_windowSource is not null)
        {
            NativeMethods.RemoveClipboardFormatListener(_windowSource.Handle);
            NativeMethods.UnregisterHotKey(_windowSource.Handle, _registeredHotkeyId);
            _windowSource.RemoveHook(WindowMessageHook);
        }
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.ContextMenuStrip?.Dispose();
            _trayIcon.Icon?.Dispose();
            _trayIcon.Dispose();
        }
        _shutdownApproved = true;
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    private void SetStatus(string message)
    {
        StatusTextBlock.Text = message;
        FrameworkElementAutomationPeer.CreatePeerForElement(StatusTextBlock)?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }
}
