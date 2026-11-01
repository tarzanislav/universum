using System.Collections.Concurrent;
using System.IO;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Localization;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Rules;
using Universum.Core.Speech;

namespace Universum.App.Pages;

/// <summary>
/// Zlučovanie: the language of audio tracks from their speech, and the language of subtitles from their text with
/// forced subtitles told by their amount and name (owner, 1. 11. 2026: as in Hromadné spracovanie). What is found is
/// written into the tracks of the result (the user sees it in the list and may change it); doubts are listed.
/// </summary>
public sealed partial class MuxSettingsViewModel
{
    private CancellationTokenSource? _recognising;
    private double _recognitionProgress;
    private string _recognitionText = "";
    private AsyncRelayCommand? _detectAudioCommand;
    private AsyncRelayCommand? _detectSubtitlesCommand;
    private RelayCommand? _stopRecognitionCommand;

    /// <summary>Rozpoznať jazyk zvuku: the audio tracks taken without a language (all of them when each has one).</summary>
    public AsyncRelayCommand DetectAudioCommand => _detectAudioCommand ??= new AsyncRelayCommand(DetectAudioAsync,
        () => !IsRecognising && Tracks.Any(t => t.Include && t.Kind == TrackType.Audio));

    /// <summary>Rozpoznať jazyk titulkov a Forced: the subtitle tracks taken.</summary>
    public AsyncRelayCommand DetectSubtitlesCommand => _detectSubtitlesCommand ??= new AsyncRelayCommand(DetectSubtitlesAsync,
        () => !IsRecognising && Tracks.Any(t => t.Include && t.Kind == TrackType.Subtitles));

    public RelayCommand StopRecognitionCommand => _stopRecognitionCommand ??= new RelayCommand(() => _recognising?.Cancel(), () => IsRecognising);

    public bool IsRecognising => _recognising is not null;

    /// <summary>How far the recognition is (0–1).</summary>
    public double RecognitionProgress { get => _recognitionProgress; private set => SetProperty(ref _recognitionProgress, value); }

    /// <summary>What the recognition does now ("… – 37 %").</summary>
    public string RecognitionText { get => _recognitionText; private set => SetProperty(ref _recognitionText, value); }

    /// <summary>The speech of the audio tracks heard; each language found written into its track.</summary>
    public async Task DetectAudioAsync()
    {
        var taken = Tracks.Where(t => t.Include && t.Kind == TrackType.Audio).ToList();
        var rows = taken.Where(t => t.Language.Tag.IsUndetermined).ToList() is { Count: > 0 } undetermined ? undetermined : taken;
        if (rows.Count == 0 || !Start())
        {
            return;
        }

        var notes = new List<string>();
        var found = 0;
        var heard = new ConcurrentDictionary<int, SpokenLanguageResult>();
        try
        {
            RecognitionText = Loc.T("Overujem nástroje rozpoznávania reči…");
            var tools = await _services.SpeechAsync(_recognising!.Token);
            RecognitionText = Loc.T("Pripravujem stopy na počúvanie…");
            var tracks = new List<SpeechTrack>(rows.Count);
            foreach (var row in rows)
            {
                var file = row.Input.File;
                var ordinal = file.TracksOf(TrackType.Audio).ToList().FindIndex(t => t.Id == row.Track.Id);
                ordinal = await _services.AudioOrdinalAsync(file.Path, row.Track, Math.Max(0, ordinal), _recognising.Token);
                tracks.Add(new SpeechTrack(file.Path, ordinal, file.Duration));
            }

            var names = rows.Select(r => Loc.F("stopa {0} ({1})", r.Track.Id, r.Input.FileName)).ToList();
            var gauge = new Progress<(int Track, SpeechProgress Step)>(p =>
            {
                RecognitionProgress = p.Step.Fraction;
                RecognitionText = Loc.F("{0} – {1} – {2:0} %", Loc.F("Počúvam {0}/{1}: {2}", p.Track + 1, rows.Count, names[Math.Clamp(p.Track, 0, names.Count - 1)]),
                    Stage(p.Step.Stage), p.Step.Fraction * 100);
            });
            await SpokenLanguage.DetectManyAsync(tools, _services.Runner, tracks, (track, result) => heard[track] = result, gauge, _recognising.Token);
        }
        catch (OperationCanceledException)
        {
            notes.Add(Loc.T("zastavené"));
        }
        catch (SpeechException ex)
        {
            notes.Add(ex.Message);
        }
        finally
        {
            // what was heard (also before a stop) goes into the tracks
            foreach (var (index, result) in heard.OrderBy(h => h.Key))
            {
                var row = rows[index];
                if (result.Language is { } code && Choice(row, code) is { } choice)
                {
                    row.Language = choice;
                    found++;
                    if (result.Doubt is { } doubt)
                    {
                        notes.Add($"{Loc.F("stopa {0} ({1})", row.Track.Id, row.Input.FileName)}: {choice.Display} – {doubt}");
                    }
                }
                else
                {
                    notes.Add($"{Loc.F("stopa {0} ({1})", row.Track.Id, row.Input.FileName)}: {result.Doubt}");
                }
            }

            Finish();
        }

        Result = Loc.F("Jazyk zvuku z reči: {0} z {1} stôp.", found, rows.Count) + (notes.Count > 0 ? " " + string.Join("; ", notes) + "." : "");
    }

