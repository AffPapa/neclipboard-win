using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;

[assembly: InternalsVisibleTo("NeClipboardWin.Verification")]

namespace NeClipboardWin;

internal static class StorageSafety
{
    internal const int MaximumEntries = 1_000;
    internal const int MaximumPayloadBytes = 15 * 1024 * 1024;
    internal const int MaximumImageBytes = 3 * 1024 * 1024;
    internal const int MaximumImagePixels = 8_000_000;
    internal const int MaximumTextCharacters = 500_000;
    internal const int MaximumSnippetBytes = 2 * 1024 * 1024;
    internal const int MaximumDocumentBytes = 100 * 1024 * 1024;
    internal const int MaximumSettingsBytes = 16 * 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private static readonly ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
    internal static Action<string>? FaultInjection { get; set; }

    internal static object Gate(string directory) => Gates.GetOrAdd(Path.GetFullPath(directory), _ => new object());
    internal static void Fault(string point) => FaultInjection?.Invoke(point);
    internal static bool Expected(Exception exception) => exception is CryptographicException or JsonException
        or IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException or NotSupportedException;

    internal static bool ValidSettings(AppSettings? settings)
    {
        if (settings is null || settings.MaxHistoryItems is < 10 or > MaximumEntries
            || settings.ExcludedApplications is null || settings.Snippets is null
            || settings.ExcludedApplications.Count > 100 || settings.Snippets.Count > 1_000
            || (settings.HotkeyModifiers & ~7U) != 0 || (settings.HotkeyModifiers & 3U) == 0
            || !(settings.HotkeyVirtualKey is >= 0x41 and <= 0x5A or >= 0x30 and <= 0x39 or >= 0x70 and <= 0x7A)
            || (settings.CapturePausedUntilUtc is DateTime paused && paused.Kind != DateTimeKind.Utc)) return false;
        if (settings.ExcludedApplications.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 260
            || value.IndexOfAny(['\0', '\r', '\n', '/', '\\']) >= 0)) return false;
        long bytes = 0;
        var ids = new HashSet<Guid>();
        foreach (var snippet in settings.Snippets)
        {
            if (snippet is null || snippet.Id == Guid.Empty || !ids.Add(snippet.Id)
                || string.IsNullOrWhiteSpace(snippet.Title) || snippet.Title.Length > 200
                || snippet.Content is null || snippet.Content.Length > MaximumTextCharacters
                || snippet.UpdatedUtc.Kind != DateTimeKind.Utc) return false;
            bytes += Encoding.UTF8.GetByteCount(snippet.Content) + Encoding.UTF8.GetByteCount(snippet.Title);
            if (bytes > MaximumSnippetBytes) return false;
        }
        return true;
    }

    internal static bool TrySnapshot(IEnumerable<ClipboardEntry>? source, out List<ClipboardEntry> entries)
    {
        entries = [];
        if (source is null) return false;
        // The caller owns synchronization while taking a UI snapshot; never truncate a backup silently.
        foreach (var entry in source)
        {
            if (entries.Count == MaximumEntries || entry is null) return false;
            entries.Add(entry.DeepClone());
        }
        return ValidEntries(entries);
    }

    internal static bool ValidEntries(IReadOnlyCollection<ClipboardEntry>? entries)
    {
        if (entries is null || entries.Count > MaximumEntries) return false;
        long bytes = 0;
        var ids = new HashSet<Guid>();
        foreach (var entry in entries)
        {
            if (entry is null || entry.Id == Guid.Empty || !ids.Add(entry.Id) || !Enum.IsDefined(entry.Kind)
                || entry.Payload is null || entry.SourceApplication is null || entry.SourceApplication.Length > 260
                || entry.ContentHash is null || entry.ContentHash.Length > 128
                || entry.CreatedUtc.Kind != DateTimeKind.Utc || entry.LastUsedUtc.Kind != DateTimeKind.Utc) return false;
            if (entry.Kind != ClipboardEntryKind.Image && entry.Payload.Length > MaximumTextCharacters) return false;
            bytes += Encoding.UTF8.GetByteCount(entry.Payload);
            if (bytes > MaximumPayloadBytes) return false;
            if (entry.Kind == ClipboardEntryKind.Image && !entry.ValidateImagePayload(ValidImage)) return false;
            if (entry.Kind == ClipboardEntryKind.Files && !ValidFileList(entry.Payload)) return false;
        }
        return true;
    }

    private static bool ValidFileList(string payload)
    {
        if (payload.Length == 0 || payload.Contains('\0')) return false;
        var remaining = payload.AsSpan();
        var count = 0;
        while (!remaining.IsEmpty)
        {
            var end = remaining.IndexOf('\n');
            var line = (end < 0 ? remaining : remaining[..end]).Trim();
            if (line.IsEmpty || line.Length > 32_767 || ++count > 100) return false;
            if (end < 0) break;
            remaining = remaining[(end + 1)..];
        }
        return count > 0;
    }

    private static bool ValidImage(string payload)
    {
        if (payload.Length < 44 || payload.Length > ((MaximumImageBytes + 2) / 3) * 4 || payload.Length % 4 != 0) return false;
        // Reject whitespace/noncanonical base64 and oversized dimensions before allocating the decoded image.
        foreach (var character in payload)
            if (!(character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '+' or '/' or '=')) return false;
        Span<byte> header = stackalloc byte[33];
        if (!Convert.TryFromBase64Chars(payload.AsSpan(0, 44), header, out var headerBytes) || headerBytes < 33) return false;
        ReadOnlySpan<byte> signature = [137, 80, 78, 71, 13, 10, 26, 10];
        if (!header[..8].SequenceEqual(signature) || BinaryPrimitives.ReadUInt32BigEndian(header[8..12]) != 13
            || !header[12..16].SequenceEqual("IHDR"u8)) return false;
        var width = BinaryPrimitives.ReadUInt32BigEndian(header[16..20]);
        var height = BinaryPrimitives.ReadUInt32BigEndian(header[20..24]);
        if (width == 0 || height == 0 || (ulong)width * height > MaximumImagePixels) return false;
        var decodedLength = payload.Length / 4 * 3 - (payload.EndsWith("==", StringComparison.Ordinal) ? 2 : payload.EndsWith('=') ? 1 : 0);
        if (decodedLength > MaximumImageBytes) return false;
        var bytes = new byte[decodedLength];
        if (!Convert.TryFromBase64String(payload, bytes, out var count) || count != decodedLength) return false;
        try
        {
            using var stream = new MemoryStream(bytes, false);
            var decoder = new PngBitmapDecoder(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count != 1) return false;
            var frame = decoder.Frames[0];
            if (frame.PixelWidth != width || frame.PixelHeight != height || frame.Format.BitsPerPixel is <= 0 or > 64) return false;
            var stride = checked((frame.PixelWidth * frame.Format.BitsPerPixel + 7) / 8);
            // Force decoding (including corrupt IDAT) within the verified pixel budget.
            frame.CopyPixels(new Int32Rect(0, 0, frame.PixelWidth, frame.PixelHeight), new byte[checked(stride * frame.PixelHeight)], stride, 0);
            return true;
        }
        catch (Exception exception) when (Expected(exception) || exception is InvalidOperationException or OverflowException
            or FormatException or System.Runtime.InteropServices.ExternalException)
        {
            return false;
        }
    }

    internal static byte[] ReadBounded(string path, int maximumBytes = MaximumDocumentBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximumBytes) throw new InvalidDataException("Storage file exceeds its size limit.");
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    internal static byte[] ReadJson(string path, int maximumBytes = MaximumDocumentBytes)
    {
        var encrypted = ReadBounded(path, maximumBytes);
        var json = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
        try
        {
            if (json.Length > maximumBytes) throw new InvalidDataException("Decrypted storage exceeds its size limit.");
            PreflightJson(json);
            return json;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(json);
            throw;
        }
    }

    internal static void PreflightJson(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { MaxDepth = 16 });
        var tokens = 0;
        var property = string.Empty;
        while (reader.Read())
        {
            if (++tokens > 50_000) throw new InvalidDataException("Too many JSON values.");
            if (reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueSpan.Length > 100) throw new InvalidDataException("Invalid property name.");
                property = reader.GetString() ?? string.Empty;
            }
            else if (reader.TokenType == JsonTokenType.String)
            {
                var limit = property switch
                {
                    "Payload" => ((MaximumImageBytes + 2) / 3) * 4,
                    "Content" => MaximumTextCharacters,
                    "Title" => 200,
                    "SourceApplication" => 260,
                    "ContentHash" => 128,
                    "ExcludedApplications" => 260,
                    _ => 1_024
                };
                if (DecodedCharacterCount(reader.ValueSpan, limit) > limit) throw new InvalidDataException("JSON string exceeds its size limit.");
            }
        }
    }

    private static int DecodedCharacterCount(ReadOnlySpan<byte> value, int limit)
    {
        var count = 0;
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character == '\\')
            {
                if (++index >= value.Length) return limit + 1;
                if (value[index] == 'u') index += 4;
                count++;
            }
            else if (character < 0x80 || character >= 0xC0) count += character >= 0xF0 ? 2 : 1;
            if (count > limit) return count;
        }
        return count;
    }

    internal static byte[] Encrypt<T>(T document, int maximumBytes = MaximumDocumentBytes)
    {
        using var stream = new BoundedMemoryStream(maximumBytes - 4096);
        JsonSerializer.Serialize(stream, document, JsonOptions);
        var json = stream.ToArray();
        try { return ProtectedData.Protect(json, null, DataProtectionScope.CurrentUser); }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
            CryptographicOperations.ZeroMemory(stream.GetBuffer().AsSpan(0, checked((int)stream.Length)));
        }
    }

    private sealed class BoundedMemoryStream(int limit) : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Length + count > limit) throw new InvalidDataException("Serialized storage exceeds its size limit.");
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > limit) throw new InvalidDataException("Serialized storage exceeds its size limit.");
            base.Write(buffer);
        }
    }
}
