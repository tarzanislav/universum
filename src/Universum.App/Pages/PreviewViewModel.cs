using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using Universum.Core.Media;
using Universum.Core.MkvToolNix;
using Universum.Core.Playback;
using Universum.Core.Processes;
using Universum.Core.Subtitles.Pictures;

namespace Universum.App.Pages;

/// <summary>
/// A preview of a merged result in UNIVERSUM's player (owner, 1. 11. 2026): the picture, the sound tracks to switch and the
/// subtitles of the result – text and pictures (PGS) – drawn as the result will show them.
/// </summary>
public sealed class PreviewViewModel : ObservableObject
{
    private static readonly VideoChoice<int?> NoSubtitles = new(null, "bez titulkov");
    private readonly AppServices _services;
    private readonly string _path;
    private MediaFile? _file;
    private VideoChoice<int?> _subtitle = NoSubtitles;
    private string _status = "Otváram náhľad…";
    private int _loading;

    public PreviewViewModel(AppServices services, string path)
    {
        _services = services;
        _path = path;
        Player = new Player.PlayerViewModel(services);
        Title = Path.GetFileName(path);
    }

    public Player.PlayerViewModel Player { get; }

    public string Title { get; }

    public IReadOnlyList<VideoChoice<int?>> Subtitles { get; private set; } = [NoSubtitles];

    /// <summary>The subtitle track shown (read from the result when chosen).</summary>
    public VideoChoice<int?> Subtitle
    {
        get => _subtitle;
        set
        {
            if (value is not null && SetProperty(ref _subtitle, value))
            {
                _ = ShowAsync(value.Value);
            }
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public async Task OpenAsync()
    {
        var installation = _services.CurrentMkvToolNix ?? await _services.FindMkvToolNixAsync();
        if (installation is null)
        {
            Status = "MKVToolNix nie je k dispozícii.";
            return;
        }

        try
        {
            _file = await new MkvMergeInspector(installation, _services.Runner).InspectAsync(_path);
            await Player.OpenAsync(_file);
            Subtitles = [NoSubtitles, .. _file.TracksOf(TrackType.Subtitles).Select((t, i) =>
                new VideoChoice<int?>(t.Id, $"{i + 1}: {Texts.Language(t.Language)}{(t.Name is { Length: > 0 } name ? " – " + name : "")}{(t.Flags.Forced ? " (forced)" : "")}"))];
            OnPropertyChanged(nameof(Subtitles));
            Status = $"{_file.TracksOf(TrackType.Audio).Count()} zvuk(y), {_file.TracksOf(TrackType.Subtitles).Count()} titulky – zvuk prepnete pod obrazom, titulky vpravo hore.";
            // the subtitles a player shows by themselves: the default (or forced) track
            if (_file.TracksOf(TrackType.Subtitles).FirstOrDefault(t => t.Flags.Default) ?? _file.TracksOf(TrackType.Subtitles).FirstOrDefault(t => t.Flags.Forced) is { } shown)
            {
                Subtitle = Subtitles.First(s => s.Value == shown.Id);
            }
        }
        catch (Exception ex) when (ex is MkvToolNixException or IOException or ProcessStartException or TimeoutException or Core.Speech.SpeechException)
        {
            Status = "Náhľad sa nedá otvoriť: " + ex.Message;
        }
    }

    public Task CloseAsync() => Player.CloseAsync();

    private async Task ShowAsync(int? trackId)
    {
        var loading = ++_loading;
        Player.SetSubtitles(SubtitleTimeline.Empty, SubtitleTimeline.Empty);
        Player.SetPictures(PictureTimeline.Empty, PictureTimeline.Empty);
        if (trackId is not { } id || _file?.Tracks.FirstOrDefault(t => t.Id == id) is not { } track || _services.CurrentMkvToolNix is not { } installation)
        {
            return;
        }

        if (track.IsTextSubtitles)
        {
            var document = await SubtitleText.ExtractDocumentAsync(installation, _services.Runner, _file, track);
            if (loading == _loading && document is not null)
            {
                Player.SetSubtitles(new SubtitleTimeline(document.Cues), SubtitleTimeline.Empty);
            }
        }
        else
        {
            var pictures = await PictureSubtitles.ExtractAsync(installation, _services.Runner, _file, track);
            if (loading == _loading)
            {
                Player.SetPictures(new PictureTimeline(pictures), PictureTimeline.Empty);
            }
        }
    }
}
