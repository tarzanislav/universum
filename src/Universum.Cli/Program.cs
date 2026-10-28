using System.Runtime.Versioning;
using System.Text;
using Universum.Cli;
using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.MkvToolNix;
using Universum.Core.Processes;
using Universum.Core.Templates;
using Universum.Platform.Windows;

Console.OutputEncoding = Encoding.UTF8;
if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
{
    Console.WriteLine(Help.Text);
    return 0;
}

Options options;
try
{
    options = Options.Parse(args);
}
catch (UsageException ex)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine("Pomoc: universum-cli --help");
    return 2;
}

if (OperatingSystem.IsWindows())
{
    RegisterVolumes();
}

var runner = new ProcessRunner();
IEnumerable<IMkvToolNixLocationSource> sources = OperatingSystem.IsWindows() ? [Registry()] : [];
var installation = await new MkvToolNixLocator(runner, sources).FindAsync(options.MkvToolNixDirectory);
if (installation is null)
{
    Console.Error.WriteLine("MKVToolNix sa nenašiel. Zadajte priečinok voľbou --mkvtoolnix.");
    return 2;
}

if (!Directory.Exists(options.Path))
{
    Console.Error.WriteLine($"Priečinok {options.Path} neexistuje.");
    return 2;
}

var outputDirectory = options.OutputDirectory ?? SettingsProfile.OutputDirectory();
if (string.IsNullOrWhiteSpace(outputDirectory))
{
    Console.Error.WriteLine("Výstupný priečinok nie je nastavený: zadajte --output <priečinok> alebo ho nastavte v programe UNIVERSUM.");
    return 2;
}

var layout = new LibraryLayout(Path.GetFullPath(outputDirectory));

var template = BatchTemplate.RulesOnly;
if (options.TemplateFile is not null)
{
    try
    {
        template = BatchTemplate.FromJson(await File.ReadAllTextAsync(options.TemplateFile));
    }
    catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
    {
        Console.Error.WriteLine($"Šablónu sa nepodarilo načítať: {ex.Message}");
        return 2;
    }
}

Console.WriteLine($"MKVToolNix {installation.Version} · {template.Name}");
var cacheFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "UNIVERSUM", "identification-cache.json");
var inspector = new Universum.Core.Media.CachingInspector(new MkvMergeInspector(installation, runner), cacheFile);
var analysis = await BatchAnalyzer.AnalyzeAsync(options.Path, options.Recursive, inspector, excludeDirectories: layout.ResultFolders);
TrySave(inspector);

foreach (var (label, slot, language) in options.Languages)
{
    if (Group(analysis, label) is { } group)
    {
        group.Decisions.Languages[slot] = language;
    }
}

foreach (var (label, slot) in options.Forced)
{
    if (Group(analysis, label) is { } group)
    {
        group.Decisions.Forced[slot] = true;
    }
}

var profile = SettingsProfile.Load();
BatchPlanner.Suggest(analysis, profile);
if (options.ConfirmSuggestions)
{
    foreach (var group in analysis.Groups)
    {
        foreach (var (slot, evidence) in group.Suggestions)
        {
            group.Decisions.Languages.TryAdd(slot, evidence.Language);
        }

        foreach (var slot in group.ForcedSuggestions)
        {
            group.Decisions.Forced.TryAdd(slot, true);
        }
    }
}

var library = LibraryJob.Guess(layout, analysis);
if (options.Kind is { } kind)
{
    library.Kind = kind;
}

// a TMDB address tells the kind: themoviedb.org/tv/… is a series, themoviedb.org/movie/… a film
if (options.Tmdb is { Kind: { } addressKind })
{
    if (options.Kind is { } chosen && chosen != addressKind)
    {
        Console.Error.WriteLine($"Adresa TMDB je {(addressKind == MediaKind.Series ? "seriál (/tv/)" : "film (/movie/)")}, ale zadané je {(chosen == MediaKind.Series ? "--serial" : "--film")}.");
        return 2;
    }

    library.Kind = addressKind;
}

var tmdbKey = options.TmdbKey ?? Environment.GetEnvironmentVariable("UNIVERSUM_TMDB_KEY") ??
              (OperatingSystem.IsWindows() && SettingsProfile.TmdbEnabled() && SettingsProfile.TmdbKeyProtected() is { } stored ? Unprotect(stored) : null);
