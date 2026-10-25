namespace Universum.Core.Planning;

/// <summary>How a target state is written.</summary>
public enum WriteMethod
{
    /// <summary>The file already matches the rules.</summary>
    None,

    /// <summary>Only header values change: mkvpropedit edits the file in place within seconds.</summary>
    HeaderEdit,

    /// <summary>The track order changes: mkvmerge writes a new file.</summary>
    Remux,
}
