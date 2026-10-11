using System.Runtime.InteropServices;

namespace NeClipboardWin;

internal static class NativeMethods
{
    internal const int WmClipboardUpdate = 0x031D;
    internal const int WmHotkey = 0x0312;
    internal const int WmSettingChange = 0x001A;
    internal const int WmThemeChanged = 0x031A;
    internal const uint ModControl = 0x0002;
    internal const uint ModAlt = 0x0001;
    internal const uint ModShift = 0x0004;
    internal const uint ModNoRepeat = 0x4000;
    internal const ushort VkV = 0x56;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    internal static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    internal static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll")]
    internal static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint SendInput(uint inputCount, INPUT[] inputs, int inputSize);

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int valueSize);

    internal static void SetImmersiveDarkTitleBar(IntPtr hwnd, bool enabled)
    {
        var value = enabled ? 1 : 0;
        const int darkModeAttribute = 20;
        const int legacyDarkModeAttribute = 19;
        if (DwmSetWindowAttribute(hwnd, darkModeAttribute, ref value, sizeof(int)) != 0)
            DwmSetWindowAttribute(hwnd, legacyDarkModeAttribute, ref value, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct INPUT
    {
        internal uint Type;
        internal InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    internal struct InputUnion
    {
        [FieldOffset(0)] internal KEYBDINPUT Keyboard;
        // INPUT is a union. Including MOUSEINPUT preserves its native size (40 bytes on x64).
        [FieldOffset(0)] internal MOUSEINPUT Mouse;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MOUSEINPUT
    {
        internal int X;
        internal int Y;
        internal uint MouseData;
        internal uint Flags;
        internal uint Time;
        internal IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KEYBDINPUT
    {
        internal ushort VirtualKey;
        internal ushort ScanCode;
        internal uint Flags;
        internal uint Time;
        internal IntPtr ExtraInfo;
    }

    internal static uint SendPasteShortcut()
    {
        const uint inputKeyboard = 1;
        const uint keyUp = 0x0002;
        var inputs = new[]
        {
            new INPUT { Type = inputKeyboard, Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = 0x11 } } },
            new INPUT { Type = inputKeyboard, Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = VkV } } },
            new INPUT { Type = inputKeyboard, Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = VkV, Flags = keyUp } } },
            new INPUT { Type = inputKeyboard, Data = new InputUnion { Keyboard = new KEYBDINPUT { VirtualKey = 0x11, Flags = keyUp } } }
        };
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }
}