    /// <summary>The language of the subtitles read from their text; forced subtitles told by their amount and name.</summary>
    public async Task DetectSubtitlesAsync()
    {
        var rows = Tracks.Where(t => t.Include && t.Kind == TrackType.Subtitles).ToList();
        if (rows.Count == 0 || !Start())
        {
            return;
        }

        var notes = new List<string>();
        int languages = 0, forced = 0;
        try
        {
            string? ffmpeg = null;
            try
            {
                ffmpeg = (await _services.SpeechAsync(_recognising!.Token)).Ffmpeg;
            }
            catch (Exception ex) when (ex is SpeechException or IOException or UnauthorizedAccessException)
            {
                // without FFmpeg the tracks of MKV files are read whole by mkvextract
            }

            var files = rows.GroupBy(r => r.Input).ToList();
            var done = 0;
            foreach (var group in files)
            {
                var file = group.Key.File;
                RecognitionProgress = (double)done / files.Count;
                RecognitionText = Loc.F("Čítam titulky {0}/{1}: {2} – {3:0} %", done + 1, files.Count, group.Key.FileName, RecognitionProgress * 100);
                var text = group.Where(r => r.IsTextSubtitles).ToList();
                IReadOnlyDictionary<int, Core.Text.SubtitleLanguageResult> read = new Dictionary<int, Core.Text.SubtitleLanguageResult>();
                if (text.Count > 0 && _services.CurrentMkvToolNix is { } installation)
                {
                    read = await SubtitleText.DetectAsync(installation, _services.Runner, ffmpeg, file, _recognising!.Token);
                }

                foreach (var row in group)
                {
                    var name = Loc.F("stopa {0} ({1})", row.Track.Id, row.Input.FileName);
                    if (row.IsTextSubtitles && read.TryGetValue(row.Track.Id, out var result) && result.Replaces(row.Language.Tag) &&
                        Choice(row, result.Language) is { } choice && !choice.Same(row.Language.Tag))
                    {
                        row.Language = choice;
                        languages++;
                        if (!result.Certain)
                        {
                            notes.Add($"{name}: {choice.Display} – {result.Detail}");
                        }
                    }
                    else if (row.IsTextSubtitles && !read.ContainsKey(row.Track.Id) && row.Language.Tag.IsUndetermined)
                    {
                        notes.Add(Loc.F("{0}: text sa nedal prečítať", name));
                    }
                    else if (!row.IsTextSubtitles && row.Language.Tag.IsUndetermined)
                    {
                        notes.Add(Loc.F("{0}: obrázkové titulky – jazyk z textu až po OCR v Úprave titulkostopy", name));
                    }

                    // forced: by a word of its name or of its file's name, or by its amount against the fullest track
                    if (!row.IsForced && Forced(row) is { } why)
                    {
                        row.IsForced = true;
                        forced++;
                        notes.Add($"{name}: forced – {why}");
                    }
                }

                done++;
            }
        }
        catch (OperationCanceledException)
        {
            notes.Add(Loc.T("zastavené"));
        }
        finally
        {
            Finish();
        }

        Result = Loc.F("Titulky: jazyk určený pri {0} stopách, forced pri {1}.", languages, forced) + (notes.Count > 0 ? " " + string.Join("; ", notes) + "." : "");
    }

    // why the track is forced: a word of its name or of its file's name, or its amount
    private string? Forced(MuxTrackRow row)
    {
        var keywords = _services.Profile.ForcedKeywords;
        if (Core.Text.Words.ContainsAny(row.Name, keywords) || Core.Text.Words.ContainsAny(row.Track.Name, keywords))
        {
            return Loc.T("podľa názvu stopy");
        }

        if (Core.Text.Words.ContainsAny(Path.GetFileNameWithoutExtension(row.Input.File.Path), keywords))
        {
            return Loc.T("podľa názvu súboru");
        }

        return ForcedSubtitles.ByAmount(row.Input.File, row.Track);
    }

    // the language among the track's choices
    private static LanguageChoice? Choice(MuxTrackRow row, string code)
    {
        var tag = LanguageTag.FromCode(code);
        return row.LanguageChoices.FirstOrDefault(c => c.Same(tag));
    }

    private bool Start()
    {
        if (IsRecognising)
        {
            return false;
        }

        _recognising = new CancellationTokenSource();
        RecognitionProgress = 0;
        RefreshRecognition();
        return true;
    }

    private void Finish()
    {
        _recognising?.Dispose();
        _recognising = null;
        RecognitionProgress = 0;
        RecognitionText = "";
        RefreshRecognition();
    }

    private void RefreshRecognition()
    {
        OnPropertyChanged(nameof(IsRecognising));
        DetectAudioCommand.NotifyCanExecuteChanged();
        DetectSubtitlesCommand.NotifyCanExecuteChanged();
        StopRecognitionCommand.NotifyCanExecuteChanged();
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
