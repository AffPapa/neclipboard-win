using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace NeClipboardWin.DesktopVerification;

internal static class Program
{
    [STAThread]
    private static void Main(string[] arguments)
    {
        ApplicationConfiguration.Initialize();
        if (arguments is ["--inspect-native-format-storage"])
        {
            InspectNativeFormatStorage();
            return;
        }
        if (arguments is ["--inspect-clipboard-formats"])
        {
            InspectClipboardFormats();
            return;
        }
        if (arguments is ["--snapshot-format-selftest"])
        {
            Environment.ExitCode = ClipboardSnapshotFormats.SelfTest();
            return;
        }
        if (arguments is ["--inspect-known-format-types"])
        {
            InspectKnownFormatTypes();
            return;
        }
        if (arguments.Length != 1)
        {
            MessageBox.Show("Укажите одноразовую папку NeClipboardWin-QA-* внутри TEMP.", "NeClipboard Win — synthetic fixture");
            return;
        }
        var directory = Path.GetFullPath(arguments[0]);
        var tempRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        if (!directory.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(directory).StartsWith("NeClipboardWin-QA-", StringComparison.Ordinal))
            throw new ArgumentException("The desktop fixture only accepts an isolated TEMP directory.");
        Directory.CreateDirectory(directory);
        Application.Run(new ReceiverWindow(directory));
    }

    private static void InspectClipboardFormats()
    {
        // Read metadata only: never request, copy, hash, persist, or change a clipboard value.
        // JSON escaping prevents custom format names from emitting terminal control characters.
        try
        {
            var formats = Clipboard.GetDataObject()?.GetFormats(autoConvert: false) ?? [];
            foreach (var format in formats.Take(128))
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(format.Length <= 256 ? format : format[..256]));
            if (formats.Length > 128) Console.WriteLine("FORMAT_LIST_TRUNCATED");
        }
        catch (ExternalException)
        {
            Console.Error.WriteLine("Clipboard format metadata is temporarily unavailable; no values were read or changed.");
            Environment.ExitCode = 1;
        }
    }

    private static void InspectKnownFormatTypes()
    {
        // Diagnostic schema is fixed: types and privacy-buffer lengths only, never values.
        string[] known = ["Bitmap", "System.Drawing.Bitmap", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard"];
        try
        {
            var current = Clipboard.GetDataObject();
            var present = current?.GetFormats(autoConvert: false) ?? [];
            foreach (var format in known.Where(format => present.Contains(format, StringComparer.Ordinal)))
            {
                try
                {
                    var value = current!.GetData(format, autoConvert: false);
                    var type = value?.GetType().FullName;
                    long? length = ClipboardSnapshotFormats.IsPrivacyFormat(format) ? value switch { byte[] bytes => bytes.Length, MemoryStream stream => stream.Length, _ => null } : null;
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Format = format, Type = type is { Length: > 256 } ? type[..256] : type, PrivacyBufferLength = length }));
                }
                catch (Exception exception) when (exception is ExternalException or NotSupportedException or InvalidOperationException)
                {
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Format = format, ErrorType = exception.GetType().FullName }));
                }
            }
        }
        catch (ExternalException)
        {
            Console.Error.WriteLine("Clipboard type metadata is temporarily unavailable; no values were logged or changed.");
            Environment.ExitCode = 1;
        }
    }

    private static void InspectNativeFormatStorage()
    {
        if (!OpenClipboard(IntPtr.Zero))
        {
            Console.Error.WriteLine("Clipboard native metadata unavailable; clipboard unchanged.");
            Environment.ExitCode = 1;
            return;
        }
        try
        {
            // Handles are never printed or persisted; no dereference, clone, or clipboard write.
            var bitmapAvailable = GetClipboardData(2) != IntPtr.Zero;
            Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Format = "Bitmap", Materialized = bitmapAvailable }));
            foreach (var format in new[] { "System.Drawing.Bitmap", "CanIncludeInClipboardHistory", "CanUploadToCloudClipboard" })
            {
                var handle = GetClipboardData(RegisterClipboardFormat(format));
                var size = handle == IntPtr.Zero ? 0UL : GlobalSize(handle).ToUInt64();
                Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new { Format = format, Materialized = handle != IntPtr.Zero, GlobalBytes = size <= 16 * 1024 * 1024 ? (ulong?)size : null, Exceeds16MiB = size > 16 * 1024 * 1024 }));
            }
        }
        finally { CloseClipboard(); }
    }

    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool OpenClipboard(IntPtr owner);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseClipboard();
    [DllImport("user32.dll")] private static extern IntPtr GetClipboardData(uint format);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterClipboardFormat(string format);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern UIntPtr GlobalSize(IntPtr handle);
}

