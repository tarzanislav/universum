using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.Media;
using Universum.Core.Rules;

namespace Universum.App.Pages;

/// <summary>Settings: the rules profile (languages and keywords) and maintenance.</summary>
public sealed class SettingsPageViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _preferredLanguages = "";
    private string _forcedKeywords = "";
    private string _hearingImpairedKeywords = "";
    private string _commentaryKeywords = "";
    private bool _removeTags;
    private bool _repairIndex;
    private int _videoNameLength;
    private string _message = "";

    public SettingsPageViewModel(AppServices services)
    {
        _services = services;
        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(() => Load(RuleProfile.Default));
        ClearCacheCommand = new RelayCommand(ClearCache);
        Load(services.Profile);
    }

    public RelayCommand SaveCommand { get; }

    public RelayCommand ResetCommand { get; }

    public RelayCommand ClearCacheCommand { get; }

    public string PreferredLanguages { get => _preferredLanguages; set => SetProperty(ref _preferredLanguages, value); }

    public string ForcedKeywords { get => _forcedKeywords; set => SetProperty(ref _forcedKeywords, value); }

    public string HearingImpairedKeywords { get => _hearingImpairedKeywords; set => SetProperty(ref _hearingImpairedKeywords, value); }

    public string CommentaryKeywords { get => _commentaryKeywords; set => SetProperty(ref _commentaryKeywords, value); }

    public bool RemoveTags { get => _removeTags; set => SetProperty(ref _removeTags, value); }

    public bool RepairIndex { get => _repairIndex; set => SetProperty(ref _repairIndex, value); }

    public int VideoNameLength { get => _videoNameLength; set => SetProperty(ref _videoNameLength, value); }

    public string Message { get => _message; private set => SetProperty(ref _message, value); }

    public string Folders => $"Nastavenia a šablóny: {AppSettings.Directory}\nZáznamy pre Vrátiť späť: {AppSettings.JournalDirectory}";

    private void Load(RuleProfile profile)
    {
        PreferredLanguages = string.Join(", ", profile.PreferredLanguages);
        ForcedKeywords = string.Join(", ", profile.ForcedKeywords);
        HearingImpairedKeywords = string.Join(", ", profile.HearingImpairedKeywords);
        CommentaryKeywords = string.Join(", ", profile.CommentaryKeywords);
        RemoveTags = profile.RemoveTags;
        RepairIndex = profile.RepairIndex;
        VideoNameLength = profile.VideoNameLength;
        Message = "";
    }

    private void Save()
    {
        var languages = List(PreferredLanguages).Select(code => Languages.Find(code)?.Ietf ?? code.ToLowerInvariant()).Distinct().ToList();
        if (languages.Count == 0)
        {
            Message = "Zadajte aspoň jeden preferovaný jazyk (napr. cs, sk).";
            return;
        }

        if (VideoNameLength is < 4 or > 32)
        {
            Message = "Dĺžka názvu videa musí byť 4 až 32 znakov.";
            return;
        }

        var profile = RuleProfile.Default with
        {
            Name = "Vlastný profil",
            PreferredLanguages = languages,
            ForcedKeywords = List(ForcedKeywords),
            HearingImpairedKeywords = List(HearingImpairedKeywords),
            CommentaryKeywords = List(CommentaryKeywords),
            RemoveTags = RemoveTags,
            RepairIndex = RepairIndex,
            VideoNameLength = VideoNameLength,
        };
        _services.Settings.Profile = IsDefault(profile) ? null : profile;
        _services.Settings.Save();
        Load(_services.Profile);
        Message = "Uložené. Platí pre Pravidlá aj Hromadné spracovanie (pri ďalšom otvorení súboru alebo analýze).";
    }

    private void ClearCache()
    {
        try
        {
            File.Delete(AppSettings.IdentificationCacheFile);
            Message = "Vyrovnávacia pamäť analýzy je vymazaná.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Message = ex.Message;
        }
    }

    private static bool IsDefault(RuleProfile profile)
    {
        var d = RuleProfile.Default;
        return profile.PreferredLanguages.SequenceEqual(d.PreferredLanguages) && profile.ForcedKeywords.SequenceEqual(d.ForcedKeywords) &&
               profile.HearingImpairedKeywords.SequenceEqual(d.HearingImpairedKeywords) && profile.CommentaryKeywords.SequenceEqual(d.CommentaryKeywords) &&
               profile.RemoveTags == d.RemoveTags && profile.RepairIndex == d.RepairIndex && profile.VideoNameLength == d.VideoNameLength;
    }

    private static List<string> List(string text) =>
        text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
