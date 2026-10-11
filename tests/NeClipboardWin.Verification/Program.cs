using System.Text;
using System.Security.Cryptography;
using System.Text.Json;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using NeClipboardWin;

if (args.Length == 2 && args[0] == "--inspect-desktop-qa")
{
    var directory = Path.GetFullPath(args[1]);
    var temporaryRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
    if (!directory.StartsWith(temporaryRoot, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(directory).StartsWith("NeClipboardWin-QA-", StringComparison.Ordinal))
        throw new ArgumentException("Inspection only accepts an isolated NeClipboardWin-QA-* TEMP directory.");
    var entries = new EncryptedHistoryStore(directory).Load();
    var settings = new SettingsService(directory).Load();
    Console.WriteLine($"Synthetic desktop QA history: text={entries.Count(entry => entry.Kind == ClipboardEntryKind.Text)}, image={entries.Count(entry => entry.Kind == ClipboardEntryKind.Image)}, files={entries.Count(entry => entry.Kind == ClipboardEntryKind.Files)}; autoPaste={settings.AutoPasteAfterSelection}; hotkey={settings.HotkeyModifiers}/{settings.HotkeyVirtualKey}");
    Console.WriteLine($"All text synthetic={entries.Where(entry => entry.Kind == ClipboardEntryKind.Text).All(entry => entry.Payload.StartsWith("NeClipboardWin-QA:", StringComparison.Ordinal))}; all sources fixture={entries.All(entry => entry.SourceApplication == "NeClipboardWin.DesktopVerification.exe")}");
    return;
}

if (args.Length == 3 && args[0] == "--crash-restore")
{
    var service = new SettingsService(args[1]);
    StorageSafety.FaultInjection = point => { if (point == args[2]) Environment.Exit(73); };
    service.TryRestoreWithHistory(new EncryptedHistoryStore(args[1]), new AppSettings { MaxHistoryItems = 33 }, [new ClipboardEntry { Payload = "new state" }]);
    Environment.Exit(74);
}

if (args.Contains("--benchmark-models", StringComparer.Ordinal))
{
    BenchmarkModels();
    return;
}

var testRoot = Path.Combine(Path.GetTempPath(), "NeClipboardWin.Verification", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);

try
{
    Run("model previews", TestModelPreviews);
    Run("invalid model rejection", () => TestInvalidModelRejection(testRoot));
    Run("encrypted settings round-trip", () => TestSettingsRoundTrip(testRoot));
    Run("legacy settings migration", () => TestLegacySettingsMigration(testRoot));
    Run("encrypted history round-trip", () => TestHistoryRoundTrip(testRoot));
    Run("corrupt history recovery", () => TestCorruptHistoryRecovery(testRoot));
    Run("corrupt settings quarantine", () => TestCorruptSettingsQuarantine(testRoot));
    Run("corrupt history quarantine", () => TestCorruptHistoryQuarantine(testRoot));
    Run("previous history recovery", () => TestPreviousHistoryRecovery(testRoot));
    Run("previous settings recovery", () => TestPreviousSettingsRecovery(testRoot));
    Run("bounded history load", () => TestBoundedHistoryLoad(testRoot));
    Run("encrypted backup round-trip", () => TestBackupRoundTrip(testRoot));
    Run("backup content bounds", () => TestBackupContentBounds(testRoot));
    Run("corrupt backup rejection", () => TestCorruptBackupRejection(testRoot));
    Run("preview cache and complete search", TestPreviewCacheAndSearch);
    Run("deep snapshots", TestDeepSnapshots);
    Run("load rejects invalid encrypted documents", () => TestInvalidEncryptedDocuments(testRoot));
    Run("ordinary history and settings limits", () => TestStorageLimits(testRoot));
    Run("oversized file rejected before allocation", () => TestOversizedFile(testRoot));
    Run("image validation and pixel bounds", () => TestImages(testRoot));
    Run("atomic save write failure", () => TestWriteFailure(testRoot));
    Run("restore transaction rollback", () => TestRestoreRollback(testRoot));
    Run("restore recovery blocks unsafe writes", () => TestRestoreRecoveryRequired(testRoot));
    Run("restore abrupt process termination", () => TestRestoreProcessCrash(testRoot));
    Run("clear never resurrects previous history", () => TestClear(testRoot));
    Run("native INPUT structure layout", TestNativeInputLayout);
    Run("palette placement remains inside work area", TestPalettePlacement);
    Run("legacy encrypted upgrade and repeat load", () => TestEncryptedLegacyUpgrade(testRoot));
    Run("JSON depth and preallocation string bounds", TestJsonPreflight);
    Run("invalid recovery journal preserves data", () => TestInvalidRecoveryJournal(testRoot));
    Run("clipboard privacy markers and replay policy", TestClipboardPrivacy);
    Run("locked storage fails closed without losing data", () => TestLockedStorage(testRoot));
    Run("theme text contrast contracts", TestThemeTextContrast);
    Run("snippet actionable error accessibility", TestSnippetErrorAccessibility);
    Console.WriteLine("All NeClipboard Win verification checks passed.");
}
finally
{
    if (Directory.Exists(testRoot)) Directory.Delete(testRoot, true);
}

static void Run(string name, Action check)
{
    check();
    Console.WriteLine($"PASS  {name}");
}

static void TestSnippetErrorAccessibility()
{
    // In-process WPF properties only: no Show, window handle, input or clipboard.
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var editor = new SnippetEditorWindow(new Snippet
            {
                Title = "Synthetic title", Content = "Synthetic Unicode 😀\r\nSecond line"
            });
            try
            {
                var error = (System.Windows.Controls.TextBlock)typeof(SnippetEditorWindow)
                    .GetField("_errorText", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(editor)!;
                var showError = typeof(SnippetEditorWindow)
                    .GetMethod("ShowError", BindingFlags.Instance | BindingFlags.NonPublic)!;
                foreach (var message in new[] { "Укажите название заготовки.", "Добавьте текст заготовки." })
                {
                    showError.Invoke(editor, [message]);
                    Expect(error.Text == message && error.Visibility == System.Windows.Visibility.Visible,
                        "The actionable error must remain visible.");
                    Expect(System.Windows.Automation.AutomationProperties.GetName(error) == message,
                        "UIA must expose the actual error, not a generic label.");
                    var peer = System.Windows.Automation.Peers.FrameworkElementAutomationPeer.CreatePeerForElement(error);
                    Expect(peer?.GetName() == message, "The automation peer must expose each updated error.");
                    Expect(System.Windows.Automation.AutomationProperties.GetLiveSetting(error)
                        == System.Windows.Automation.AutomationLiveSetting.Assertive,
                        "Important validation messages must remain an assertive live region.");
                }
                Expect(editor.Result.Title == "Synthetic title"
                    && editor.Result.Content == "Synthetic Unicode 😀\r\nSecond line",
                    "Displaying errors must not lose entered data.");
            }
            finally { editor.Close(); }
        }
        catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new InvalidOperationException("Snippet error accessibility regression failed.", failure);
}

static void TestThemeTextContrast()
{
    // Static palette validation: no Window, clipboard, registry changes or UI input.
    // Rendering, disabled alpha and the user's high-contrast colors need separate QA.
    var palettes = new[] { "LightPalette", "DarkPalette" }.Select(name =>
        (Name: name, Colors: (IReadOnlyDictionary<string, string>)typeof(ThemeManager)
            .GetField(name, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!)).ToArray();
    Expect(palettes[0].Colors.Keys.Order().SequenceEqual(palettes[1].Colors.Keys.Order()), "Light/dark palette keys must match.");
    foreach (var (name, colors) in palettes)
    {
        var pairs = new List<(string Foreground, string Background)>();
        foreach (var foreground in new[] { "TextBrush", "MutedBrush", "SubtleTextBrush" })
            foreach (var background in new[] { "AppBackgroundBrush", "SurfaceBrush", "SurfaceRaisedBrush", "SurfaceMutedBrush", "HoverBrush", "SelectionBrush" })
                pairs.Add((foreground, background));
        pairs.AddRange([("AccentTextBrush", "AccentBrush"), ("AccentBrush", "AccentSoftBrush"),
            ("TextBrush", "PressedBrush"), ("DangerBrush", "SurfaceRaisedBrush"),
            ("PinnedBrush", "SelectionBrush"), ("PinnedBrush", "HoverBrush")]);
        foreach (var (foreground, background) in pairs)
        {
            var first = Luminance(colors[foreground]);
            var second = Luminance(colors[background]);
            var contrast = (Math.Max(first, second) + .05) / (Math.Min(first, second) + .05);
            Expect(contrast >= 4.5, $"{name}: {foreground}/{background} contrast {contrast:F4} must be >=4.5 without rounding.");
        }
    }
    static double Luminance(string color)
    {
        Expect(color.Length == 7 && color[0] == '#', "Contrast fixture expects opaque sRGB hex colors.");
        static double Linear(byte value)
        {
            var channel = value / 255d;
            return channel <= .04045 ? channel / 12.92 : Math.Pow((channel + .055) / 1.055, 2.4);
        }
        return .2126 * Linear(Convert.ToByte(color.Substring(1, 2), 16))
            + .7152 * Linear(Convert.ToByte(color.Substring(3, 2), 16))
            + .0722 * Linear(Convert.ToByte(color.Substring(5, 2), 16));
    }
}

static void TestModelPreviews()
{
    var text = new ClipboardEntry { Kind = ClipboardEntryKind.Text, Payload = "line one\r\nline two" };
    Expect(text.Preview == "line one line two", "Text preview must flatten new lines.");
    var files = new ClipboardEntry { Kind = ClipboardEntryKind.Files, Payload = "C:\\Temp\\one.txt\nC:\\Temp\\two.txt" };
    Expect(files.Preview == "one.txt и ещё 1", "File preview must describe the first file and count.");
    Expect(text.AccessibleSummary.Contains("Текст:"), "Accessible summary must identify the item type.");
}

static void TestSettingsRoundTrip(string root)
{
    var directory = Path.Combine(root, "settings");
    var service = new SettingsService(directory);
    var settings = new AppSettings
    {
        CaptureEnabled = false,
        MaxHistoryItems = 77,
        LaunchAtSignIn = true,
        AutoPasteAfterSelection = true,
        ExcludedApplications = ["secret.exe"],
        Snippets = [new Snippet { Title = "Reply", Content = "Private snippet" }]
    };
    Expect(service.TrySave(settings), "Settings must save.");
    var raw = File.ReadAllText(Path.Combine(directory, "settings.dat"), Encoding.UTF8);
    Expect(!raw.Contains("Private snippet", StringComparison.Ordinal), "Settings must not store snippets as plaintext.");
    var loaded = service.Load();
    Expect(loaded.AutoPasteAfterSelection && loaded.Snippets.Single().Content == "Private snippet" && loaded.ExcludedApplications.Single() == "secret.exe", "Settings must round-trip.");
}

static void TestInvalidModelRejection(string root)
{
    var store = new EncryptedHistoryStore(Path.Combine(root, "invalid-models"));
    Expect(!store.TrySave([new ClipboardEntry { Kind = (ClipboardEntryKind)99, Payload = "invalid" }]), "Unknown entry kind must be rejected.");
    Expect(!store.TrySave([new ClipboardEntry { Payload = null! }]), "Null entry payload must be rejected.");
    Expect(!new SettingsService(Path.Combine(root, "invalid-settings-model")).TrySave(new AppSettings { Snippets = null! }), "Null snippet list must be rejected.");
}

static void TestHistoryRoundTrip(string root)
{
    var directory = Path.Combine(root, "history");
    var store = new EncryptedHistoryStore(directory);
    var entries = new[]
    {
        new ClipboardEntry { Kind = ClipboardEntryKind.Text, Payload = "Sensitive clipboard value", ContentHash = "A" },
        new ClipboardEntry { Kind = ClipboardEntryKind.Files, Payload = "C:\\Temp\\document.pdf", ContentHash = "B", IsPinned = true }
    };
    Expect(store.TrySave(entries), "History must save.");
    var raw = File.ReadAllText(Path.Combine(directory, "history.dat"), Encoding.UTF8);
    Expect(!raw.Contains("Sensitive clipboard value", StringComparison.Ordinal), "History must be DPAPI-encrypted at rest.");
    var loaded = store.Load();
    Expect(loaded.Count == 2 && loaded[0].Payload == "Sensitive clipboard value" && loaded[1].IsPinned, "History must round-trip.");
}

static void TestLegacySettingsMigration(string root)
{
    var directory = Path.Combine(root, "legacy-settings");
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "settings.json"), "{\"CaptureEnabled\":false,\"Snippets\":[{\"Title\":\"Legacy\",\"Content\":\"Protected after migration\"}]}");
    var settings = new SettingsService(directory).Load();
    Expect(settings.CaptureEnabled == false && settings.Snippets.Single().Content == "Protected after migration", "Legacy settings must load during migration.");
    Expect(File.Exists(Path.Combine(directory, "settings.dat")) && !File.Exists(Path.Combine(directory, "settings.json")), "Legacy settings must migrate to encrypted storage and remove plaintext.");
}

