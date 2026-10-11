using System.Buffers.Binary;
using System.IO;
using System.Windows;

namespace NeClipboardWin;

/// <summary>Bounded interpretation of Windows clipboard privacy hints; no object deserialization.</summary>
internal static class ClipboardPrivacy
{
    internal const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";
    internal const string HistoryFormat = "CanIncludeInClipboardHistory";
    internal const string CloudFormat = "CanUploadToCloudClipboard";

    internal static bool ShouldSkipCapture(System.Windows.IDataObject? data)
    {
        if (data is null) return true;
        // This marker is defined by its presence, not by its contents.
        if (data.GetDataPresent(ExcludeFormat, autoConvert: false)) return true;
        if (!data.GetDataPresent(HistoryFormat, autoConvert: false)) return false;
        // An unknown or malformed explicit policy is handled conservatively.
        return !TryReadDword(data.GetData(HistoryFormat, autoConvert: false), out var include) || include != 1;
    }

    internal static bool TryReadDword(object? value, out uint result)
    {
        try
        {
            switch (value)
            {
                case uint unsigned:
                    result = unsigned;
                    return true;
                case int signed:
                    result = unchecked((uint)signed);
                    return true;
                case byte[] { Length: sizeof(uint) } bytes:
                    result = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
                    return true;
                case MemoryStream stream when stream.Length == sizeof(uint):
                    result = BinaryPrimitives.ReadUInt32LittleEndian(stream.ToArray());
                    return true;
            }
        }
        catch (ObjectDisposedException) { }
        result = 0;
        return false;
    }

    internal static void ApplyPrivateReplayPolicy(System.Windows.DataObject data)
    {
        // Publish these together with the payload in one SetDataObject call. A
        // temporary unmarked clipboard payload could already be seen by Windows.
        data.SetData(HistoryFormat, new MemoryStream(new byte[sizeof(uint)], writable: false), autoConvert: false);
        data.SetData(CloudFormat, new MemoryStream(new byte[sizeof(uint)], writable: false), autoConvert: false);
    }
}
