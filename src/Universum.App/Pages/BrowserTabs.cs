using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace Universum.App.Pages;

/// <summary>A window of a browser: the address of its tab in front (when it can be read) and the titles of all its tabs.</summary>
public sealed record BrowserWindow(string? Address, IReadOnlyList<string> TabTitles);

/// <summary>
/// The open browser windows as Windows' accessibility shows them to every program (owner, 8. 11. 2026: Zásobník TMDB –
/// "Z prehliadača"): the address bar of each window (only its tab in front – a browser shows no other address) and the
/// titles of its tabs. Chrome, Edge, Firefox, Brave, Opera, Vivaldi. Only read; nothing is kept but what UNIVERSUM takes
/// from TMDB's pages.
/// </summary>
public static class BrowserTabs
{
    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase) { "chrome", "msedge", "firefox", "brave", "opera", "vivaldi" };

    public static IReadOnlyList<BrowserWindow> Read()
    {
        var windows = new List<BrowserWindow>();
        AutomationElementCollection tops;
        try
        {
            tops = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException)
        {
            return windows;
        }

        foreach (AutomationElement window in tops)
        {
            try
            {
                if (!IsBrowser(window.Current.ProcessId))
                {
                    continue;
                }

                windows.Add(new BrowserWindow(Address(window), TabTitles(window)));
            }
            catch (Exception ex) when (ex is ElementNotAvailableException or COMException or InvalidOperationException or ArgumentException)
            {
                // a window that closed meanwhile, or does not answer: the others are read
            }
        }

        return windows;
    }

    private static bool IsBrowser(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return Browsers.Contains(process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    // the address bar: the first edit field of the window (the toolbar comes before the page)
    private static string? Address(AutomationElement window)
    {
        var edit = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
        return edit?.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) == true ? ((ValuePattern)pattern).Current.Value : null;
    }

    // the tabs of the tab strip (the first list of tabs of the window)
    private static IReadOnlyList<string> TabTitles(AutomationElement window)
    {
        var strip = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Tab));
        if (strip is null)
        {
            return [];
        }

        var tabs = strip.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.TabItem));
        return [.. tabs.Cast<AutomationElement>().Select(t => t.Current.Name).Where(n => !string.IsNullOrWhiteSpace(n))];
    }
}