using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
var titleFromTmdb = false;

var tmdb = string.IsNullOrWhiteSpace(tmdbKey) ? null : new TmdbClient(http, tmdbKey, Path.Combine(Path.GetDirectoryName(cacheFile)!, "tmdb-cache.json"));
if (tmdb is not null)
{
    tmdb.Waiting += delay => Console.WriteLine($"TMDB žiada spomaliť – čakám {Math.Ceiling(delay.TotalSeconds):0} s…");
}

try
{
    if (options.Tmdb is { Kind: null } && !library.KindConfirmed)
    {
        Console.Error.WriteLine("--tmdb s číslom: zadajte aj --film alebo --serial (alebo celú adresu TMDB s /movie/ alebo /tv/).");
        return 2;
    }

    if (options.Tmdb is { } reference && tmdb is not null)
    {
        var tmdbKind = reference.Kind ?? library.Kind;
        if (await tmdb.GetAsync(tmdbKind, reference.Id) is not { } found)
        {
            Console.Error.WriteLine($"TMDB nepozná titul {reference.Id}.");
            return 2;
        }

        library.Kind = tmdbKind;
        var fromTmdb = found.ToTitleInfo() ?? (options.Title is { } english ? new TitleInfo(tmdbKind, english, found.Year, found.Id, true) : null);
        if (fromTmdb is null)
        {
            Console.Error.WriteLine($"TMDB nemá anglický názov titulu {found.Display}: zadajte ho voľbou --nazov.");
            return 2;
        }

        Console.WriteLine($"TMDB: {found.Display} → {fromTmdb.FolderName}");
        SetTitle(library, analysis, fromTmdb);
        if (tmdbKind == MediaKind.Series && found.Seasons.Count > 0)
        {
            library.SeriesOnTmdb = (fromTmdb, found.Seasons);
        }

        titleFromTmdb = true;
    }
    else if (options.Tmdb is not null && options.Title is null)
    {
        Console.Error.WriteLine("Pre --tmdb bez --nazov treba kľúč TMDB (--tmdb-key alebo uložený v programe UNIVERSUM).");
        return 2;
    }
    else if (tmdb is not null && options.Title is null)
    {
        Console.WriteLine($"TMDB: spresnené návrhy názvov: {await RefineSuggestionsAsync(tmdb, library)}");
    }


    tmdb?.SaveCache();
}
catch (TmdbException ex)
{
    Console.Error.WriteLine("TMDB: " + ex.Message);
}

if (options.Title is { } name && !titleFromTmdb)
{
    // "Sága - Časť 2 (2011)" or "Nine Puzzles (2025) [tmdbid-227191]" typed as the name: year and ID are read from it
    var typed = TitleGuess.Typed(library.Kind, name);
    if (options.Year is { } year && typed?.Year is { } typedYear && year != typedYear)
    {
        Console.Error.WriteLine($"--rok {year} sa líši od roku v --nazov ({typedYear}).");
        return 2;
    }

    if (options.Tmdb?.Id is { } tmdbId && typed?.TmdbId is { } typedId && tmdbId != typedId)
    {
        Console.Error.WriteLine($"--tmdb {tmdbId} sa líši od ID v --nazov ({typedId}).");
        return 2;
    }

    var title = new TitleInfo(library.Kind, typed?.Name ?? name, options.Year ?? typed?.Year, options.Tmdb?.Id ?? typed?.TmdbId,
        options.Tmdb is not null || typed?.IncludeId == true);
    if (title.Problem is { } problem)
    {
        Console.Error.WriteLine($"Názov {name}: {problem}.");
        return 2;
    }

    SetTitle(library, analysis, title);
}