internal static class ClipboardSnapshotFormats
{
    private static readonly string[] PrivacyFormats = ["CanIncludeInClipboardHistory", "CanUploadToCloudClipboard", "ExcludeClipboardContentFromMonitorProcessing"];
    private static readonly string[] SupportedFormats = [DataFormats.UnicodeText, DataFormats.Text, DataFormats.OemText, DataFormats.StringFormat,
        DataFormats.Html, DataFormats.Rtf, DataFormats.CommaSeparatedValue, DataFormats.Bitmap,
        DataFormats.Dib, DataFormats.FileDrop, DataFormats.Locale, "PNG", "Preferred DropEffect", "Performed DropEffect", "Paste Succeeded", "NeClipboardWin.SyntheticQa", "System.Drawing.Bitmap"];

    internal static bool IsSupported(string format) => SupportedFormats.Contains(format, StringComparer.Ordinal) || PrivacyFormats.Contains(format, StringComparer.Ordinal);
    internal static bool IsPrivacyFormat(string format) => PrivacyFormats.Contains(format, StringComparer.Ordinal);

    internal static object? ClonePrivacyDword(object? value) => value switch
    {
        int integer => integer,
        byte[] bytes when bytes.Length == 4 => bytes.ToArray(),
        MemoryStream stream when stream.Length == 4 => new MemoryStream(stream.ToArray(), writable: false),
        _ => null
    };

    internal static bool CanPrepareReplay(bool ready, uint ownedSequence, uint currentSequence) => ready && ownedSequence != 0 && ownedSequence == currentSequence;
    internal static bool ReplayObserved(bool prepared, uint preparedSequence, uint currentSequence, bool matches) => prepared && preparedSequence != 0 && currentSequence != 0 && currentSequence != preparedSequence && matches;

    internal static int SelfTest()
    {
        // Pure in-memory regression checks. No access to the system clipboard.
        var passed = IsSupported("System.Drawing.Bitmap") && PrivacyFormats.All(IsSupported)
            && !IsSupported("Unknown.Custom.Ole.Format") && !IsSupported("canincludeinclipboardhistory")
            && !IsPrivacyFormat("System.Drawing.Bitmap")
            && ClonePrivacyDword(0) is int zero && zero == 0
            && ClonePrivacyDword(1) is int one && one == 1
            && ClonePrivacyDword(unchecked((int)0xAABBCCDD)) is int arbitrary && arbitrary == unchecked((int)0xAABBCCDD)
            && ClonePrivacyDword(new byte[3]) is null && ClonePrivacyDword(new byte[5]) is null
            && ClonePrivacyDword("1") is null && ClonePrivacyDword(null) is null;
        byte[] source = [0xDD, 0xCC, 0xBB, 0xAA];
        var clone = ClonePrivacyDword(source) as byte[];
        passed &= clone is not null && !ReferenceEquals(source, clone) && source.SequenceEqual(clone);
        using var stream = new MemoryStream(source, writable: false);
        stream.Position = 2;
        using var streamClone = ClonePrivacyDword(stream) as MemoryStream;
        passed &= streamClone is not null && !ReferenceEquals(stream, streamClone) && streamClone.ToArray().SequenceEqual(source) && stream.Position == 2;
        using var shortStream = new MemoryStream(new byte[3]);
        using var longStream = new MemoryStream(new byte[5]);
        passed &= ClonePrivacyDword(shortStream) is null && ClonePrivacyDword(longStream) is null;
        passed &= CanPrepareReplay(true, 10, 10) && !CanPrepareReplay(false, 10, 10)
            && !CanPrepareReplay(true, 0, 0) && !CanPrepareReplay(true, 10, 11)
            && !ReplayObserved(false, 10, 11, true) && !ReplayObserved(true, 0, 11, true)
            && !ReplayObserved(true, 10, 0, true)
            && !ReplayObserved(true, 10, 10, true) && !ReplayObserved(true, 10, 11, false)
            && ReplayObserved(true, 10, 11, true);
        var consumedGate = true;
        if (ReplayObserved(consumedGate, 10, 11, true)) consumedGate = false;
        passed &= !ReplayObserved(consumedGate, 10, 12, true);
        Console.WriteLine(passed ? "PASS strict clipboard snapshot format regression (no system clipboard access)" : "FAIL strict clipboard snapshot format regression");
        return passed ? 0 : 1;
    }
}

