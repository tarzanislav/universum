using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Universum.Core.News;

namespace Universum.App.Pages;

/// <summary>
/// Nastavenia → Zaujímavosti: the sources (a page or its RSS/Atom feed, added by address or from a file – addresses
/// live only in the settings), the DeepL key for translations (protected for this Windows account), the language,
/// pictures, and forgetting the kept articles.
/// </summary>
public sealed class InterestSourcesViewModel : ObservableObject
{
    private readonly AppServices _services;
    private string _status = "";
    private string _newName = "";
    private string _newUrl = "";
    private string _newLanguage = "en";
    private string _keyInput = "";
    private string _keyStatus = "";

    public InterestSourcesViewModel(AppServices services)
    {
        _services = services;
        AddCommand = new RelayCommand(Add, () => NewName.Trim().Length > 0 && NewUrl.Trim().Length > 0);
        ImportCommand = new RelayCommand(Import);
        SaveKeyCommand = new AsyncRelayCommand(SaveKeyAsync, () => KeyInput.Trim().Length > 0);
        RemoveKeyCommand = new RelayCommand(() =>
        {
            _services.SetDeepLKey(null);
            _keyStatus = "";
            Refresh();
        }, () => _services.DeepLKey is not null);
        ClearCommand = new RelayCommand(() =>
        {
            if (Confirm("Vymazať všetky uložené články Zaujímavostí (aj históriu)?\n\nObľúbené značky ostanú. Zdroje sa pri ďalšej aktualizácii načítajú znova."))
            {
                new InterestStore(AppSettings.InterestsFile).Clear();
                Status = "Uložené články sú vymazané.";
            }
        });
        Reload();
    }

    public ObservableCollection<InterestSourceRow> Rows { get; } = [];

    public IReadOnlyList<Choice<string>> Languages { get; } =
        [new("en", "angličtina"), new("cs", "čeština"), new("sk", "slovenčina"), new("de", "nemčina"), new("fr", "francúzština")];

    public IReadOnlyList<Choice<string>> Targets { get; } = [new("sk", "slovenčiny"), new("cs", "češtiny"), new("en", "angličtiny")];

    public string NewName { get => _newName; set { if (SetProperty(ref _newName, value ?? "")) { AddCommand.NotifyCanExecuteChanged(); } } }

    public string NewUrl { get => _newUrl; set { if (SetProperty(ref _newUrl, value ?? "")) { AddCommand.NotifyCanExecuteChanged(); } } }

    public string NewLanguage { get => _newLanguage; set => SetProperty(ref _newLanguage, value ?? "en"); }

    /// <summary>The language articles are translated into.</summary>
    public string Target
    {
        get => _services.InterestLanguage;
        set
        {
            if (value is not null && value != _services.InterestLanguage)
            {
                _services.SetInterestLanguage(value);
                OnPropertyChanged();
            }
        }
    }

    public bool Images
    {
        get => _services.Settings.InterestImages;
        set
        {
            _services.Settings.InterestImages = value;
            _services.Settings.Save();
            OnPropertyChanged();
        }
    }

    public string KeyInput { get => _keyInput; set { if (SetProperty(ref _keyInput, value ?? "")) { SaveKeyCommand.NotifyCanExecuteChanged(); } } }

    public string KeyStatus => _keyStatus.Length > 0 ? _keyStatus
        : _services.DeepLKey is null ? "Kľúč nie je zadaný – články sa ukazujú v origináli. Bezplatný kľúč: DeepL API Free (500 000 znakov mesačne)."
        : $"Kľúč je uložený (chránený pre vaše konto Windows). {Usage}";

    /// <summary>The characters translated this month.</summary>
    public string Usage
    {
        get
        {
            var month = DateTime.Now.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            var used = new InterestStore(AppSettings.InterestsFile).Load().Translated.GetValueOrDefault(month);
            return $"Tento mesiac preložené: {used.ToString("N0", Texts.Slovak)} z {Interests.MonthBudget.ToString("N0", Texts.Slovak)} znakov.";
        }
    }

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public RelayCommand AddCommand { get; }

    public RelayCommand ImportCommand { get; }

    public AsyncRelayCommand SaveKeyCommand { get; }

    public RelayCommand RemoveKeyCommand { get; }