static void TestCorruptHistoryRecovery(string root)
{
    var directory = Path.Combine(root, "corrupt-history");
    Directory.CreateDirectory(directory);
    File.WriteAllText(Path.Combine(directory, "history.dat"), "not a DPAPI payload");
    var store = new EncryptedHistoryStore(directory);
    Expect(store.Load().Count == 0, "Corrupt encrypted history must recover to an empty in-memory list.");
}

static void TestCorruptSettingsQuarantine(string root)
{
    var directory = Path.Combine(root, "corrupt-settings");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "settings.dat");
    File.WriteAllText(path, "broken DPAPI settings");
    var service = new SettingsService(directory);
    Expect(service.Load().Snippets.Count == 0 && service.LastLoadRecovered, "Corrupt settings must recover without throwing.");
    Expect(!File.Exists(path) && Directory.GetFiles(directory, "settings.dat.corrupt-*").Length == 1, "Corrupt settings must be quarantined before future saves.");
}

static void TestCorruptHistoryQuarantine(string root)
{
    var directory = Path.Combine(root, "corrupt-history-quarantine");
    Directory.CreateDirectory(directory);
    var path = Path.Combine(directory, "history.dat");
    File.WriteAllText(path, "broken DPAPI history");
    var store = new EncryptedHistoryStore(directory);
    Expect(store.Load().Count == 0 && store.LastLoadRecovered, "Corrupt history must recover without throwing.");
    Expect(!File.Exists(path) && Directory.GetFiles(directory, "history.dat.corrupt-*").Length == 1, "Corrupt history must be quarantined before future saves.");
}

