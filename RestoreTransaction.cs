using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace NeClipboardWin;

// A durable rollback journal spans the two independently encrypted stores. Deleting the journal
// is the commit point. A crash before that point restores the old pair before either store loads.
internal static class RestoreTransaction
{
    private const string JournalName = "restore.pending.json";
    private static readonly string[] FileNames = ["settings.dat", "history.dat", "settings.prev.dat", "history.prev.dat"];

    internal static RestoreResult Apply(string directory, AppSettings settings, IEnumerable<ClipboardEntry> entries)
    {
        if (!Recover(directory)) return RestoreResult.RecoveryRequired;
        string? rollbackDirectory = null;
        try
        {
            if (!StorageSafety.ValidSettings(settings) || !StorageSafety.TrySnapshot(entries, out var snapshot)) return RestoreResult.Failed;
            var settingsBytes = SettingsService.Encode(settings.DeepClone());
            var historyBytes = EncryptedHistoryStore.Encode(snapshot);
            var journal = new Journal { SchemaVersion = 1, TransactionId = Guid.NewGuid().ToString("N") };
            rollbackDirectory = Path.Combine(directory, ".restore-" + journal.TransactionId);
            Directory.CreateDirectory(rollbackDirectory);
            foreach (var name in FileNames)
            {
                var source = Path.Combine(directory, name);
                var record = new FileRecord { Name = name, Existed = File.Exists(source) };
                if (record.Existed)
                {
                    using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
                    if (input.Length > StorageSafety.MaximumDocumentBytes) throw new InvalidDataException("Rollback file exceeds its size limit.");
                    using var output = new FileStream(Path.Combine(rollbackDirectory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    input.CopyTo(output);
                    output.Flush(flushToDisk: true);
                    input.Position = 0;
                    record.Length = input.Length;
                    record.Sha256 = Convert.ToHexString(SHA256.HashData(input));
                }
                journal.Files.Add(record);
            }
            SettingsService.AtomicWrite(Path.Combine(directory, JournalName), JsonSerializer.SerializeToUtf8Bytes(journal));
            StorageSafety.Fault("restore.after-journal");
            SettingsService.AtomicWrite(Path.Combine(directory, "settings.dat"), settingsBytes, Path.Combine(directory, "settings.prev.dat"));
            StorageSafety.Fault("restore.after-settings");
            SettingsService.AtomicWrite(Path.Combine(directory, "history.dat"), historyBytes, Path.Combine(directory, "history.prev.dat"));
            StorageSafety.Fault("restore.after-history");
            _ = SettingsService.ReadSettings(Path.Combine(directory, "settings.dat"));
            _ = EncryptedHistoryStore.ReadEntries(Path.Combine(directory, "history.dat"));
            StorageSafety.Fault("restore.before-commit");
            File.Delete(Path.Combine(directory, JournalName));
            Cleanup(rollbackDirectory);
            return RestoreResult.Applied;
        }
        catch (Exception exception) when (StorageSafety.Expected(exception))
        {
            var recovered = Recover(directory);
            if (recovered && rollbackDirectory is not null) Cleanup(rollbackDirectory);
            return recovered ? RestoreResult.Failed : RestoreResult.RecoveryRequired;
        }
    }

    internal static bool Recover(string directory)
    {
        var journalPath = Path.Combine(directory, JournalName);
        if (!File.Exists(journalPath))
        {
            CleanupOrphans(directory);
            return true;
        }
        try
        {
            var journal = JsonSerializer.Deserialize<Journal>(StorageSafety.ReadBounded(journalPath, 16_384), StorageSafety.JsonOptions);
            if (journal is null || journal.SchemaVersion != 1 || !Guid.TryParseExact(journal.TransactionId, "N", out _)
                || journal.Files is null || journal.Files.Count != FileNames.Length || journal.Files.Any(file => file is null)
                || !journal.Files.Select(file => file.Name).SequenceEqual(FileNames)) return false;
            var rollbackDirectory = Path.Combine(directory, ".restore-" + journal.TransactionId);
            if ((File.GetAttributes(rollbackDirectory) & FileAttributes.ReparsePoint) != 0) return false;
            // Verify every rollback file before changing either live store.
            foreach (var record in journal.Files)
                if (record.Existed && !Matches(Path.Combine(rollbackDirectory, record.Name), record)) return false;
            foreach (var record in journal.Files)
            {
                StorageSafety.Fault("rollback.before-" + record.Name);
                var target = Path.Combine(directory, record.Name);
                if (record.Existed)
                {
                    var source = Path.Combine(rollbackDirectory, record.Name);
                    var bytes = record.Length == 0 ? [] : StorageSafety.ReadBounded(source);
                    SettingsService.AtomicWrite(target, bytes);
                }
                else if (File.Exists(target)) File.Delete(target);
            }
            foreach (var record in journal.Files)
            {
                var target = Path.Combine(directory, record.Name);
                if (record.Existed ? !Matches(target, record) : File.Exists(target)) return false;
            }
            File.Delete(journalPath);
            Cleanup(rollbackDirectory);
            return true;
        }
        catch (Exception exception) when (StorageSafety.Expected(exception)) { return false; }
    }

    private static bool Matches(string path, FileRecord record)
    {
        if (record.Length < 0 || record.Length > StorageSafety.MaximumDocumentBytes || record.Sha256 is null || record.Sha256.Length != 64) return false;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        return input.Length == record.Length && string.Equals(Convert.ToHexString(SHA256.HashData(input)), record.Sha256, StringComparison.Ordinal);
    }

    private static void CleanupOrphans(string directory)
    {
        try
        {
            foreach (var path in Directory.EnumerateDirectories(directory, ".restore-*"))
                if (Guid.TryParseExact(Path.GetFileName(path)[9..], "N", out _)) Cleanup(path);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void Cleanup(string directory)
    {
        try
        {
            if (!Directory.Exists(directory) || (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) return;
            foreach (var name in FileNames)
            {
                var path = Path.Combine(directory, name);
                if (File.Exists(path)) File.Delete(path);
            }
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class Journal
    {
        public int SchemaVersion { get; init; }
        public string TransactionId { get; init; } = string.Empty;
        public List<FileRecord> Files { get; init; } = [];
    }

    private sealed class FileRecord
    {
        public string Name { get; init; } = string.Empty;
        public bool Existed { get; init; }
        public long Length { get; set; }
        public string Sha256 { get; set; } = string.Empty;
    }
}
