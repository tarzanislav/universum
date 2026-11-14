using System.ComponentModel;
using System.Diagnostics;

namespace Universum.App.Pages;

/// <summary>
/// Opens a page in the user's browser – the pages of titles (TMDB, HISTRA, IMDb, ČSFD), links of notes, the weather, the
/// script of the Zásobník TMDB on this computer. One way for all of them (14. 11. 2026, step 1 of the review of the code):
/// only an address of the web (https, http) is ever handed to Windows – never a file or a program. The self-test only
/// records the addresses.
/// </summary>
public static class Browser
{
    public static Action<Uri> Open { get; set; } = Launch;

    /// <summary>An absolute https or http address.</summary>
    public static bool IsWeb(Uri address) => address.IsAbsoluteUri && (address.Scheme == Uri.UriSchemeHttps || address.Scheme == Uri.UriSchemeHttp);

    private static void Launch(Uri address)
    {
        if (!IsWeb(address))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(address.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // no browser registered: nothing to open (the address is in the tooltip)
        }
    }
}