static void TestPreviousHistoryRecovery(string root)
{
    var directory = Path.Combine(root, "previous-history");
    var store = new EncryptedHistoryStore(directory);
    Expect(store.TrySave([new ClipboardEntry { Kind = ClipboardEntryKind.Text, Payload = "version one", ContentHash = "one" }]), "Initial history must save.");
    Expect(store.TrySave([new ClipboardEntry { Kind = ClipboardEntryKind.Text, Payload = "version two", ContentHash = "two" }]), "Updated history must save.");
    File.WriteAllText(Path.Combine(directory, "history.dat"), "broken current version");
    var recoveredStore = new EncryptedHistoryStore(directory);
    var recovered = recoveredStore.Load();
    Expect(recoveredStore.LastLoadRecovered && recoveredStore.RecoveredFromPreviousVersion, "Corrupt current history must use the previous encrypted version.");
    Expect(recovered.Single().Payload == "version one", "Previous history version must remain readable.");
}

static void TestPreviousSettingsRecovery(string root)
{
    var directory = Path.Combine(root, "previous-settings");
    var service = new SettingsService(directory);
    Expect(service.TrySave(new AppSettings { Snippets = [new Snippet { Title = "First", Content = "version one" }] }), "Initial settings must save.");
    Expect(service.TrySave(new AppSettings { Snippets = [new Snippet { Title = "Second", Content = "version two" }] }), "Updated settings must save.");
    File.WriteAllText(Path.Combine(directory, "settings.dat"), "broken current settings");
    var recoveredService = new SettingsService(directory);
    var recovered = recoveredService.Load();
    Expect(recoveredService.LastLoadRecovered && recoveredService.RecoveredFromPreviousVersion, "Corrupt current settings must use the previous encrypted version.");
    Expect(recovered.Snippets.Single().Content == "version one", "Previous settings version must remain readable.");
}

