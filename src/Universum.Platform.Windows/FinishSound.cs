using System.Runtime.InteropServices;

namespace Universum.Platform.Windows;

/// <summary>
/// The sound of the end of the work (owner, 29. 10. 2026): Windows' own sounds of the user's sound scheme – "Asterisk" when
/// everything was done, "Exclamation" when something failed. Nothing is played when the scheme has no sound.
/// </summary>
public static partial class FinishSound
{
    private const uint Asterisk = 0x40;
    private const uint Exclamation = 0x30;

    public static void Play(bool problems) => MessageBeep(problems ? Exclamation : Asterisk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);
}