internal sealed class ReceiverWindow : Form
{
    private const string MarkerFormat = "NeClipboardWin.SyntheticQa";
    private const string MarkerValue = "NeClipboardWin-QA";
    private const string SyntheticText = "NeClipboardWin-QA:\r\nПроверка текста, emoji 😀 и переносов.\r\nПоследняя строка: tail-search-token.";
    private const string ReplaySentinel = "NeClipboardWin-QA: replay-sentinel-not-target";
    private readonly string _directory;
    private readonly string[] _files;
    private readonly TextBox _receiver = new() { Multiline = true, AcceptsReturn = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AccessibleName = "Поле-получатель синтетического текста" };
    private readonly Label _status = new() { AutoSize = false, Dock = DockStyle.Fill, Text = "Сначала нажмите «Начать». Существующий буфер сохраняется только в RAM.", AccessibleName = "Результат проверки" };
    private readonly List<Button> _testButtons = [];
    private readonly List<IDisposable> _snapshotResources = [];
    private DataObject? _snapshot;
    private bool _snapshotWasEmpty;
    private bool _ready;
    private uint _snapshotSequence;
    private uint _ownedSequence;
    private bool _hotkeyReserved;
    private bool _replayPrepared;
    private uint _preparedSequence;

    internal ReceiverWindow(string directory)
    {
        _directory = directory;
        var fixtures = Path.Combine(directory, "fixtures");
        Directory.CreateDirectory(fixtures);
        _files = [Path.Combine(fixtures, "файл один.txt"), Path.Combine(fixtures, "file-two.txt")];
        foreach (var file in _files) File.WriteAllText(file, "NeClipboardWin-QA synthetic file. No user content.");
        Text = "NeClipboard Win — Synthetic receiver";
        Width = 900;
        Height = 600;
        MinimumSize = new Size(750, 520);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), RowCount = 5, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 140));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 75));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 45));
        layout.Controls.Add(new Label { Text = "Только синтетические данные. Клики и горячие клавиши выполняются вручную.", Dock = DockStyle.Fill }, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoScroll = true };
        AddButton(actions, "Начать безопасный тест", Begin, false);
        AddButton(actions, "1. Текст → буфер", PublishText);
        AddButton(actions, "2. Изображение → буфер", PublishImage);
        AddButton(actions, "3. Файлы → буфер", PublishFiles);
        AddButton(actions, "Контрольный текст перед вставкой", PrepareReplaySentinel);
        AddButton(actions, "Проверить вставленный текст", VerifyText);
        AddButton(actions, "Проверить PNG в буфере", VerifyImage);
        AddButton(actions, "Проверить файлы в буфере", VerifyFiles);
        AddButton(actions, "Занять/освободить Ctrl+Alt+F9", ToggleHotkey);
        layout.Controls.Add(actions, 0, 1);
        layout.Controls.Add(_receiver, 0, 2);
        layout.Controls.Add(_status, 0, 3);
        var finish = new FlowLayoutPanel { Dock = DockStyle.Fill };
        AddButton(finish, "Вернуть буфер и закрыть", Close, false);
        layout.Controls.Add(finish, 0, 4);
        Controls.Add(layout);
        FormClosing += (_, _) => Finish();
    }

    private void AddButton(FlowLayoutPanel parent, string text, Action action, bool requiresReady = true)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(6), Enabled = !requiresReady, AccessibleName = text };
        button.Click += (_, _) =>
        {
            try { action(); }
            catch (Exception exception) when (exception is ExternalException or IOException or ArgumentException or InvalidOperationException)
            {
                if (!_ready) DisposeSnapshotResources();
                _status.Text = "Операция не выполнена. Буфер занят или недоступен; личные данные не записываются в отчёт.";
                Record("fixture-operation", false);
            }
        };
        if (requiresReady) _testButtons.Add(button);
        parent.Controls.Add(button);
    }

    private void Begin()
    {
        if (_ready) { _status.Text = "Тест уже начат; исходный снимок сохранён только в RAM."; return; }
        var initialSequence = GetClipboardSequenceNumber();
        var current = Clipboard.GetDataObject();
        var formats = current?.GetFormats(autoConvert: false) ?? [];
        if (formats.Any(format => !ClipboardSnapshotFormats.IsSupported(format)))
        {
            _status.Text = "Тест не начат: исходный буфер содержит формат, который нельзя безопасно восстановить. Буфер не изменён.";
            Record("clipboard-snapshot-supported", false);
            return;
        }
        var snapshot = new DataObject();
        long totalBytes = 0;
        foreach (var format in formats)
        {
            var value = current!.GetData(format, autoConvert: false);
            object? copy = ClipboardSnapshotFormats.IsPrivacyFormat(format) ? ClipboardSnapshotFormats.ClonePrivacyDword(value) : value switch
            {
                string text when text.Length <= 8 * 1024 * 1024 => text,
                string[] paths when paths.Length <= 1_000 && paths.Sum(path => (long)path.Length) <= 1_000_000 => paths.ToArray(),
                byte[] bytes when bytes.Length <= 16 * 1024 * 1024 => bytes.ToArray(),
                MemoryStream stream when stream.Length <= 16 * 1024 * 1024 => new MemoryStream(stream.ToArray(), writable: false),
                Bitmap bitmap when (long)bitmap.Width * bitmap.Height <= 8_000_000 => (Bitmap)bitmap.Clone(),
                int integer => integer,
                _ => null
            };
            if (copy is null)
            {
                DisposeSnapshotResources();
                _status.Text = "Тест не начат: исходный буфер невозможно скопировать целиком в допустимом объёме. Буфер не изменён.";
                Record("clipboard-snapshot-complete", false);
                return;
            }
            totalBytes += copy switch { string text => text.Length * 2L, string[] paths => paths.Sum(path => path.Length * 2L), byte[] bytes => bytes.Length, MemoryStream stream => stream.Length, Bitmap bitmap => (long)bitmap.Width * bitmap.Height * 4, _ => 4 };
            if (copy is IDisposable resource) _snapshotResources.Add(resource);
            if (totalBytes > 64 * 1024 * 1024)
            {
                DisposeSnapshotResources();
                _status.Text = "Тест не начат: снимок буфера превышает 64 MiB. Буфер не изменён.";
                Record("clipboard-snapshot-bounded", false);
                return;
            }
            snapshot.SetData(format, autoConvert: false, copy);
        }
        if (GetClipboardSequenceNumber() != initialSequence)
        {
            DisposeSnapshotResources();
            _status.Text = "Тест не начат: содержимое буфера изменилось во время подготовки. Нажмите «Начать» ещё раз.";
            return;
        }
        _snapshot = snapshot;
        _snapshotSequence = initialSequence;
        _snapshotWasEmpty = formats.Length == 0;
        _ready = true;
        foreach (var button in _testButtons) button.Enabled = true;
        _status.Text = "Готово. Выберите тип данных, откройте NeClipboard горячей клавишей и вставьте текст в поле ниже.";
        Record("clipboard-snapshot-ready", true);
    }

    private static DataObject SyntheticObject()
    {
        var data = new DataObject();
        data.SetData(MarkerFormat, autoConvert: false, new MemoryStream(Encoding.UTF8.GetBytes(MarkerValue), writable: false));
        return data;
    }

    private bool SetSyntheticClipboard(DataObject data, string name)
    {
        if (GetClipboardSequenceNumber() != (_ownedSequence == 0 ? _snapshotSequence : _ownedSequence))
        {
            _status.Text = "Буфер изменился. Сначала проверьте результат вставки; стороннее новое содержимое не перезаписывается.";
            Record("publish-refused-newer-clipboard", false);
            return false;
        }
        Clipboard.SetDataObject(data, copy: true);
        _replayPrepared = false;
        _preparedSequence = 0;
        _ownedSequence = GetClipboardSequenceNumber();
        Record(name, true);
        _status.Text = "Синтетический буфер готов. Откройте NeClipboard, найдите запись и выберите её.";
        _receiver.Focus();
        return true;
    }

    private void PublishText()
    {
        _receiver.Clear();
        var data = SyntheticObject();
        data.SetData(DataFormats.UnicodeText, SyntheticText);
        SetSyntheticClipboard(data, "publish-synthetic-text");
    }

    private void PublishImage()
    {
        using var bitmap = CreateSyntheticBitmap();
        var data = SyntheticObject();
        data.SetData(DataFormats.Bitmap, bitmap);
        SetSyntheticClipboard(data, "publish-synthetic-image");
    }

    private void PublishFiles()
    {
        var data = SyntheticObject();
        data.SetData(DataFormats.FileDrop, _files.ToArray());
        SetSyntheticClipboard(data, "publish-synthetic-files");
    }

    private void PrepareReplaySentinel()
    {
        _replayPrepared = false;
        _preparedSequence = 0;
        if (!ClipboardSnapshotFormats.CanPrepareReplay(_ready, _ownedSequence, GetClipboardSequenceNumber()))
        {
            _status.Text = "Буфер уже изменился. Новое стороннее содержимое не перезаписывается.";
            return;
        }
        var sentinel = SyntheticObject();
        sentinel.SetData(DataFormats.UnicodeText, ReplaySentinel);
        if (!SetSyntheticClipboard(sentinel, "publish-replay-sentinel")) return;
        var sequence = _ownedSequence;
        var matches = GetClipboardSequenceNumber() == sequence && Clipboard.ContainsText(TextDataFormat.UnicodeText)
            && Clipboard.GetText(TextDataFormat.UnicodeText) == ReplaySentinel
            && !Clipboard.ContainsImage() && !Clipboard.ContainsFileDropList()
            && GetClipboardSequenceNumber() == sequence;
        if (!matches)
        {
            _status.Text = "Контрольный текст не подтверждён; проверка вставки не подготовлена.";
            Record("replay-sentinel-confirmed", false);
            return;
        }
        _preparedSequence = sequence;
        _replayPrepared = true;
        _receiver.Clear();
        Record("replay-sentinel-confirmed", true);
        _status.Text = "В буфере другой контрольный текст. Выберите прежнюю запись в NeClipboard и проверьте результат.";
        _receiver.Focus();
    }

    private void VerifyText()
    {
        var sequence = GetClipboardSequenceNumber();
        var clipboardMatches = Clipboard.ContainsText(TextDataFormat.UnicodeText) && Clipboard.GetText(TextDataFormat.UnicodeText) == SyntheticText;
        var passed = ClipboardSnapshotFormats.ReplayObserved(_replayPrepared, _preparedSequence, sequence,
            clipboardMatches && _receiver.Text.ReplaceLineEndings("\n") == SyntheticText.ReplaceLineEndings("\n")) && GetClipboardSequenceNumber() == sequence;
        Record("receiver-text-equals-synthetic", passed);
        if (clipboardMatches && GetClipboardSequenceNumber() == sequence) _ownedSequence = sequence;
        if (passed) _replayPrepared = false;
        _status.Text = passed ? "PASS: вставленный текст совпал целиком, включая Unicode и переносы." : "FAIL: поле-получатель не совпало с синтетическим текстом.";
    }

    private void VerifyImage()
    {
        var sequence = GetClipboardSequenceNumber();
        using var image = Clipboard.ContainsImage() ? Clipboard.GetImage() : null;
        using var actual = image is null ? null : new Bitmap(image);
        using var expected = CreateSyntheticBitmap();
        var passed = actual is { Width: 2, Height: 2 };
        if (passed)
            for (var y = 0; y < 2; y++) for (var x = 0; x < 2; x++) passed &= actual!.GetPixel(x, y).ToArgb() == expected.GetPixel(x, y).ToArgb();
        var clipboardMatches = passed;
        passed = ClipboardSnapshotFormats.ReplayObserved(_replayPrepared, _preparedSequence, sequence, clipboardMatches) && GetClipboardSequenceNumber() == sequence;
        Record("clipboard-image-equals-synthetic", passed);
        if (clipboardMatches && GetClipboardSequenceNumber() == sequence) _ownedSequence = sequence;
        if (passed) _replayPrepared = false;
        _status.Text = passed ? "PASS: изображение из буфера совпало по всем четырём пикселям." : "FAIL: изображение не совпало с синтетическим образцом.";
    }

    private void VerifyFiles()
    {
        var sequence = GetClipboardSequenceNumber();
        var clipboardMatches = Clipboard.ContainsFileDropList() && Clipboard.GetFileDropList().Cast<string>().SequenceEqual(_files, StringComparer.Ordinal);
        var passed = ClipboardSnapshotFormats.ReplayObserved(_replayPrepared, _preparedSequence, sequence, clipboardMatches) && GetClipboardSequenceNumber() == sequence;
        Record("clipboard-files-equal-synthetic", passed);
        if (clipboardMatches && GetClipboardSequenceNumber() == sequence) _ownedSequence = sequence;
        if (passed) _replayPrepared = false;
        _status.Text = passed ? "PASS: список обоих синтетических путей совпал полностью." : "FAIL: список путей не совпал с синтетическим образцом.";
    }

    private static Bitmap CreateSyntheticBitmap()
    {
        var bitmap = new Bitmap(2, 2);
        bitmap.SetPixel(0, 0, Color.Red);
        bitmap.SetPixel(1, 0, Color.Lime);
        bitmap.SetPixel(0, 1, Color.Blue);
        bitmap.SetPixel(1, 1, Color.White);
        return bitmap;
    }

    private void ToggleHotkey()
    {
        if (_hotkeyReserved) { UnregisterHotKey(Handle, 701); _hotkeyReserved = false; }
        else _hotkeyReserved = RegisterHotKey(Handle, 701, 0x4003, 0x78);
        _status.Text = _hotkeyReserved ? "Ctrl + Alt + F9 занят fixture. Проверьте отказ регистрации в NeClipboard." : "Ctrl + Alt + F9 освобождён (либо уже был занят другим процессом).";
        Record("hotkey-fixture-reserved", _hotkeyReserved);
    }

    private void Finish()
    {
        if (_hotkeyReserved) UnregisterHotKey(Handle, 701);
        try
        {
            if (_ready && _ownedSequence != 0 && GetClipboardSequenceNumber() == _ownedSequence)
            {
                if (_snapshotWasEmpty) Clipboard.Clear();
                else Clipboard.SetDataObject(_snapshot!, copy: true);
                Record("original-clipboard-restored", true);
            }
            else if (_ownedSequence != 0) Record("restore-skipped-newer-clipboard", true);
        }
        catch (ExternalException) { Record("original-clipboard-restored", false); }
        finally { DisposeSnapshotResources(); }
    }

    private void DisposeSnapshotResources()
    {
        foreach (var resource in _snapshotResources) resource.Dispose();
        _snapshotResources.Clear();
    }

    private void Record(string test, bool passed)
    {
        // Only fixed test labels and boolean results. Never persist the original clipboard or its hash.
        try { File.AppendAllText(Path.Combine(_directory, "desktop-fixture-results.log"), $"{DateTime.UtcNow:O} {(passed ? "PASS" : "FAIL")} {test}{Environment.NewLine}"); }
        catch (IOException) { _status.Text = "Журнал теста недоступен; результат не записан."; }
        catch (UnauthorizedAccessException) { _status.Text = "Журнал теста недоступен; результат не записан."; }
    }

    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool RegisterHotKey(IntPtr handle, int id, uint modifiers, uint key);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnregisterHotKey(IntPtr handle, int id);
}
