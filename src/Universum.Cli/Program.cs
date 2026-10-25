using System.Runtime.Versioning;
using System.Text;
using Universum.Cli;
using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.Library;
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
var analysis = await BatchAnalyzer.AnalyzeAsync(options.Path, options.Recursive, inspector, excludeDirectory: layout.Root);
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

if (options.Title is { } name)
{
    var title = new TitleInfo(library.Kind, name, options.Year, options.Tmdb?.Id, options.Tmdb is not null);
    if (title.Problem is { } problem)
    {
        Console.Error.WriteLine($"Názov {name}: {problem}.");
        return 2;
    }

    if (library.Kind == MediaKind.Series)
    {
        library.Series = title;
    }
    else
    {
        foreach (var file in analysis.Groups.SelectMany(g => g.Files))
        {
            library.Movies[file.Path] = title;
        }
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

if (options.ConfirmSuggestions)
{
    library.ConfirmSuggestions();
}

var items = BatchPlanner.Plan(analysis, template, profile, library);
Output.Print(analysis, items, library);
if (options.Command == "analyze" || options.DryRun)
{
    SaveReport(options.ReportFile, items, []);
    return items.Any(i => i.Status == BatchStatus.NeedsReview) ? 1 : 0;
}

var processor = new FileProcessor(installation, runner, RecycleBin(), Path.Combine(Path.GetDirectoryName(cacheFile)!, "journal"));
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
    public bool CanRecycle(string path) => false;

    public void MoveToRecycleBin(string path) => throw new IOException("No Recycle Bin on this system.");
}
