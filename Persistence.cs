using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace NeClipboardWin;

public enum RestoreResult { Applied, Failed, RecoveryRequired }

internal sealed class StorageUnavailableException : IOException
{
    internal StorageUnavailableException() : base("Unreadable storage could not be preserved. Startup was stopped to avoid replacing user data.") { }
}

public sealed class SettingsService
{
    private readonly string _path;
    private readonly string _legacyPath;
    private readonly string _previousPath;
    internal string DirectoryPath { get; }
    public bool LastLoadRecovered { get; private set; }
    public bool RecoveredFromPreviousVersion { get; private set; }
    public static string AppDataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NeClipboard Win");

    public SettingsService(string? appDataDirectory = null)
    {
        DirectoryPath = Path.GetFullPath(appDataDirectory ?? AppDataDirectory);
        Directory.CreateDirectory(DirectoryPath);
        _path = Path.Combine(DirectoryPath, "settings.dat");
        _legacyPath = Path.Combine(DirectoryPath, "settings.json");
        _previousPath = Path.Combine(DirectoryPath, "settings.prev.dat");
    }

    public bool RecoverPendingRestore()
    {
        lock (StorageSafety.Gate(DirectoryPath)) return RestoreTransaction.Recover(DirectoryPath);
    }

    public RestoreResult TryRestoreWithHistory(EncryptedHistoryStore historyStore, AppSettings settings, IEnumerable<ClipboardEntry> entries)
    {
        if (!string.Equals(DirectoryPath, historyStore.DirectoryPath, StringComparison.OrdinalIgnoreCase)) return RestoreResult.Failed;
        lock (StorageSafety.Gate(DirectoryPath)) return RestoreTransaction.Apply(DirectoryPath, settings, entries);
    }

    public AppSettings Load()
    {
        lock (StorageSafety.Gate(DirectoryPath))
        {
            if (!RestoreTransaction.Recover(DirectoryPath)) throw new InvalidOperationException("Pending restore could not be recovered. Storage is locked until recovery succeeds.");
            LastLoadRecovered = false;
            RecoveredFromPreviousVersion = false;
            try
            {
                if (File.Exists(_path))
                {
                    var settings = ReadSettings(_path);
                    TryDeleteLegacySettings();
                    return settings;
                }
                if (TryLoadPrevious(out var previous))
                {
                    RecoveredFromPreviousVersion = true;
                    return previous;
                }
                return LoadLegacySettings();
            }
            catch (Exception exception) when (StorageSafety.Expected(exception))
            {
                LastLoadRecovered = QuarantineUnreadableOrThrow(_path);
                if (TryLoadPrevious(out var previous))
                {
                    RecoveredFromPreviousVersion = true;
                    return previous;
                }
                return new AppSettings();
            }
        }
    }

    private AppSettings LoadLegacySettings()
    {
        try
        {
            if (!File.Exists(_legacyPath)) return new AppSettings();
            var json = StorageSafety.ReadBounded(_legacyPath, StorageSafety.MaximumSettingsBytes);
            StorageSafety.PreflightJson(json);
            var settings = JsonSerializer.Deserialize<AppSettings>(json, StorageSafety.JsonOptions);
            if (!StorageSafety.ValidSettings(settings)) throw new InvalidDataException("Invalid legacy settings.");
            if (TrySave(settings!)) TryDeleteLegacySettings();
            return settings!;
        }
        catch (Exception exception) when (StorageSafety.Expected(exception))
        {
            LastLoadRecovered = QuarantineUnreadableOrThrow(_legacyPath);
            return new AppSettings();
        }
    }

