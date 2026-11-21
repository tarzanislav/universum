using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.App.Localization;
using Universum.App.Templates;
using Universum.Core.Batch;
using Universum.Core.Execution;
using Universum.Core.IO;
using Universum.Core.Library;
using Universum.Core.Library.Tmdb;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Rules;
using Universum.Core.Speech;
using Universum.Core.Templates;
using Universum.Core.Text;
using Universum.Platform.Windows;

namespace Universum.App.Pages;

// BatchPageViewModel: the language of subtitle tracks read from their text, the speech of audio tracks
public sealed partial class BatchPageViewModel
{
    /// <summary>Recognises the language of undetermined text subtitles of the selected group (reads the representative file).</summary>
    // the subtitle reading running in the background (null when none) and its end
    private CancellationTokenSource? _subtitleReading;
    private Task _subtitleReadingDone = Task.CompletedTask;

    /// <summary>Stops the subtitle reading and waits until it has ended (a new analysis, Vymazať frontu).</summary>
    private async Task StopReadingSubtitlesAsync()
    {
        _subtitleReading?.Cancel();
        await _subtitleReadingDone;
    }

    // what was read, by the file (path, size, time): an analysis again or a group shown again reads nothing twice
    private readonly Dictionary<string, IReadOnlyDictionary<int, SubtitleLanguageResult>> _subtitleLanguages = new(Paths.Comparer);

    /// <summary>Reading the subtitles runs (the run waits for it; Zastaviť stops it).</summary>
    public bool IsReadingSubtitles => _subtitleReading is not null;