static void TestBoundedHistoryLoad(string root)
{
    var directory = Path.Combine(root, "bounded-history");
    var entries = Enumerable.Range(0, 150)
        .Select(index => new ClipboardEntry
        {
            Kind = ClipboardEntryKind.Text,
            Payload = new string((char)('a' + index % 26), 100 * 1024),
            ContentHash = index.ToString()
        })
        .ToArray();
    var store = new EncryptedHistoryStore(directory);
    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
    Expect(store.TrySave(entries), "Bounded history must save.");
    var loaded = store.Load();
    stopwatch.Stop();
    Expect(loaded.Count == entries.Length && loaded.Sum(entry => Encoding.UTF8.GetByteCount(entry.Payload)) <= 15 * 1024 * 1024, "Bounded history must round-trip within the 15 MiB product cap.");
    Console.WriteLine($"INFO  bounded history save/load: {stopwatch.ElapsedMilliseconds} ms");
}

static void TestBackupRoundTrip(string root)
{
    var path = Path.Combine(root, "backup.neclipboard-backup");
    var settings = new AppSettings { Snippets = [new Snippet { Title = "Backup", Content = "Private backup snippet" }] };
    var entries = new[] { new ClipboardEntry { Kind = ClipboardEntryKind.Text, Payload = "Private backup history", ContentHash = "backup" } };
    var backup = new BackupService();
    Expect(backup.TryCreate(path, settings, entries), "Backup must save.");
    var raw = File.ReadAllText(path, Encoding.UTF8);
    Expect(!raw.Contains("Private backup", StringComparison.Ordinal), "Backup must not be plaintext.");
    Expect(backup.TryRead(path, out var restoredSettings, out var restoredEntries), "Backup must load for the same Windows user.");
    Expect(restoredSettings.Snippets.Single().Content == "Private backup snippet" && restoredEntries.Single().Payload == "Private backup history", "Backup must restore settings and history.");
}

static void TestCorruptBackupRejection(string root)
{
    var path = Path.Combine(root, "broken.neclipboard-backup");
    File.WriteAllText(path, "not a backup");
    var backup = new BackupService();
    Expect(!backup.TryRead(path, out _, out _), "Corrupt backup must be rejected without throwing.");
}

static void TestBackupContentBounds(string root)
{
    var backup = new BackupService();
    var invalidSettingsPath = Path.Combine(root, "invalid-settings.neclipboard-backup");
    Expect(!backup.TryCreate(invalidSettingsPath, new AppSettings { MaxHistoryItems = 1 }, []), "Backup must reject settings outside the supported range.");

    var oversizedPayloadPath = Path.Combine(root, "oversized-payload.neclipboard-backup");
    var oversizedEntry = new ClipboardEntry { Kind = ClipboardEntryKind.Text, Payload = new string('x', 500_001), ContentHash = "oversized" };
    Expect(!backup.TryCreate(oversizedPayloadPath, new AppSettings(), [oversizedEntry]), "Backup must reject oversized payloads before writing.");
}

static void Expect(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void BenchmarkModels()
{
    var entries = Enumerable.Range(0, 150).Select(index => new ClipboardEntry
    {
        Payload = string.Concat(Enumerable.Repeat("synthetic line for preview\r\n", 3_500)) + "tail-needle"
    }).ToArray();
    long result = 0;
    foreach (var entry in entries) result += entry.Preview.Length;
    var samples = new List<double>();
    var before = GC.GetAllocatedBytesForCurrentThread();
    for (var iteration = 0; iteration < 30; iteration++)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (var entry in entries)
        {
            result += entry.Preview.Length;
            if (entry.MatchesSearch("tail-needle")) result++;
            if (entry.MatchesSearch("not-present")) result++;
        }
        samples.Add(watch.Elapsed.TotalMilliseconds);
    }
    var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
    samples.Sort();
    Console.WriteLine($"MODEL BENCHMARK samples=30 entries={entries.Length} chars={entries.Sum(entry => entry.Payload.Length)} p50={samples[14]:F2}ms p95={samples[28]:F2}ms allocated={allocated}bytes checksum={result}");
}

static void TestPreviewCacheAndSearch()
{
    var entry = new ClipboardEntry { Payload = new string('a', 499_980) + "unique-needle", SourceApplication = "Example.exe" };
    Expect(entry.Preview.Length <= ClipboardEntry.PreviewCharacterLimit + 1, "Preview must have a fixed display bound.");
    Expect(ReferenceEquals(entry.Preview, entry.Preview), "Repeated bindings must reuse the preview.");
    Expect(!entry.Preview.Contains("unique-needle") && entry.MatchesSearch("UNIQUE-NEEDLE"), "Search must find content beyond the display preview.");
    Expect(entry.MatchesSearch("example.exe") && !entry.MatchesSearch("absent-needle"), "Source search and missing queries must work.");
    var files = new ClipboardEntry { Kind = ClipboardEntryKind.Files, Payload = "C:\\first.txt\nC:\\nested\\second-only.doc" };
    Expect(files.MatchesSearch("second-only"), "Search must include every stored file path.");
    var emoji = new ClipboardEntry { Payload = new string('x', 199) + "😀tail" };
    Expect(!char.IsHighSurrogate(emoji.Preview[^2]), "Preview truncation must not split a surrogate pair.");
}

static void TestDeepSnapshots()
{
    var original = new AppSettings { CapturePausedUntilUtc = DateTime.UtcNow.AddMinutes(15), Snippets = [new Snippet { Title = "original", Content = "before" }] };
    var copy = original.DeepClone();
    original.Snippets[0].Content = "after";
    original.ExcludedApplications.Clear();
    original.HotkeyVirtualKey = 0x41;
    Expect(copy.Snippets[0].Content == "before" && copy.ExcludedApplications.Count > 0 && copy.HotkeyVirtualKey == 0x56, "Settings snapshots must not share mutable nested objects.");
    var entry = new ClipboardEntry { Payload = "immutable payload" };
    var cloned = entry.DeepClone();
    entry.IsPinned = true;
    entry.LastUsedUtc = DateTime.UtcNow.AddHours(1);
    Expect(!cloned.IsPinned && cloned.LastUsedUtc != entry.LastUsedUtc && cloned.Id == entry.Id, "Entry snapshot must isolate mutable state and preserve identity.");
}