    public RelayCommand ClearCommand { get; }

    /// <summary>Asks the user (the self-test answers itself).</summary>
    public Func<string, bool> Confirm { get; set; } = question =>
        System.Windows.MessageBox.Show(question, "UNIVERSUM", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question) == System.Windows.MessageBoxResult.Yes;

    /// <summary>Adds the sources of a file (or replaces those of the same name); false with the reason in <see cref="Status"/>.</summary>
    public bool ImportFile(string path)
    {
        try
        {
            var added = FeedSource.Load(File.ReadAllText(path));
            var names = added.Select(a => a.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            _services.SetFeedSources(_services.FeedSources.Where(s => !names.Contains(s.Name)).Concat(added));
            Reload();
            Status = $"Pridané zdroje: {added.Count}. Načítajú sa pri ďalšej aktualizácii (alebo tlačidlom Obnoviť v Zaujímavostiach).";
            return true;
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Status = "Zdroje sa nepridali: " + ex.Message;
            return false;
        }
    }

    /// <summary>Adds the source typed in (a page or its feed, https only).</summary>
    public bool AddSource(string name, string url, string language)
    {
        var source = new FeedSource { Name = name.Trim(), Url = url.Trim(), Language = language };
        if (source.Address is null)
        {
            Status = "Adresa musí začínať https:// (napr. https://stránka/feed/).";
            return false;
        }

        _services.SetFeedSources(_services.FeedSources.Where(s => !string.Equals(s.Name, source.Name, StringComparison.OrdinalIgnoreCase)).Append(source));
        Reload();
        Status = $"Zdroj „{source.Name}“ je pridaný.";
        return true;
    }

    internal void SetEnabled(InterestSourceRow row, bool enabled)
    {
        _services.SetFeedSources(_services.FeedSources.Select(s => s.Name == row.Name ? s with { Enabled = enabled } : s));
        Status = enabled ? $"Zdroj „{row.Name}“ je zapnutý." : $"Zdroj „{row.Name}“ je vypnutý.";
    }

    internal void Remove(InterestSourceRow row)
    {
        _services.SetFeedSources(_services.FeedSources.Where(s => s.Name != row.Name));
        Reload();
        Status = $"Zdroj „{row.Name}“ je odstránený (jeho uložené články ostali).";
    }

    private void Add()
    {
        if (AddSource(NewName, NewUrl, NewLanguage))
        {
            NewName = "";
            NewUrl = "";
        }
    }

    private void Import()
    {
        var dialog = new OpenFileDialog { Title = "Zdroje Zaujímavostí", Filter = "Zdroje (*.json)|*.json" };
        if (dialog.ShowDialog() == true)
        {
            ImportFile(dialog.FileName);
        }
    }

    private async Task SaveKeyAsync()
    {
        var key = KeyInput.Trim();
        if (!DeepLClient.IsValidKey(key))
        {
            _keyStatus = "Toto nevyzerá ako kľúč DeepL.";
            Refresh();
            return;
        }

        try
        {
            using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            await new DeepLClient(http, key).TranslateAsync(["film"], _services.InterestLanguage);
            _services.SetDeepLKey(key);
            KeyInput = "";
            _keyStatus = "";
        }
        catch (TranslationException ex)
        {
            _keyStatus = ex.Message;
        }

        Refresh();
    }

    private void Refresh()
    {
        OnPropertyChanged(nameof(KeyStatus));
        RemoveKeyCommand.NotifyCanExecuteChanged();
    }

    private void Reload()
    {
        Rows.Clear();
        foreach (var source in _services.FeedSources)
        {
            Rows.Add(new InterestSourceRow(source, this));
        }
    }
}

/// <summary>A source of Zaujímavosti in the settings.</summary>
public sealed class InterestSourceRow(FeedSource source, InterestSourcesViewModel owner) : ObservableObject
{
    private bool _enabled = source.Enabled;

    public string Name => source.Name;

    public string Address => source.Address?.Host ?? source.Url;

    public string Language => source.Language.ToUpperInvariant();

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                owner.SetEnabled(this, value);
            }
        }
    }

    public RelayCommand RemoveCommand => _remove ??= new RelayCommand(() => owner.Remove(this));

    private RelayCommand? _remove;
}