    /// <summary>
    /// The language of every text subtitle track (owner, 30. 10. 2026): the first file of each group is read – a few parts
    /// of the film by FFmpeg, like the audio (forced subtitles where the index of the file has them) – and the language
    /// found is applied to the group as UNIVERSUM's suggestion (Czech and Slovak by their letters and words).
    /// <paramref name="again"/>: the selected group is read once more (Prečítať titulky znova).
    /// </summary>
    // only: the groups added to the batch (Do Hromadného) – the others keep what was read for them
    private async Task ReadSubtitlesAsync(bool again, IReadOnlyList<BatchGroup>? only = null)
    {
        if (_analysis is not { } analysis || _subtitleReading is not null || _services.CurrentMkvToolNix is not { } installation)
        {
            return;
        }

        var groups = again && SelectedGroup?.Group is { } selected ? [selected] : (only ?? analysis.Groups).ToList();
        groups = groups.Where(g => g.Representative.Tracks.Any(t => t.Type == TrackType.Subtitles && t.IsTextSubtitles)).ToList();
        if (groups.Count == 0)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var done = new TaskCompletionSource();
        _subtitleReading = cancellation;
        _subtitleReadingDone = done.Task;
        OnPropertyChanged(nameof(IsReadingSubtitles));
        RefreshCommands();
        int read = 0, certain = 0, probable = 0;
        try
        {
            string? ffmpeg;
            try
            {
                ffmpeg = (await _services.SpeechAsync(cancellation.Token)).Ffmpeg;
            }
            catch (Exception ex) when (ex is Core.Speech.SpeechException or IOException or UnauthorizedAccessException)
            {
                // without FFmpeg mkvextract reads the whole tracks
                ffmpeg = null;
            }

            foreach (var group in groups)
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var file = group.Representative;
                var key = FileKey(file.Path);
                if (again || !_subtitleLanguages.TryGetValue(key, out var found))
                {
                    RunState = Loc.F("Čítam titulky {0} / {1}: {2}…", ++read, groups.Count, Path.GetFileName(file.Path));
                    found = await SubtitleText.DetectAsync(installation, _services.Runner, ffmpeg, file, cancellation.Token);
                    _subtitleLanguages[key] = found;
                }

                if (!ReferenceEquals(_analysis, analysis))
                {
                    return;
                }

                var slots = SlotKey.Of(file).ToDictionary(x => x.Track.Id, x => x.Key);
                group.Detected.Clear();
                foreach (var (id, result) in found.Where(f => slots.ContainsKey(f.Key)))
                {
                    group.Detected[slots[id]] = SubtitleText.Evidence(result);
                    if (result.Certain)
                    {
                        certain++;
                    }
                    else
                    {
                        probable++;
                    }
                }
            }

            RunState = Loc.F("Titulky prečítané: jazyk {0} je návrh UNIVERSUM", Texts.Count(certain + probable, Loc.T("stopy"), Loc.T("stôp"), Loc.T("stôp"))) +
                       (probable > 0 ? Loc.F(" ({0} len najpravdepodobnejší – overte)", probable) : "") + Loc.T(" – iný jazyk vyberiete pri stope.");
        }
        catch (OperationCanceledException)
        {
            RunState = Loc.T("Čítanie titulkov zastavené – jazyky ostávajú, ako ich má súbor.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Core.Processes.ProcessStartException or TimeoutException or MkvToolNixException)
        {
            RunState = Loc.T("Titulky sa nepodarilo prečítať – jazyky ostávajú, ako ich má súbor: ") + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_subtitleReading, cancellation))
            {
                _subtitleReading = null;
            }

            OnPropertyChanged(nameof(IsReadingSubtitles));
            if (ReferenceEquals(_analysis, analysis))
            {
                Replan();
            }

            RefreshCommands();
            done.TrySetResult();
        }
    }

    private static string FileKey(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{Path.GetFullPath(path)}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return path;
        }
    }

    /// <summary>Which audio tracks "Rozpoznať jazyk audiostôp" listens to.</summary>
    public enum SpeechScope
    {
        /// <summary>The files selected in the list, each on its own.</summary>
        Selected,

        /// <summary>The selected group: its films one by one, a series through one episode.</summary>
        Group,

        /// <summary>Every group of the folder, the same way.</summary>
        All,
    }

    // the speech of the audio tracks without a language: pieces from several places of each film vote; every result is
    // a suggestion with what to check (owner, 28. 10.: the films of a group each on their own, or only chosen films)
    // only: the groups added to the batch (Do Hromadného) – the tracks of the others are not heard again
    private async Task DetectAudioAsync(SpeechScope scope, bool automatic = false, IReadOnlyList<BatchGroup>? only = null)
    {
        if (_analysis is null)
        {
            return;
        }

        var series = _library?.Kind == MediaKind.Series;
        var (targets, known) = scope switch
        {
            SpeechScope.Selected => SpeechTargets.ForFiles(_selectedFiles.Select(f => (f.Item.Group, f.Item.File))),
            SpeechScope.Group when SelectedGroup?.Group is { } group => SpeechTargets.ForGroups([group], series),
            SpeechScope.All => SpeechTargets.ForGroups(only ?? _analysis.Groups, series),
            _ => (Array.Empty<SpeechTarget>(), 0),
        };
        if (automatic && targets.Count == 0)
        {
            // after the analysis: every audio track has its language – the summary of the analysis stays
            return;
        }

        Busy = true;
        var cancellation = _speechCancellation = new CancellationTokenSource();
        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(ShowsProgress));
        OnPropertyChanged(nameof(StopText));
        StopCommand.NotifyCanExecuteChanged();
        int recognised = 0, failed = 0;
        var notes = new List<string>();

        // the results as they come (also the ones decided before a stop) – kept on the window's thread at the end
        var heard = new System.Collections.Concurrent.ConcurrentDictionary<int, SpokenLanguageResult>();
        var applied = new HashSet<int>();
        void Apply()
        {
            foreach (var (index, result) in heard.OrderBy(h => h.Key))
            {
                if (!applied.Add(index))
                {
                    continue;
                }

                var target = targets[index];
                if (result.Language is { } code)
                {
                    SpeechTargets.Keep(target, new LanguageEvidence(LanguageTag.FromCode(code), Confidence.Medium, EvidenceSource.Speech)
                    {
                        Detail = result.Doubt,
                        NeedsOwnConfirmation = result.NeedsOwnConfirmation,
                    });
                    recognised++;
                }
                else
                {
                    failed++;
                    notes.Add($"{Path.GetFileName(target.File.Path)} – {Texts.Slot(target.Key)}: {result.Doubt}");
                }
            }
        }

        try
        {
            if (targets.Count > 0)
            {
                RunState = Loc.T("Overujem nástroje rozpoznávania reči…");
                var tools = await _services.SpeechAsync(cancellation.Token);
                RunState = Loc.T("Pripravujem stopy na počúvanie…");
                var tracks = new List<SpeechTrack>(targets.Count);
                foreach (var target in targets)
                {
                    var ordinal = await _services.AudioOrdinalAsync(target.File.Path, target.Track, target.Key.Ordinal, cancellation.Token);
                    tracks.Add(new SpeechTrack(target.File.Path, ordinal, target.File.Duration));
                }

                // owner, 1. 11. 2026: as fast as possible – all tracks at once (the next one is cut while one is heard, Czech
                // against Slovak for all of them by one run of the accurate model); a gauge 0–100 % with what it does
                var names = targets.Select(t => $"{Path.GetFileName(t.File.Path)} – {Texts.Slot(t.Key)}").ToList();
                var gauge = new Progress<(int Track, SpeechProgress Step)>(p =>
                {
                    Progress = p.Step.Fraction;
                    RunState = Loc.F("{0} – {1} – {2:0} %", Loc.F("Počúvam {0}/{1}: {2}", p.Track + 1, targets.Count, names[Math.Clamp(p.Track, 0, names.Count - 1)]),
                        Stage(p.Step.Stage), p.Step.Fraction * 100);
                });
                await SpokenLanguage.DetectManyAsync(tools, _services.Runner, tracks, (track, result) => heard[track] = result, gauge, cancellation.Token);
                Apply();
            }

            Replan();
            RunState = (targets.Count == 0 && known == 0
                           ? scope == SpeechScope.Selected ? Loc.T("Vyberte v zozname súbory, ktoré sa majú vypočuť.") : Loc.T("Audio: žiadne stopy bez jazyka.")
                           : Texts.Detection(Loc.T("Audio"), recognised, failed, 0,
                               known > 0 ? Loc.F("{0} už jazyk má (z názvu stopy, vašej voľby alebo skoršieho rozpoznania)", known) : null)) +
                       (notes.Count > 0 ? " " + string.Join("; ", notes.Take(3)) + (notes.Count > 3 ? Loc.F(" (a ďalšie: {0})", notes.Count - 3) : "") + "." : "");
        }
        catch (OperationCanceledException)
        {
            Apply();
            Replan();
            RunState = Loc.F("Rozpoznávanie reči zastavené – rozpoznané doteraz: {0} z {1}.", recognised, targets.Count);
        }
        catch (SpeechException ex)
        {
            Apply();
            Replan();
            RunState = ex.Message + (recognised > 0 ? Loc.F(" (rozpoznané doteraz: {0})", recognised) : "");
        }
        finally
        {
            Progress = 0;
            _speechCancellation = null;
            OnPropertyChanged(nameof(IsListening));
            OnPropertyChanged(nameof(ShowsProgress));
            OnPropertyChanged(nameof(StopText));
            cancellation.Dispose();
            Busy = false;
            StopCommand.NotifyCanExecuteChanged();
        }
    }

    // what the recognition of a track does now, for the gauge
    private static string Stage(SpeechStage stage) => stage switch
    {
        SpeechStage.Cutting => Loc.T("vyberám úseky zvuku"),
        SpeechStage.Listening => Loc.T("počúvam úseky (rýchly model)"),
        SpeechStage.CzechOrSlovak => Loc.T("rozlišujem češtinu a slovenčinu (presný model)"),
        _ => Loc.T("hotovo"),
    };
}
