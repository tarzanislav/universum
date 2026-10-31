using System.Runtime.InteropServices;

namespace Universum.Platform.Windows;

/// <summary>
/// Hlasové písanie Windows (owner, 31. 10. 2026 – POZNÁMKY): the same as Win + H – Windows' own voice typing writes into the
/// field that has the focus. It works through Microsoft's service (the internet); UNIVERSUM itself sends nothing.
/// </summary>
public static partial class VoiceTyping
{
    private const byte WindowsKey = 0x5B;
    private const byte H = 0x48;
    private const uint KeyUp = 0x0002;

    /// <summary>Opens the voice typing of Windows for the field with the focus.</summary>
    public static void Open()
    {
        KeyboardEvent(WindowsKey, 0, 0, 0);
        KeyboardEvent(H, 0, 0, 0);
        KeyboardEvent(H, 0, KeyUp, 0);
        KeyboardEvent(WindowsKey, 0, KeyUp, 0);
    }

    [LibraryImport("user32.dll", EntryPoint = "keybd_event")]
    private static partial void KeyboardEvent(byte key, byte scan, uint flags, nuint extra);
}