// the series' seasons on TMDB, so every episode is checked (specification 13.5): by its ID (folder "[tmdbid-…]", --nazov
// with an ID) or, without one, by the exact name and year
if (tmdb is not null && library is { Kind: MediaKind.Series, Series: { } named, SeriesOnTmdb: null })
{
    try
    {
        var details = named.TmdbId is { } seriesId
            ? await tmdb.GetAsync(MediaKind.Series, seriesId)
            : TmdbClient.ExactMatch(await tmdb.SearchAsync(MediaKind.Series, named.Name, named.Year), named.Name, named.Year) is { } match
                ? await tmdb.GetAsync(MediaKind.Series, match.Id)
                : null;
        if (details is { Seasons.Count: > 0 })
        {
            library.SeriesOnTmdb = (named, details.Seasons);
        }

        if (named.TmdbId is { } id && details?.ToTitleInfo() is { } byTmdb && byTmdb.FolderName != named.FolderName)
        {
            Console.WriteLine($"TMDB: titul {id} je „{byTmdb.FolderName}“, zadaný alebo z priečinka „{named.FolderName}“ – skontrolujte --tmdb alebo --nazov.");
        }

        tmdb.SaveCache();
    }
    catch (TmdbException ex)
    {
        Console.Error.WriteLine("TMDB (sezóny): " + ex.Message);
    }
}

