using System.Text.Json.Serialization;
using System.IO;
using System.Text;

namespace NeClipboardWin;

public enum ClipboardEntryKind
{
    Text,
    Image,
    Files
}

public sealed class ClipboardEntry
{
    public const int PreviewCharacterLimit = 200;
    private string? _preview;
    private ImageValidation _imageValidation = new();
    public Guid Id { get; init; } = Guid.NewGuid();
    public ClipboardEntryKind Kind { get; init; }
    public string Payload { get; init; } = string.Empty;
    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;
    public bool IsPinned { get; set; }
    public string SourceApplication { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;

    [JsonIgnore]
    public string TypeLabel => Kind switch
    {
        ClipboardEntryKind.Image => "Изображение",
        ClipboardEntryKind.Files => "Файлы",
        _ => "Текст"
    };

    [JsonIgnore]
    public string Icon => Kind switch
    {
        ClipboardEntryKind.Image => "▣",
        ClipboardEntryKind.Files => "▤",
        _ => "T"
    };

    [JsonIgnore]
    public string Preview => _preview ??= Kind switch
    {
        ClipboardEntryKind.Image => "Изображение из буфера обмена",
        ClipboardEntryKind.Files => BuildFilesPreview(),
        _ => BuildTextPreview()
    };

    public bool MatchesSearch(string search) => string.IsNullOrEmpty(search)
        || (Kind != ClipboardEntryKind.Image && Payload.Contains(search, StringComparison.OrdinalIgnoreCase))
        || Preview.Contains(search, StringComparison.OrdinalIgnoreCase)
        || TypeLabel.Contains(search, StringComparison.OrdinalIgnoreCase)
        || SourceApplication.Contains(search, StringComparison.OrdinalIgnoreCase);

    public ClipboardEntry DeepClone() => new()
    {
        Id = Id, Kind = Kind, Payload = Payload, CreatedUtc = CreatedUtc,
        LastUsedUtc = LastUsedUtc, IsPinned = IsPinned,
        SourceApplication = SourceApplication, ContentHash = ContentHash,
        _preview = _preview, _imageValidation = _imageValidation
    };

    internal bool ValidateImagePayload(Func<string, bool> validate)
    {
        lock (_imageValidation)
            return _imageValidation.Valid ??= validate(Payload);
    }

    private sealed class ImageValidation { internal bool? Valid; }

    [JsonIgnore]
    public string TimestampText => LastUsedUtc.ToLocalTime().ToString("dd.MM  HH:mm");

    [JsonIgnore]
    public string AccessibleSummary
    {
        get
        {
            var preview = Preview;
            if (preview.Length > 80) preview = preview[..80] + "…";
            return $"{TypeLabel}: {preview}. {TimestampText}" + (IsPinned ? ". Закреплено" : string.Empty);
        }
    }

    private string BuildFilesPreview()
    {
        var remaining = Payload.AsSpan();
        var count = 0;
        var first = string.Empty;
        while (!remaining.IsEmpty)
        {
            var end = remaining.IndexOf('\n');
            var line = (end < 0 ? remaining : remaining[..end]).Trim();
            if (!line.IsEmpty)
            {
                if (count == 0)
                {
                    var name = Path.GetFileName(line);
                    first = name.Length <= PreviewCharacterLimit ? name.ToString() : name[..PreviewCharacterLimit].ToString() + "…";
                }
                count++;
            }
            if (end < 0) break;
            remaining = remaining[(end + 1)..];
        }
        return count switch { 0 => "Файлы", 1 => first, _ => $"{first} и ещё {count - 1}" };
    }

    private string BuildTextPreview()
    {
        var source = Payload.AsSpan().Trim();
        var builder = new StringBuilder(Math.Min(source.Length, PreviewCharacterLimit + 1));
        var index = 0;
        while (index < source.Length && builder.Length < PreviewCharacterLimit)
        {
            var character = source[index++];
            if (character == '\r' && index < source.Length && source[index] == '\n') index++;
            builder.Append(character is '\r' or '\n' or '\f' or '\u0085' or '\u2028' or '\u2029' ? ' ' : character);
        }
        if (index < source.Length)
        {
            if (builder.Length > 0 && char.IsHighSurrogate(builder[^1])) builder.Length--;
            builder.Append('…');
        }
        return builder.ToString();
    }
}

public sealed class Snippet
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Title { get; set; } = "Новый сниппет";
    public string Content { get; set; } = string.Empty;
    public DateTime UpdatedUtc { get; set; } = DateTime.UtcNow;

    [JsonIgnore]
    public string AccessibleSummary => $"Сниппет: {Title}";

    public Snippet DeepClone() => new() { Id = Id, Title = Title, Content = Content, UpdatedUtc = UpdatedUtc };
}

public sealed class AppSettings
{
    public bool CaptureEnabled { get; set; } = true;
    public int MaxHistoryItems { get; set; } = 200;
    public bool LaunchAtSignIn { get; set; }
    public bool AutoPasteAfterSelection { get; set; }
    public uint HotkeyModifiers { get; set; } = 6;
    public uint HotkeyVirtualKey { get; set; } = 0x56;
    public DateTime? CapturePausedUntilUtc { get; set; }
    public List<string> ExcludedApplications { get; set; } = ["1Password.exe", "Bitwarden.exe", "KeePass.exe", "KeePassXC.exe"];
    public List<Snippet> Snippets { get; set; } = [];

    public AppSettings DeepClone() => new()
    {
        CaptureEnabled = CaptureEnabled, MaxHistoryItems = MaxHistoryItems,
        LaunchAtSignIn = LaunchAtSignIn, AutoPasteAfterSelection = AutoPasteAfterSelection,
        HotkeyModifiers = HotkeyModifiers, HotkeyVirtualKey = HotkeyVirtualKey,
        CapturePausedUntilUtc = CapturePausedUntilUtc,
        ExcludedApplications = [.. ExcludedApplications],
        Snippets = Snippets.Select(snippet => snippet.DeepClone()).ToList()
    };
}
