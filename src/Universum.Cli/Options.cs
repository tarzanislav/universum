using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.Library;
using Universum.Core.Media;

namespace Universum.Cli;

/// <summary>Parsed command line. Errors are reported as <see cref="UsageException"/>.</summary>
public sealed class Options
{
    public required string Command { get; init; }

    public required string Path { get; init; }

    public bool Recursive { get; private set; }

    public string? TemplateFile { get; private set; }

    public OriginalHandling? Original { get; private set; }

    /// <summary>Output folder with MOVIES and TVSHOWS; null = the folder set in the UNIVERSUM settings.</summary>
    public string? OutputDirectory { get; private set; }

    /// <summary>Films or a series; null = guessed from the names.</summary>
    public MediaKind? Kind { get; private set; }

    public string? Title { get; private set; }

    public int? Year { get; private set; }

    public TmdbReference? Tmdb { get; private set; }

    /// <summary>TMDB key for this run (otherwise UNIVERSUM_TMDB_KEY or the key saved in the application).</summary>
    public string? TmdbKey { get; private set; }

    /// <summary>Episode numbers by file name: "Seriál E2.mkv=S01E02".</summary>
    public List<(string File, EpisodeNumber Number)> Episodes { get; } = [];

    /// <summary>Files to process although they wait for review for a reason the user may override (--aj-tak).</summary>
    public List<string> ProcessAnyway { get; } = [];

    /// <summary>The answer to the questions Season 00 or a regular season: specials go into Season 00 (--specialy).</summary>
    public bool SpecialsIntoSeason00 { get; private set; }

    public bool DryRun { get; private set; }

    public string? ReportFile { get; private set; }

    public string? MkvToolNixDirectory { get; private set; }

    /// <summary>Group decisions: "A:audio:2=cs" (group label, track type, position from 1, language).</summary>
    public List<(string Group, SlotKey Slot, LanguageTag Language)> Languages { get; } = [];

    /// <summary>Confirmed forced subtitles: "A:subtitles:1".</summary>
    public List<(string Group, SlotKey Slot)> Forced { get; } = [];

    public bool ConfirmSuggestions { get; private set; }

    public static Options Parse(string[] args)
    {
        if (args.Length < 2 || args[0] is not ("analyze" or "apply"))
        {
            throw new UsageException("Zadajte príkaz analyze alebo apply a priečinok.");
        }

        var options = new Options { Command = args[0], Path = args[1] };
        for (var i = 2; i < args.Length; i++)
        {
            var value = () => i + 1 < args.Length ? args[++i] : throw new UsageException($"Voľba {args[i]} potrebuje hodnotu.");
            switch (args[i])
            {
                case "--recursive" or "-r":
                    options.Recursive = true;
                    break;
                case "--template" or "-t":
                    options.TemplateFile = value();
                    break;
                case "--keep":
                    options.Original = OriginalHandling.Keep;
                    break;
                case "--delete":
                    throw new UsageException("Mazanie originálov je zatiaľ vypnuté – kým program v praxi neodladíme, originály sa vždy zachovajú.");
                case "--output" or "-o" or "--vystup":
                    options.OutputDirectory = value();
                    break;
                case "--film" or "--movie":
                    options.Kind = MediaKind.Movie;
                    break;
                case "--serial" or "--series":
                    options.Kind = MediaKind.Series;
                    break;
                case "--nazov" or "--title":
                    options.Title = value();
                    break;
                case "--rok" or "--year":
                    var year = value();
                    options.Year = int.TryParse(year, out var y) && y is >= 1870 and <= 2200 ? y : throw new UsageException($"Rok {year} nie je platný.");
                    break;
                case "--tmdb":
                    var reference = value();
                    options.Tmdb = TitleGuess.ParseTmdb(reference) ?? throw new UsageException($"TMDB zadajte ako číslo alebo adresu themoviedb.org (je {reference}).");
                    break;
                case "--tmdb-key":
                    options.TmdbKey = value();
                    break;
                case "--epizoda" or "--episode":
                    options.Episodes.Add(ParseEpisode(value()));
                    break;
                case "--specialy" or "--specials":
                    options.SpecialsIntoSeason00 = true;
                    break;
                case "--aj-tak" or "--anyway":
                    options.ProcessAnyway.Add(value());
                    break;
                case "--dry-run":
                    options.DryRun = true;
                    break;
                case "--report":
                    options.ReportFile = value();
                    break;
                case "--mkvtoolnix":
                    options.MkvToolNixDirectory = value();
                    break;
                case "--language" or "-l":
                    options.Languages.Add(ParseLanguage(value()));
                    break;
                case "--forced":
                    var (group, slot) = ParseSlot(value());
                    options.Forced.Add((group, slot));
                    break;
                case "--confirm-suggestions":
                    options.ConfirmSuggestions = true;
                    break;
                default:
                    throw new UsageException($"Neznáma voľba {args[i]}.");
            }
        }

        if (options.Year is not null && options.Title is null)
        {
            throw new UsageException("Rok patrí k názvu: zadajte aj --nazov.");
        }

        // originals are always kept (deleting is switched off until the program is proven in practice)
        options.Original ??= OriginalHandling.Keep;

        return options;
    }

    private static (string, SlotKey, LanguageTag) ParseLanguage(string text)
    {
        var equals = text.LastIndexOf('=');
        if (equals < 0)
        {
            throw new UsageException($"Jazyk zadajte ako SKUPINA:typ:poradie=jazyk, napr. A:audio:2=cs (je {text}).");
        }

        var (group, slot) = ParseSlot(text[..equals]);
        var code = text[(equals + 1)..].Trim();
        if (Core.Media.Languages.Find(code) is null && code.Length is not (2 or 3))
        {
            throw new UsageException($"Neznámy jazyk {code}.");
        }

        return (group, slot, LanguageTag.FromCode(code));
    }

    private static (string, EpisodeNumber) ParseEpisode(string text)
    {
        var equals = text.LastIndexOf('=');
        var number = equals < 0 ? null : EpisodeNumber.TryParse(text[(equals + 1)..]);
        if (number is null)
        {
            throw new UsageException($"Epizódu zadajte ako súbor=S01E04, napr. \"Seriál E2.mkv=S01E02\" (je {text}).");
        }

        return (text[..equals].Trim(), number);
    }

    private static (string, SlotKey) ParseSlot(string text)
    {
        var parts = text.Split(':');
        if (parts.Length != 3 || !int.TryParse(parts[2], out var position) || position < 1)
        {
            throw new UsageException($"Stopu zadajte ako SKUPINA:typ:poradie, napr. A:subtitles:1 (je {text}).");
        }

        var type = parts[1].ToLowerInvariant() switch
        {
            "audio" or "zvuk" or "a" => TrackType.Audio,
            "subtitles" or "titulky" or "s" or "t" => TrackType.Subtitles,
            _ => throw new UsageException($"Typ stopy je audio alebo subtitles (je {parts[1]})."),
        };
        return (parts[0].ToUpperInvariant(), new SlotKey(type, position - 1));
    }
}

public sealed class UsageException(string message) : Exception(message);
