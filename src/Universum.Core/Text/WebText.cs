using System.Text;

namespace Universum.Core.Text;

/// <summary>
/// Text that came from a web service (titles, descriptions, list names): shown to the user, never trusted. Control and
/// formatting characters (also the invisible ones that reverse the reading direction) are removed, runs of white space
/// become one space, and the text is cut to a sensible length.
/// </summary>
public static class WebText
{
    /// <summary>The longest text kept from a web answer (a description of a film is far shorter).</summary>
    public const int MaxLength = 4000;

    /// <summary>The text made safe to show; empty for null.</summary>
    public static string Clean(string? text, int maxLength = MaxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength));
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (IsHidden(c))
            {
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
            if (builder.Length >= maxLength)
            {
                break;
            }
        }

        return builder.ToString();
    }

    // format characters (zero-width, direction overrides and isolates, byte order mark) and private use
    private static bool IsHidden(char c) =>
        char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.PrivateUse
            or System.Globalization.UnicodeCategory.OtherNotAssigned;
}
