namespace Universum.App.Shell;

/// <summary>One tool in the left rail (like the tool selector in MKVToolNix GUI).</summary>
public sealed record ToolItem(string Title, string Glyph, object Page);