static void WriteEncrypted(string path, object document)
{
    File.WriteAllBytes(path, ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(document), null, DataProtectionScope.CurrentUser));
}

static void TestInvalidEncryptedDocuments(string root)
{
    var directory = Path.Combine(root, "invalid-encrypted");
    Directory.CreateDirectory(directory);
    var invalidDocuments = new object?[]
    {
        null,
        new object?[] { null },
        new[] { new ClipboardEntry { Kind = (ClipboardEntryKind)999, Payload = "bad kind" } },
        new[] { new ClipboardEntry { Payload = null! } },
        new { Format = "NeClipboardWin.History", SchemaVersion = 999, Entries = Array.Empty<ClipboardEntry>() },
        new { Format = "OtherProduct.History", SchemaVersion = 1, Entries = Array.Empty<ClipboardEntry>() },
        new[] { new ClipboardEntry { SourceApplication = new string('x', 261) } }
    };
    foreach (var document in invalidDocuments)
    {
        WriteEncrypted(Path.Combine(directory, "history.dat"), document!);
        var store = new EncryptedHistoryStore(directory);
        Expect(store.Load().Count == 0 && store.LastLoadRecovered, "Invalid ordinary history must be quarantined, not returned to UI.");
    }
    WriteEncrypted(Path.Combine(directory, "settings.dat"), new AppSettings { Snippets = null! });
    var settings = new SettingsService(directory);
    Expect(settings.Load().Snippets is not null && settings.LastLoadRecovered, "Invalid ordinary settings must recover safely.");
    var futureBackup = Path.Combine(root, "future.neclipboard-backup");
    WriteEncrypted(futureBackup, new { Format = "NeClipboardWin.Backup", SchemaVersion = 2, Settings = new AppSettings(), Entries = Array.Empty<ClipboardEntry>() });
    Expect(!new BackupService().TryRead(futureBackup, out _, out _), "Future backup schemas must be refused before applying data.");
}

static void TestStorageLimits(string root)
{
    var directory = Path.Combine(root, "real-limits");
    var store = new EncryptedHistoryStore(directory);
    var tooMany = Enumerable.Range(0, 1_001).Select(index => new ClipboardEntry { Payload = index.ToString() }).ToArray();
    Expect(!store.TrySave(tooMany), "Ordinary save must refuse more than 1000 entries.");
    Expect(!new BackupService().TryCreate(Path.Combine(root, "too-many.backup"), new AppSettings(), tooMany), "Backup must refuse rather than silently truncate extra entries.");
    WriteEncrypted(Path.Combine(directory, "history.dat"), tooMany);
    Expect(store.Load().Count == 0 && store.LastLoadRecovered, "Ordinary load must refuse too many entries.");
    var tooLarge = Enumerable.Range(0, 40).Select(_ => new ClipboardEntry { Payload = new string('x', 400_000) }).ToArray();
    Expect(!store.TrySave(tooLarge), "Total payload above 15MiB must be refused.");
    WriteEncrypted(Path.Combine(directory, "history.dat"), tooLarge);
    Expect(store.Load().Count == 0 && store.LastLoadRecovered, "The same total limit must apply on read.");
    var service = new SettingsService(Path.Combine(root, "settings-limits"));
    Expect(!service.TrySave(new AppSettings { HotkeyModifiers = 4 }), "Shift-only global hotkey must be refused.");
    Expect(!service.TrySave(new AppSettings { HotkeyVirtualKey = 0x1B }), "Escape must not be registered as an allowed hotkey.");
    Expect(service.TrySave(new AppSettings { HotkeyVirtualKey = 0x7A }), "F11 is the highest supported function hotkey.");
    Expect(!service.TrySave(new AppSettings { HotkeyVirtualKey = 0x7B }), "F12 is always reserved by Windows for debuggers.");
    Expect(service.Load().HotkeyVirtualKey == 0x7A, "Rejecting F12 must preserve the previously saved hotkey.");
    Expect(!service.TrySave(new AppSettings { Snippets = Enumerable.Range(0, 5).Select(_ => new Snippet { Content = new string('x', 500_000) }).ToList() }), "Aggregate snippet content must be bounded.");
    Expect(!service.TrySave(new AppSettings { ExcludedApplications = Enumerable.Repeat("a.exe", 101).ToList() }), "Exclusion count must be bounded.");
    var duplicate = new ClipboardEntry { Payload = "same id" };
    Expect(!store.TrySave([duplicate, duplicate.DeepClone()]), "Duplicate IDs must not enter the store.");
}