    internal static AppSettings ReadSettings(string path)
    {
        var json = StorageSafety.ReadJson(path, StorageSafety.MaximumSettingsBytes);
        try
        {
            AppSettings? settings;
            if (HasEnvelope(json))
            {
                var document = JsonSerializer.Deserialize<SettingsDocument>(json, StorageSafety.JsonOptions);
                if (document is null || document.Format != "NeClipboardWin.Settings" || document.SchemaVersion != 1)
                    throw new InvalidDataException("Unsupported settings schema.");
                settings = document.Settings;
            }
            else settings = JsonSerializer.Deserialize<AppSettings>(json, StorageSafety.JsonOptions);
            if (!StorageSafety.ValidSettings(settings)) throw new InvalidDataException("Invalid settings.");
            return settings!;
        }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    private static bool HasEnvelope(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        while (reader.Read())
            if (reader.TokenType == JsonTokenType.PropertyName && reader.CurrentDepth == 1
                && (reader.ValueTextEquals("Format") || reader.ValueTextEquals("SchemaVersion"))) return true;
        return false;
    }

    internal static byte[] Encode(AppSettings settings) => StorageSafety.Encrypt(new SettingsDocument
    {
        Format = "NeClipboardWin.Settings", SchemaVersion = 1, Settings = settings
    }, StorageSafety.MaximumSettingsBytes);

    public bool TrySave(AppSettings settings)
    {
        lock (StorageSafety.Gate(DirectoryPath))
        {
            try
            {
                if (!RestoreTransaction.Recover(DirectoryPath) || !StorageSafety.ValidSettings(settings)) return false;
                AtomicWrite(_path, Encode(settings.DeepClone()), _previousPath);
                return true;
            }
            catch (Exception exception) when (StorageSafety.Expected(exception)) { return false; }
        }
    }

    internal static void AtomicWrite(string path, byte[] data, string? previousPath = null)
    {
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                // Keep the normal production path to a single write. Verification can
                // install the internal fault hook to fail after a real partial temp write.
                if (data.Length > 1 && StorageSafety.FaultInjection is not null)
                {
                    var firstChunkLength = data.Length / 2;
                    stream.Write(data.AsSpan(0, firstChunkLength));
                    StorageSafety.Fault("atomic.during-temp-write");
                    stream.Write(data.AsSpan(firstChunkLength));
                }
                else stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            StorageSafety.Fault("atomic.before-replace");
            if (File.Exists(path)) File.Replace(temporaryPath, path, previousPath, ignoreMetadataErrors: true);
            else File.Move(temporaryPath, path);
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private void TryDeleteLegacySettings()
    {
        try { if (File.Exists(_legacyPath)) File.Delete(_legacyPath); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    internal static bool TryQuarantine(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N"));
            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    internal static bool QuarantineUnreadableOrThrow(string path)
    {
        // A sharing/access failure does not prove corruption. If the original cannot
        // be preserved, returning defaults would allow a later save to erase it.
        if (!TryQuarantine(path)) throw new StorageUnavailableException();
        return true;
    }

    private bool TryLoadPrevious(out AppSettings settings)
    {
        settings = new AppSettings();
        try
        {
            if (!File.Exists(_previousPath)) return false;
            settings = ReadSettings(_previousPath);
            return true;
        }
        catch (Exception exception) when (StorageSafety.Expected(exception))
        {
            QuarantineUnreadableOrThrow(_previousPath);
            return false;
        }
    }

    private sealed class SettingsDocument
    {
        public string Format { get; init; } = string.Empty;
        public int SchemaVersion { get; init; }
        public AppSettings? Settings { get; init; }
    }
}

public sealed class EncryptedHistoryStore
{
    private readonly string _path;
    private readonly string _previousPath;
    internal string DirectoryPath { get; }
    public bool LastLoadRecovered { get; private set; }
    public bool RecoveredFromPreviousVersion { get; private set; }

    public EncryptedHistoryStore(string? appDataDirectory = null)
    {
        DirectoryPath = Path.GetFullPath(appDataDirectory ?? SettingsService.AppDataDirectory);
        Directory.CreateDirectory(DirectoryPath);
        _path = Path.Combine(DirectoryPath, "history.dat");
        _previousPath = Path.Combine(DirectoryPath, "history.prev.dat");
    }

    public IReadOnlyList<ClipboardEntry> Load()
    {
        lock (StorageSafety.Gate(DirectoryPath))
        {
            if (!RestoreTransaction.Recover(DirectoryPath)) throw new InvalidOperationException("Pending restore could not be recovered. Storage is locked until recovery succeeds.");
            LastLoadRecovered = false;
            RecoveredFromPreviousVersion = false;
            try
            {
                if (File.Exists(_path)) return ReadEntries(_path);
                if (TryLoadPrevious(out var previous))
                {
                    RecoveredFromPreviousVersion = true;
                    return previous;
                }
                return [];
            }
            catch (Exception exception) when (StorageSafety.Expected(exception))
            {
                LastLoadRecovered = SettingsService.QuarantineUnreadableOrThrow(_path);
                if (TryLoadPrevious(out var previous))
                {
                    RecoveredFromPreviousVersion = true;
                    return previous;
                }
                return [];
            }
        }
    }

    internal static IReadOnlyList<ClipboardEntry> ReadEntries(string path)
    {
        var json = StorageSafety.ReadJson(path);
        try
        {
            var reader = new Utf8JsonReader(json);
            reader.Read();
            List<ClipboardEntry>? entries;
            if (reader.TokenType == JsonTokenType.StartArray)
                entries = JsonSerializer.Deserialize<List<ClipboardEntry>>(json, StorageSafety.JsonOptions);
            else
            {
                var document = JsonSerializer.Deserialize<HistoryDocument>(json, StorageSafety.JsonOptions);
                if (document is null || document.Format != "NeClipboardWin.History" || document.SchemaVersion != 1)
                    throw new InvalidDataException("Unsupported history schema.");
                entries = document.Entries;
            }
            if (!StorageSafety.ValidEntries(entries)) throw new InvalidDataException("Invalid history.");
            return entries!;
        }
        finally { CryptographicOperations.ZeroMemory(json); }
    }

    internal static byte[] Encode(List<ClipboardEntry> entries) => StorageSafety.Encrypt(new HistoryDocument
    {
        Format = "NeClipboardWin.History", SchemaVersion = 1, Entries = entries
    });

    public bool TrySave(IEnumerable<ClipboardEntry> entries)
    {
        lock (StorageSafety.Gate(DirectoryPath))
        {
            try
            {
                if (!RestoreTransaction.Recover(DirectoryPath) || !StorageSafety.TrySnapshot(entries, out var snapshot)) return false;
                SettingsService.AtomicWrite(_path, Encode(snapshot), _previousPath);
                return true;
            }
            catch (Exception exception) when (StorageSafety.Expected(exception)) { return false; }
        }
    }

    public bool TryClear()
    {
        lock (StorageSafety.Gate(DirectoryPath))
        {
            try
            {
                if (!RestoreTransaction.Recover(DirectoryPath)) return false;
                // Commit an explicit empty current file first so a crash cannot resurrect history.prev.dat.
                SettingsService.AtomicWrite(_path, Encode([]));
                if (File.Exists(_previousPath)) File.Delete(_previousPath);
                foreach (var path in Directory.EnumerateFiles(DirectoryPath, "history*.dat.corrupt-*")) File.Delete(path);
                return true;
            }
            catch (Exception exception) when (StorageSafety.Expected(exception)) { return false; }
        }
    }

    private bool TryLoadPrevious(out IReadOnlyList<ClipboardEntry> entries)
    {
        entries = [];
        try
        {
            if (!File.Exists(_previousPath)) return false;
            entries = ReadEntries(_previousPath);
            return true;
        }
        catch (Exception exception) when (StorageSafety.Expected(exception))
        {
            SettingsService.QuarantineUnreadableOrThrow(_previousPath);
            return false;
        }
    }

    private sealed class HistoryDocument
    {
        public string Format { get; init; } = string.Empty;
        public int SchemaVersion { get; init; }
        public List<ClipboardEntry>? Entries { get; init; }
    }
}

public sealed class BackupService
{
    public bool TryCreate(string path, AppSettings settings, IEnumerable<ClipboardEntry> entries)
    {
        try
        {
            if (!StorageSafety.ValidSettings(settings) || !StorageSafety.TrySnapshot(entries, out var snapshot)) return false;
            var document = new BackupDocument
            {
                Format = "NeClipboardWin.Backup", SchemaVersion = 1, CreatedUtc = DateTime.UtcNow,
                Settings = settings.DeepClone(), Entries = snapshot
            };
            SettingsService.AtomicWrite(path, StorageSafety.Encrypt(document));
            return true;
        }
        catch (Exception exception) when (StorageSafety.Expected(exception)) { return false; }
    }

    public bool TryRead(string path, out AppSettings settings, out IReadOnlyList<ClipboardEntry> entries)
    {
        settings = new AppSettings();
        entries = [];
        try
        {
            var json = StorageSafety.ReadJson(path);
            try
            {
                var document = JsonSerializer.Deserialize<BackupDocument>(json, StorageSafety.JsonOptions);
                if (document is null || document.Format != "NeClipboardWin.Backup" || document.SchemaVersion != 1
                    || !StorageSafety.ValidSettings(document.Settings) || !StorageSafety.ValidEntries(document.Entries)) return false;
                settings = document.Settings!;
                entries = document.Entries!;
                return true;
            }
            finally { CryptographicOperations.ZeroMemory(json); }
        }
        catch (Exception exception) when (StorageSafety.Expected(exception)) { return false; }
    }

    private sealed class BackupDocument
    {
        public string Format { get; init; } = string.Empty;
        public int SchemaVersion { get; init; }
        public DateTime CreatedUtc { get; init; }
        public AppSettings? Settings { get; init; }
        public List<ClipboardEntry>? Entries { get; init; }
    }
}
