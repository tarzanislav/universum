using System.Text.Json;

namespace Universum.Core.Execution;

/// <summary>Original header values of a file edited in place, so the edit can be reverted.</summary>
public sealed record UndoRecord
{
    public required string FilePath { get; init; }

    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;

    public required IReadOnlyList<UndoTrack> Tracks { get; init; }

    /// <summary>Tags of the file before the edit (mkvextract XML); null when the file had none.</summary>
    public string? TagsXmlPath { get; init; }

    public static UndoRecord Load(string path) =>
        JsonSerializer.Deserialize<UndoRecord>(File.ReadAllText(path)) ?? throw new InvalidDataException("Empty undo record.");

    public void Save(string path) => File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

/// <param name="Number">Matroska track number (stable for in-place edits).</param>
public sealed record UndoTrack(int Number, string? Name, string Language, string? LanguageIetf, bool Default, bool Enabled, bool Forced);