static void TestLockedStorage(string root)
{
    var historyDirectory = Path.Combine(root, "locked-history");
    var history = new EncryptedHistoryStore(historyDirectory);
    Expect(history.TrySave([new ClipboardEntry { Payload = "retained synthetic history" }]), "Prepare locked-history fixture.");
    AssertLockedLoadRefused(Path.Combine(historyDirectory, "history.dat"), () => history.Load());
    Expect(history.Load().Single().Payload == "retained synthetic history", "History must remain readable after the lock is released.");

    var settingsDirectory = Path.Combine(root, "locked-settings");
    var settings = new SettingsService(settingsDirectory);
    Expect(settings.TrySave(new AppSettings { MaxHistoryItems = 123 }), "Prepare locked-settings fixture.");
    AssertLockedLoadRefused(Path.Combine(settingsDirectory, "settings.dat"), () => settings.Load());
    Expect(settings.Load().MaxHistoryItems == 123, "Settings must remain unchanged after lock release.");

    File.Move(Path.Combine(historyDirectory, "history.dat"), Path.Combine(historyDirectory, "history.prev.dat"));
    AssertLockedLoadRefused(Path.Combine(historyDirectory, "history.prev.dat"), () => history.Load());
    Expect(history.Load().Single().Payload == "retained synthetic history", "A locked previous history must never become an empty default.");

    File.Move(Path.Combine(settingsDirectory, "settings.dat"), Path.Combine(settingsDirectory, "settings.prev.dat"));
    AssertLockedLoadRefused(Path.Combine(settingsDirectory, "settings.prev.dat"), () => settings.Load());
    Expect(settings.Load().MaxHistoryItems == 123, "A locked previous settings file must never become defaults.");

    var legacyDirectory = Path.Combine(root, "locked-legacy-settings");
    var legacy = new SettingsService(legacyDirectory);
    var legacyPath = Path.Combine(legacyDirectory, "settings.json");
    File.WriteAllText(legacyPath, JsonSerializer.Serialize(new AppSettings { MaxHistoryItems = 124 }));
    AssertLockedLoadRefused(legacyPath, () => legacy.Load());
    Expect(legacy.Load().MaxHistoryItems == 124, "Locked legacy settings must survive and migrate after lock release.");
}

static void AssertLockedLoadRefused(string path, Action load)
{
    var expected = File.ReadAllBytes(path);
    using (var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        var refused = false;
        try { load(); }
        catch (IOException) { refused = true; }
        Expect(refused, "Unreadable storage that cannot be quarantined must refuse load, not return empty/default data.");
    }
    Expect(File.ReadAllBytes(path).SequenceEqual(expected), "Fail-closed load must preserve every original byte.");
}

static void TestOversizedFile(string root)
{
    var directory = Path.Combine(root, "oversized-file");
    var store = new EncryptedHistoryStore(directory);
    using (var file = File.Create(Path.Combine(directory, "history.dat"))) file.SetLength(StorageSafety.MaximumDocumentBytes + 1L);
    var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
    Expect(store.Load().Count == 0 && store.LastLoadRecovered, "Oversized encrypted file must be rejected.");
    Expect(GC.GetAllocatedBytesForCurrentThread() - allocatedBefore < 2 * 1024 * 1024, "File-size check must happen before allocating the 100MiB file buffer.");
}

static void TestImages(string root)
{
    var store = new EncryptedHistoryStore(Path.Combine(root, "images"));
    Expect(!store.TrySave([new ClipboardEntry { Kind = ClipboardEntryKind.Image, Payload = "not base64" }]), "Malformed base64 must be refused.");
    var frame = System.Windows.Media.Imaging.BitmapSource.Create(2, 2, 96, 96, System.Windows.Media.PixelFormats.Bgra32, null, new byte[16], 8);
    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(frame));
    using var output = new MemoryStream();
    encoder.Save(output);
    var png = output.ToArray();
    var valid = new ClipboardEntry { Kind = ClipboardEntryKind.Image, Payload = Convert.ToBase64String(png) };
    Expect(store.TrySave([valid]) && store.Load().Single().Payload == valid.Payload, "Valid PNG must remain supported.");
    Expect(!store.TrySave([new ClipboardEntry { Kind = ClipboardEntryKind.Image, Payload = Convert.ToBase64String(png[..33]) }]), "Truncated PNG with valid header must be refused.");
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(16, 4), 100_000);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(png.AsSpan(20, 4), 100_000);
    Expect(!store.TrySave([new ClipboardEntry { Kind = ClipboardEntryKind.Image, Payload = Convert.ToBase64String(png) }]), "Huge pixel dimensions must be refused before decoding.");
    Expect(!store.TrySave([new ClipboardEntry { Kind = ClipboardEntryKind.Image, Payload = new string('A', 4 * 1024 * 1024 + 4) }]), "Oversized base64 must be refused before allocating decoded bytes.");
}

