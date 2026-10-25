namespace Universum.Core.MkvToolNix;

/// <summary>Programs of the MKVToolNix suite used by UNIVERSUM.</summary>
public enum MkvToolNixTool
{
    MkvMerge,
    MkvPropEdit,
    MkvExtract,
    MkvInfo,
    Gui,
}

public static class MkvToolNixToolNames
{
    /// <summary>File name of the tool for the current operating system ("mkvmerge.exe" on Windows).</summary>
    public static string FileName(MkvToolNixTool tool)
    {
        var name = tool switch
        {
            MkvToolNixTool.MkvMerge => "mkvmerge",
            MkvToolNixTool.MkvPropEdit => "mkvpropedit",
            MkvToolNixTool.MkvExtract => "mkvextract",
            MkvToolNixTool.MkvInfo => "mkvinfo",
            MkvToolNixTool.Gui => "mkvtoolnix-gui",
            _ => throw new ArgumentOutOfRangeException(nameof(tool), tool, null),
        };
        return OperatingSystem.IsWindows() ? name + ".exe" : name;
    }
}
