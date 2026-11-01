using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Universum.App.Shell;

/// <summary>One tool in the left rail (like the tool selector in MKVToolNix GUI); without a group it stands on its own.</summary>
public sealed record ToolItem(string Title, string Glyph, object Page, ToolGroup? InGroup = null, ToolBadge? Badge = null)
{
    public ToolGroup Group => InGroup ?? ToolGroup.None;
}

/// <summary>A live count next to a tool in the left rail (POZNÁMKY: today's records, late tasks in red, notes).</summary>
public sealed class ToolBadge : ObservableObject
{
    private string _text = "";
    private bool _alert;
    private string _toolTip = "";

    public string Text { get => _text; private set => SetProperty(ref _text, value); }

    /// <summary>Something is late (red).</summary>
    public bool Alert { get => _alert; private set => SetProperty(ref _alert, value); }

    public string ToolTip { get => _toolTip; private set => SetProperty(ref _toolTip, value); }

    public bool IsShown => Text.Length > 0;

    public void Set(string text, bool alert, string toolTip)
    {
        Text = text;
        Alert = alert;
        ToolTip = toolTip;
        OnPropertyChanged(nameof(IsShown));
    }
}

/// <summary>A group of tools in the left rail: a framed block with its own tint and a heading in its colour.</summary>
public sealed class ToolGroup
{
    public ToolGroup(string title, Color color)
    {
        Title = title;
        Heading = Frozen(color);
        Background = Frozen(Color.FromArgb(0x1F, color.R, color.G, color.B));
        Border = Frozen(Color.FromArgb(0x73, color.R, color.G, color.B));
    }

    private ToolGroup()
    {
        Title = "";
        Heading = Background = Border = Brushes.Transparent;
    }

    /// <summary>Tools on their own (Dashboard): no frame, no heading.</summary>
    public static ToolGroup None { get; } = new();

    public bool IsFramed => Title.Length > 0;

    public string Title { get; }

    /// <summary>The heading text (the full colour, readable in the light and the dark theme).</summary>
    public Brush Heading { get; }

    /// <summary>A light tint of the colour, over the rail's own background in either theme.</summary>
    public Brush Background { get; }

    public Brush Border { get; }

    public override string ToString() => Title;

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
