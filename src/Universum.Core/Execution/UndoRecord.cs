using System.Text.Json;

namespace Universum.Core.Execution;

/// <summary>Original header values of a file edited in place, so the edit can be reverted.</summary>
public sealed record UndoRecord
{
    /// <summary>The edited file (after a move, its place in the library).</summary>
    public required string FilePath { get; init; }

    /// <summary>Where the file was before it was moved into the library; undo moves it back.</summary>
    public string? MovedFrom { get; init; }

    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    public required IReadOnlyList<UndoTrack> Tracks { get; init; }

    /// <summary>Tags of the file before the edit (mkvextract XML); null when the file had none.</summary>
    public string? TagsXmlPath { get; init; }

    /// <summary>False when the file was only moved (its header was not edited).</summary>
    public bool HeaderChanged { get; init; } = true;

    /// <summary>True when the segment title was saved (records from before E4 do not have it).</summary>
    public bool TitleSaved { get; init; }

    /// <summary>Segment title before the edit; null when the file had none.</summary>
    public string? Title { get; init; }

    /// <summary>True when the chapters were saved because the edit replaces them.</summary>
    public bool ChaptersSaved { get; init; }

    /// <summary>Chapters before the edit (mkvextract XML); null when the file had none.</summary>
    public string? ChaptersXmlPath { get; init; }

    /// <summary>Last-modified time of the file before the edit; restored by undo.</summary>
    public DateTime? ModifiedUtc { get; init; }

    public static UndoRecord Load(string path) =>
        JsonSerializer.Deserialize<UndoRecord>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty undo record.");

    /// <summary>Writes the record atomically (a new file renamed over the old one).</summary>
    public void Save(string path)
    {
        var temporary = path + ".new";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, path, overwrite: true);
    }
}

/// <param name="Number">Matroska track number (stable for in-place edits).</param>
/// <param name="Uid">Track UID: undo checks that the file is still the one that was edited.</param>
/// <param name="OtherFlags">Hearing impaired, visual impaired, text descriptions, original and commentary (records from
/// before 0.3.7 do not have them – undo leaves those flags as they are).</param>
public sealed record UndoTrack(
    int Number, string? Name, string Language, string? LanguageIetf, bool Default, bool Enabled, bool Forced, ulong? Uid = null,
    UndoFlags? OtherFlags = null);

/// <summary>The flags beyond default, enabled and forced, as they were before the edit.</summary>
public sealed record UndoFlags(bool HearingImpaired, bool VisualImpaired, bool TextDescriptions, bool Original, bool Commentary);