foreach (var (fileName, number) in options.Episodes)
{
    var matches = analysis.Groups.SelectMany(g => g.Files).Where(f =>
        Path.GetFileName(f.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
        Path.GetRelativePath(analysis.Folder, f.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count != 1)
    {
        Console.Error.WriteLine($"Súbor {fileName} pre --epizoda {(matches.Count == 0 ? "sa nenašiel" : "nie je jednoznačný")} – voľba sa preskočí.");
        continue;
    }

    library.Episodes[matches[0].Path] = number;
}

foreach (var fileName in options.ProcessAnyway)
{
    var matches = analysis.Groups.SelectMany(g => g.Files.Select(f => (Group: g, f.Path))).Where(f =>
        Path.GetFileName(f.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase) ||
        Path.GetRelativePath(analysis.Folder, f.Path).Equals(fileName, StringComparison.OrdinalIgnoreCase)).ToList();
    if (matches.Count != 1)
    {
        Console.Error.WriteLine($"Súbor {fileName} pre --aj-tak {(matches.Count == 0 ? "sa nenašiel" : "nie je jednoznačný")} – voľba sa preskočí.");
        continue;
    }

    matches[0].Group.Decisions.ProcessAnyway.Add(matches[0].Path);
}

if (options.SpecialsIntoSeason00)
{
    Console.WriteLine($"Špeciály do Season 00: {library.AnswerSpecials()} súborov");
}

if (options.ConfirmSuggestions)
{
    library.ConfirmSuggestions();
}

var items = BatchPlanner.Plan(analysis, template, profile, library);
Output.Print(analysis, items, library, Path.Combine(Path.GetDirectoryName(cacheFile)!, "journal"));
if (options.Command == "analyze" || options.DryRun)
{
    SaveReport(options.ReportFile, items, []);
    return items.Any(i => i.Status == BatchStatus.NeedsReview) ? 1 : 0;
}

var processor = new FileProcessor(installation, runner, RecycleBin(), Path.Combine(Path.GetDirectoryName(cacheFile)!, "journal"),
    new LibraryIndex(Path.Combine(Path.GetDirectoryName(cacheFile)!, "library-index.json")));
layout.EnsureCreated();
using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancel.Cancel();
};
var results = await BatchRunner.RunAsync(items, processor, new OutputOptions(options.Original!.Value),
    new Progress<BatchProgress>(p =>
    {
        if (p.Result is { } result)
        {
            Console.WriteLine($"[{p.Finished}/{p.Total}] {BatchReport.Describe(p.Item, result)}: {Path.GetFileName(p.Item.File.Path)}" +
                              (result.OutputPath is { } target ? " → " + Path.GetRelativePath(layout.Root, target) : ""));
            foreach (var message in result.Messages)
            {
                Console.WriteLine("        " + message);
            }
        }
    }),
    cancellationToken: cancel.Token);
foreach (var result in results.Where(r => r.Status == ProcessingStatus.Done))
{
    inspector.Invalidate(result.SourcePath);
}

TrySave(inspector);
SaveReport(options.ReportFile, items, results);
var failed = results.Count(r => r.Status is not (ProcessingStatus.Done or ProcessingStatus.AlreadyInLibrary));
Console.WriteLine($"Hotovo: {results.Count - failed}, s chybou alebo zrušené: {failed}, na kontrolu: {items.Count(i => i.Status == BatchStatus.NeedsReview)}");
return failed > 0 || items.Any(i => i.Status == BatchStatus.NeedsReview) ? 1 : 0;

[SupportedOSPlatform("windows")]
static void RegisterVolumes() => WindowsVolumes.Register();

static void SetTitle(LibraryJob library, BatchAnalysis analysis, TitleInfo title)
{
    if (library.Kind == MediaKind.Series)
    {
        library.Series = title;
        return;
    }

    foreach (var file in analysis.Groups.SelectMany(g => g.Files))
    {
        library.Movies[file.Path] = title;
    }
}

// suggestions from the names replaced by TMDB titles where the name (also a translation: a file named in Czech) and the
// year match, as in the program (TitleSearch: the other names of the likely titles are read)
static async Task<string> RefineSuggestionsAsync(TmdbClient tmdb, LibraryJob library)
{
    var search = new TitleSearch(tmdb, null);
    if (library.Kind == MediaKind.Series)
    {
        if (library.Series is null && library.SeriesSuggestion is { } guess &&
            (await search.SearchAsync(MediaKind.Series, guess.Name, guess.Year)).Best(guess.Name, guess.Year) is { } match &&
            match.ToTitleInfo() is { } series)
        {
            library.SeriesSuggestion = series;
            // seasons and episode counts for the check that every episode exists
            if (await tmdb.GetAsync(MediaKind.Series, match.Id) is { Seasons.Count: > 0 } details)
            {
                library.SeriesOnTmdb = (series, details.Seasons);
            }

            return "seriál " + series.FolderName;
        }

        return "žiadne";
    }

    var found = 0;
    foreach (var (path, suggestion) in library.MovieSuggestions.ToList())
    {
        var guess = library.NameGuesses.GetValueOrDefault(path) ?? suggestion;
        if (!library.Movies.ContainsKey(path) &&
            (await search.SearchAsync(MediaKind.Movie, guess.Name, guess.Year)).Best(guess.Name, guess.Year)?.ToTitleInfo() is { } movie)
        {
            library.MovieSuggestions[path] = movie;
            found++;
        }
    }

    return $"{found} z {library.MovieSuggestions.Count}";
}

static string? Unprotect(string stored) => OperatingSystem.IsWindows() ? WindowsUnprotect(stored) : null;

[SupportedOSPlatform("windows")]
static string? WindowsUnprotect(string stored) => UserSecret.Unprotect(stored);

static BatchGroup? Group(BatchAnalysis analysis, string label)
{
    var group = analysis.Groups.FirstOrDefault(g => g.Label == label);
    if (group is null)
    {
        Console.Error.WriteLine($"Skupina {label} neexistuje – voľba sa preskočí.");
    }

    return group;
}

static IRecycleBin RecycleBin() => OperatingSystem.IsWindows() ? WindowsBin() : new NoRecycleBin();

[SupportedOSPlatform("windows")]
static IRecycleBin WindowsBin() => new WindowsRecycleBin();

[SupportedOSPlatform("windows")]
static IMkvToolNixLocationSource Registry() => new RegistryMkvToolNixLocations();

static void SaveReport(string? file, IReadOnlyList<BatchItem> items, IReadOnlyList<ProcessingResult> results)
{
    if (file is null)
    {
        return;
    }

    // a report never overwrites a media file (or anything that is not a report)
    if (!new[] { ".html", ".htm", ".csv" }.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"Správa sa neuložila: {file} – správa musí mať príponu .html alebo .csv.");
        return;
    }

    File.WriteAllText(file, file.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
        ? "﻿" + BatchReport.Csv(items, results)
        : BatchReport.Html("UNIVERSUM – hromadné spracovanie", items, results));
    Console.WriteLine($"Správa: {file}");
}

static void TrySave(Universum.Core.Media.CachingInspector inspector)
{
    try
    {
        inspector.Save();
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        // the cache is only an optimisation
    }
}

/// <summary>Without a Recycle Bin (Linux) deleting originals is refused.</summary>
internal sealed class NoRecycleBin : IRecycleBin
{
    public string? CannotRecycle(string path) => "no Recycle Bin on this system";

    public void MoveToRecycleBin(string path) => throw new IOException("No Recycle Bin on this system.");
}
