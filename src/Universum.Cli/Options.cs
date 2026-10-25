using Universum.Core.Batch;
using Universum.Core.Execution;
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

    public string? TargetDirectory { get; private set; }

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
                    options.Original = OriginalHandling.Delete;
                    break;
                case "--target":
                    options.TargetDirectory = value();
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

        if (options.Command == "apply" && !options.DryRun && options.Original is null)
        {
            throw new UsageException("Pri apply zvoľte --keep (originál zachovať) alebo --delete (originál do Koša), prípadne --dry-run.");
        }

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
