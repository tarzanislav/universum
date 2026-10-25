namespace Universum.App.Pages;

/// <summary>A value with its Slovak text for combo boxes.</summary>
public sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}