static void TestWriteFailure(string root)
{
    var directory = Path.Combine(root, "write-failure");
    var store = new EncryptedHistoryStore(directory);
    Expect(store.TrySave([new ClipboardEntry { Payload = "committed" }]), "Initial state must save.");
    try
    {
        StorageSafety.FaultInjection = point => { if (point == "atomic.before-replace") throw new IOException("Injected full disk before replace"); };
        Expect(!store.TrySave([new ClipboardEntry { Payload = "lost write" }]), "Failed write must report failure.");
    }
    finally { StorageSafety.FaultInjection = null; }
    Expect(store.Load().Single().Payload == "committed", "Failed replace must preserve committed contents.");
    Expect(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Failed write must clean temporary files.");
    using (var held = new FileStream(Path.Combine(directory, "history.dat"), FileMode.Open, FileAccess.Read, FileShare.None))
        Expect(!store.TrySave([new ClipboardEntry { Payload = "blocked write" }]), "Sharing violation must be handled.");
    Expect(store.Load().Single().Payload == "committed", "Locked-file failure must preserve committed contents.");
}

static (SettingsService Settings, EncryptedHistoryStore History) PrepareRestore(string directory)
{
    var settings = new SettingsService(directory);
    var history = new EncryptedHistoryStore(directory);
    Expect(settings.TrySave(new AppSettings { MaxHistoryItems = 88 }) && history.TrySave([new ClipboardEntry { Payload = "original history" }]), "Restore fixture must save.");
    Expect(settings.TrySave(new AppSettings { MaxHistoryItems = 77 }) && history.TrySave([new ClipboardEntry { Payload = "current history" }]), "Restore fixture previous copies must exist.");
    return (settings, history);
}

static void TestRestoreRollback(string root)
{
    var directory = Path.Combine(root, "restore-rollback");
    var stores = PrepareRestore(directory);
    var originals = Directory.GetFiles(directory, "*.dat").ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
    try
    {
        StorageSafety.FaultInjection = point => { if (point == "restore.after-settings") throw new IOException("Injected history write failure"); };
        var result = stores.Settings.TryRestoreWithHistory(stores.History, new AppSettings { MaxHistoryItems = 33 }, [new ClipboardEntry { Payload = "new" }]);
        Expect(result == RestoreResult.Failed, "Failed restore must report a verified rollback.");
    }
    finally { StorageSafety.FaultInjection = null; }
    foreach (var original in originals) Expect(File.ReadAllBytes(Path.Combine(directory, original.Key!)).SequenceEqual(original.Value), "Rollback must preserve both current and previous files byte-for-byte.");
    Expect(stores.Settings.TryRestoreWithHistory(stores.History, new AppSettings { MaxHistoryItems = 33 }, [new ClipboardEntry { Payload = "restored" }]) == RestoreResult.Applied, "Normal restore must commit both stores.");
    Expect(stores.Settings.Load().MaxHistoryItems == 33 && stores.History.Load().Single().Payload == "restored", "Both restored stores must match the new state.");
}

static void TestRestoreRecoveryRequired(string root)
{
    var directory = Path.Combine(root, "restore-required");
    var stores = PrepareRestore(directory);
    try
    {
        StorageSafety.FaultInjection = point => { if (point is "restore.after-settings" or "rollback.before-history.dat") throw new IOException("Injected locked recovery target"); };
        Expect(stores.Settings.TryRestoreWithHistory(stores.History, new AppSettings { MaxHistoryItems = 33 }, []) == RestoreResult.RecoveryRequired, "Failed rollback must not claim original data is fully restored.");
        Expect(!stores.Settings.TrySave(new AppSettings()) && !stores.History.TrySave([]), "No new writes may proceed while recovery cannot complete.");
        Expect(File.Exists(Path.Combine(directory, "restore.pending.json")), "Recovery evidence must remain durable.");
    }
    finally { StorageSafety.FaultInjection = null; }
    Expect(stores.Settings.RecoverPendingRestore(), "Recovery must retry successfully after the fault is removed.");
    Expect(stores.Settings.Load().MaxHistoryItems == 77 && stores.History.Load().Single().Payload == "current history", "Recovery must finish the complete original pair.");
}

static void TestRestoreProcessCrash(string root)
{
    foreach (var point in new[] { "restore.after-journal", "restore.after-settings", "restore.after-history", "restore.before-commit" })
    {
        var directory = Path.Combine(root, point.Replace('.', '-'));
        var stores = PrepareRestore(directory);
        var dotnet = Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..", "dotnet.exe"));
        var start = new ProcessStartInfo(dotnet) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        start.ArgumentList.Add("--crash-restore");
        start.ArgumentList.Add(directory);
        start.ArgumentList.Add(point);
        using var child = Process.Start(start) ?? throw new InvalidOperationException("Failed to start isolated crash fixture.");
        Expect(child.WaitForExit(15_000) && child.ExitCode == 73, "Restore fixture must terminate at the requested write boundary.");
        Expect(new SettingsService(directory).RecoverPendingRestore(), "Next startup must recover an abruptly terminated restore.");
        Expect(stores.Settings.Load().MaxHistoryItems == 77 && stores.History.Load().Single().Payload == "current history", "Abrupt termination must never expose mixed settings/history.");
    }
}

static void TestClear(string root)
{
    var directory = Path.Combine(root, "clear");
    var stores = PrepareRestore(directory);
    File.WriteAllText(Path.Combine(directory, "history.dat.corrupt-synthetic"), "synthetic retired data");
    Expect(stores.History.TryClear(), "Clear must commit an empty current history.");
    Expect(new EncryptedHistoryStore(directory).Load().Count == 0, "Restart after clear must stay empty.");
    Expect(!File.Exists(Path.Combine(directory, "history.prev.dat")) && !Directory.GetFiles(directory, "history*.dat.corrupt-*").Any(), "Clear must remove prior and quarantined history copies.");
}

static void TestNativeInputLayout()
{
    var type = typeof(ClipboardEntry).Assembly.GetType("NeClipboardWin.NativeMethods+INPUT") ?? throw new InvalidOperationException("Native input type is missing.");
    Expect(Marshal.SizeOf(type) == (Environment.Is64BitProcess ? 40 : 28), "Win32 INPUT must include the complete native union with platform alignment.");
}

static void TestPalettePlacement()
{
    Expect(PalettePlacement.ClampOrigin(-30, 0, 1920, 460) == 0,
        "The palette must not cross the work area's left edge.");
    Expect(PalettePlacement.ClampOrigin(1800, 0, 1920, 460) == 1460,
        "An expanded palette must remain fully visible at the right edge.");
    Expect(PalettePlacement.ClampOrigin(-1800, -1920, 1920, 460) == -1800,
        "Negative-coordinate monitors must retain valid origins.");
    Expect(PalettePlacement.ClampOrigin(100, 0, 300, 460) == 0,
        "If the work area is smaller than the window, anchor to its start rather than throwing.");
    Expect(PalettePlacement.ClampOrigin(double.NaN, 10, 100, 30) == 10,
        "Invalid requested coordinates must fall back to the work-area origin.");
}

static void TestEncryptedLegacyUpgrade(string root)
{
    var directory = Path.Combine(root, "encrypted-legacy-upgrade");
    Directory.CreateDirectory(directory);
    var oldSettings = new
    {
        CaptureEnabled = true, MaxHistoryItems = 111, LaunchAtSignIn = false, AutoPasteAfterSelection = true,
        ExcludedApplications = new[] { "private.exe" },
        Snippets = new[] { new Snippet { Title = "Previous version", Content = "unchanged" } }
    };
    var oldHistory = new[] { new ClipboardEntry { Payload = "old-format clipboard", IsPinned = true } };
    WriteEncrypted(Path.Combine(directory, "settings.dat"), oldSettings);
    WriteEncrypted(Path.Combine(directory, "history.dat"), oldHistory);
    var settingsService = new SettingsService(directory);
    var historyStore = new EncryptedHistoryStore(directory);
    var settings = settingsService.Load();
    var history = historyStore.Load();
    Expect(settings.MaxHistoryItems == 111 && settings.HotkeyModifiers == 6 && settings.HotkeyVirtualKey == 0x56
        && settings.Snippets.Single().Content == "unchanged" && history.Single().IsPinned, "Old encrypted raw documents must migrate without losing values.");
    Expect(settingsService.TrySave(settings) && historyStore.TrySave(history), "The migrated values must save using the new schema.");
    Expect(new SettingsService(directory).Load().Snippets.Single().Content == "unchanged"
        && new EncryptedHistoryStore(directory).Load().Single().Payload == "old-format clipboard", "Repeated startup must read the new schema.");
}

static void TestJsonPreflight()
{
    var rejected = 0;
    foreach (var json in new[]
    {
        new string('[', 20) + "0" + new string(']', 20),
        "{\"Title\":\"" + new string('a', 201) + "\"}",
        "{\"Title\":\"" + string.Concat(Enumerable.Repeat("\\u0061", 201)) + "\"}",
        "[" + string.Join(',', Enumerable.Repeat("0", 50_001)) + "]"
    })
    {
        try { StorageSafety.PreflightJson(Encoding.UTF8.GetBytes(json)); }
        catch (Exception exception) when (exception is JsonException or InvalidDataException) { rejected++; }
    }
    Expect(rejected == 4, "JSON depth, string length (including escaped content) and token count must be checked before deserialization.");
}

static void TestInvalidRecoveryJournal(string root)
{
    var directory = Path.Combine(root, "invalid-journal");
    var stores = PrepareRestore(directory);
    var before = File.ReadAllBytes(Path.Combine(directory, "history.dat"));
    File.WriteAllText(Path.Combine(directory, "restore.pending.json"), "{\"SchemaVersion\":1,\"TransactionId\":\"" + Guid.NewGuid().ToString("N") + "\",\"Files\":[null,null,null,null]}");
    Expect(!stores.Settings.RecoverPendingRestore(), "Malformed journal must block recovery safely.");
    Expect(!stores.History.TrySave([]), "Malformed journal must block overwriting user state.");
    Expect(File.ReadAllBytes(Path.Combine(directory, "history.dat")).SequenceEqual(before), "Malformed recovery metadata must not change the current history.");
}

static void TestClipboardPrivacy()
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            var ordinary = new System.Windows.DataObject();
            ordinary.SetText("NeClipboardWin-QA: privacy fixture");
            Expect(ClipboardPrivacy.ShouldSkipCapture(null), "Unavailable clipboard data must not be treated as permission to capture.");
            Expect(!ClipboardPrivacy.ShouldSkipCapture(ordinary), "An ordinary clipboard item without privacy markers must remain capturable.");
            var excluded = new System.Windows.DataObject();
            excluded.SetData(ClipboardPrivacy.ExcludeFormat, new MemoryStream());
            Expect(ClipboardPrivacy.ShouldSkipCapture(excluded), "Any explicit exclusion marker must prevent capture.");
            foreach (var value in new object[] { 0U, -1, 2U, new byte[] { 0, 0, 0, 0 }, new byte[] { 0, 0, 0, 1 }, new byte[3], new byte[5], "1", new MemoryStream(new byte[1_024]) })
            {
                var data = new System.Windows.DataObject();
                data.SetData(ClipboardPrivacy.HistoryFormat, value);
                Expect(ClipboardPrivacy.ShouldSkipCapture(data), "Opt-out, malformed or unknown history markers must fail closed.");
                (value as IDisposable)?.Dispose();
            }
            using var allowedStream = new MemoryStream(new byte[] { 1, 0, 0, 0 });
            allowedStream.Position = 3;
            Expect(ClipboardPrivacy.TryReadDword(allowedStream, out var allowed) && allowed == 1 && allowedStream.Position == 3, "DWORD stream parsing must be exact, little-endian, bounded and preserve the caller's position.");
            var allowedData = new System.Windows.DataObject();
            allowedData.SetData(ClipboardPrivacy.HistoryFormat, allowedStream);
            Expect(!ClipboardPrivacy.ShouldSkipCapture(allowedData), "An explicit DWORD1 history marker permits capture.");
            using var disposed = new MemoryStream(new byte[4]);
            disposed.Dispose();
            Expect(!ClipboardPrivacy.TryReadDword(disposed, out _), "Disposed marker stream must be rejected without throwing.");
            ClipboardPrivacy.ApplyPrivateReplayPolicy(ordinary);
            Expect(ClipboardPrivacy.TryReadDword(ordinary.GetData(ClipboardPrivacy.HistoryFormat), out var history) && history == 0
                && ClipboardPrivacy.TryReadDword(ordinary.GetData(ClipboardPrivacy.CloudFormat), out var cloud) && cloud == 0,
                "Every replay object must opt out of Windows clipboard history and cloud upload atomically.");
            Expect(ordinary.GetText() == "NeClipboardWin-QA: privacy fixture", "Replay policy must not alter actual clipboard payload.");
        }
        catch (Exception exception) { failure = exception; }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw new InvalidOperationException("Clipboard privacy regression failed.", failure);
}
