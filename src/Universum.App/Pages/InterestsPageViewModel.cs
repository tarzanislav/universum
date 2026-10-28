using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.Core.News;
using Universum.Core.Text;

namespace Universum.App.Pages;

/// <summary>Which articles Zaujímavosti show: today's, this week's, or all kept ones (the history).</summary>
public enum InterestPeriod
{
    Today,
    Week,
    All,
}

/// <summary>
/// Zaujímavosti (owner, 29. 10. 2026): news, reviews and curiosities of the film world from the sources of the
/// settings – the title, a short summary and the picture of each article, translated by DeepL into the user's language,
/// the whole article opens on its own page. Everything read is kept (the history); the sources are asked once a day
/// at the start or on Obnoviť.
/// </summary>
public sealed class InterestsPageViewModel : ObservableObject
{
    /// <summary>Cards shown at first (more on Zobraziť ďalšie).</summary>
    public const int Page = 60;

    private static readonly Color[] Palette =
    [
        Color.FromRgb(0xF2, 0x8C, 0x7C), Color.FromRgb(0x6F, 0xA8, 0xE8), Color.FromRgb(0x63, 0xC4, 0x8F), Color.FromRgb(0xA7, 0x8B, 0xE6),
        Color.FromRgb(0xF2, 0xB3, 0x4E), Color.FromRgb(0xE8, 0x7E, 0xB0), Color.FromRgb(0x4F, 0xB8, 0xB8), Color.FromRgb(0x9C, 0xC2, 0x5A),
    ];

    private readonly AppServices _services;
    private InterestArchive? _archive;
    private List<InterestCard> _all = [];
    private InterestPeriod _period = InterestPeriod.Week;
    private string _filter = "";
    private string? _source;
    private bool _onlyFavorites;
    private bool _showOriginal;
    private int _shown = Page;
    private string _status = "";
    private string _problems = "";
    private bool _busy;
    private CancellationTokenSource? _work;
    private InterestCard? _featured;

    public InterestsPageViewModel(AppServices services)
    {
        _services = services;
        RefreshCommand = new AsyncRelayCommand(RefreshAsync, () => !Busy);
        StopCommand = new RelayCommand(() => _work?.Cancel(), () => Busy);
        MoreCommand = new RelayCommand(() =>
        {
            _shown += Page;
            Show();
        });
        AllSourcesCommand = new RelayCommand(() => Source = null);
        services.FeedSourcesChanged += () =>
        {
            OnPropertyChanged(nameof(HasSources));
            Build();
        };
        services.TranslationChanged += Build;
        services.Marks.Changed += key =>
        {
            foreach (var card in _all.Where(c => c.MarkKey == key))
            {
                card.MarkChanged();
            }
        };
    }

    public ObservableCollection<InterestCard> Cards { get; } = [];

    /// <summary>The newest article with a picture, shown big on top.</summary>
    public InterestCard? Featured { get => _featured; private set => SetProperty(ref _featured, value); }

    public ObservableCollection<SourceChip> Sources { get; } = [];

    public bool ShowToday
    {
        get => _period == InterestPeriod.Today;
        set => SetPeriod(value, InterestPeriod.Today);
    }

    public bool ShowWeek
    {
        get => _period == InterestPeriod.Week;
        set => SetPeriod(value, InterestPeriod.Week);
    }

    /// <summary>All kept articles (the history).</summary>
    public bool ShowAll
    {
        get => _period == InterestPeriod.All;
        set => SetPeriod(value, InterestPeriod.All);
    }

    /// <summary>Only the articles of one source (null = all).</summary>
    public string? Source
    {
        get => _source;
        set
        {
            if (SetProperty(ref _source, value))
            {
                foreach (var chip in Sources)
                {
                    chip.IsSelected = chip.Name == value;
                }

                _shown = Page;
                Show();
            }
        }
    }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetProperty(ref _filter, value ?? ""))
            {
                _shown = Page;
                Show();
            }
        }
    }

    /// <summary>Only the articles the user marked with a star.</summary>
    public bool OnlyFavorites
    {
        get => _onlyFavorites;
        set
        {
            if (SetProperty(ref _onlyFavorites, value))
            {
                Show();
            }
        }
    }

    /// <summary>The original texts instead of the translations.</summary>
    public bool ShowOriginal
    {
        get => _showOriginal;
        set
        {
            if (SetProperty(ref _showOriginal, value))
            {
                foreach (var card in _all)
                {
                    card.ShowOriginal = value;
                }
            }
        }
    }

    public bool HasSources => _services.FeedSources.Count > 0;

    public bool HasMore { get; private set; }

    public bool IsEmpty => Cards.Count == 0 && Featured is null;

    public string EmptyText =>
        !HasSources ? "Zatiaľ tu nie je žiadny zdroj.\nPridajte ho v Nastaveniach → Zaujímavosti (adresa stránky alebo jej kanála RSS, alebo súbor zdrojov)."
        : Busy ? "Načítavam zaujímavosti…"
        : OnlyFavorites ? "Žiadne obľúbené články – označíte ich hviezdičkou."
        : Filter.Length > 0 ? "Nič nezodpovedá hľadaniu."
        : _period == InterestPeriod.Today ? "Dnes zatiaľ nič nové – pozrite Tento týždeň alebo Všetko."
        : "Žiadne články – skúste Obnoviť.";

    /// <summary>Under the cards: the sources and the translator.</summary>
    public string Attribution =>
        "Zdroje: " + (HasSources ? string.Join(" · ", _services.FeedSources.Where(s => s.Enabled).Select(s => s.Name)) : "žiadne") +
        (_services.Translator is not null ? " · " + DeepLClient.Attribution : " · bez prekladu (kľúč DeepL v Nastaveniach)") +
        ". Celé články sú na stránkach ich autorov.";

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public string Problems
    {
        get => _problems;
        private set
        {
            if (SetProperty(ref _problems, value))
            {
                OnPropertyChanged(nameof(HasProblems));
            }
        }
    }

    public bool HasProblems => Problems.Length > 0;

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                RefreshCommand.NotifyCanExecuteChanged();
                StopCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(EmptyText));
            }
        }
    }

    public AsyncRelayCommand RefreshCommand { get; }

    public RelayCommand StopCommand { get; }

    public RelayCommand MoreCommand { get; }

    public RelayCommand AllSourcesCommand { get; }

    internal AppServices Services => _services;

    /// <summary>The page is shown: the kept articles at once.</summary>
    public void Shown()
    {
        if (_archive is null)
        {
            _archive = _services.Interests.Load();
            if (_archive.ReadAt is { } read)
            {
                Status = $"Uložené zaujímavosti z {read:d. M. yyyy H:mm}.";
            }

            Build();
        }
    }

    /// <summary>Asks the sources, adds what is new and translates the newest articles.</summary>
    public async Task RefreshAsync()
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        var work = _work = new CancellationTokenSource();
        var before = (_archive ?? _services.Interests.Load()).Items.Select(i => i.Link).ToHashSet(StringComparer.Ordinal);
        var progress = new Progress<string>(text =>
        {
            if (Busy)
            {
                Status = text + "…";
            }
        });
        try
        {
            var (archive, problems) = await _services.Interests.RefreshAsync(_services.FeedSources, _services.Translator, _services.InterestLanguage, progress, work.Token);
            _archive = archive;
            Problems = string.Join("\n", problems);
            var added = archive.Items.Count(i => !before.Contains(i.Link));
            Status = $"Aktuálne k {DateTime.Now:d. M. yyyy H:mm}: {Texts.Count(added, "nový článok", "nové články", "nových článkov")}.";
            Build();
        }
        catch (OperationCanceledException)
        {
            Status = "Zastavené.";
        }
        finally
        {
            OnPropertyChanged(nameof(Attribution));
            work.Dispose();
            _work = null;
            Busy = false;
        }
    }

    private void SetPeriod(bool chosen, InterestPeriod period)
    {
        if (!chosen || _period == period)
        {
            return;
        }

        _period = period;
        _shown = Page;
        OnPropertyChanged(nameof(ShowToday));
        OnPropertyChanged(nameof(ShowWeek));
        OnPropertyChanged(nameof(ShowAll));
        Show();
    }

    // the cards of all kept articles and the chips of their sources
    private void Build()
    {
        var language = _services.InterestLanguage;
        var colors = _services.FeedSources.Select(s => s.Name).Concat((_archive?.Items ?? []).Select(i => i.Source)).Distinct(StringComparer.Ordinal)
            .Select((name, i) => (name, color: Palette[i % Palette.Length])).ToDictionary(p => p.name, p => p.color, StringComparer.Ordinal);
        _all = (_archive?.Items ?? []).Select(i => new InterestCard(i, language, colors.GetValueOrDefault(i.Source, Palette[0]), this) { ShowOriginal = ShowOriginal }).ToList();
        Sources.Clear();
        foreach (var group in _all.GroupBy(c => c.Item.Source).OrderBy(g => g.Key, StringComparer.Create(Texts.Slovak, true)))
        {
            Sources.Add(new SourceChip(group.Key, group.Count(), colors.GetValueOrDefault(group.Key, Palette[0]), this) { IsSelected = group.Key == Source });
        }

        Show();
    }

    private void Show()
    {
        var now = DateTimeOffset.Now;
        var wanted = Words.Normalize(Filter);
        var cards = _all.Where(c => _period switch
            {
                InterestPeriod.Today => c.Item.Published.LocalDateTime.Date == now.LocalDateTime.Date || now - c.Item.Published < TimeSpan.FromHours(12),
                InterestPeriod.Week => now - c.Item.Published < TimeSpan.FromDays(7),
                _ => true,
            })
            .Where(c => (Source is null || c.Item.Source == Source) && (!OnlyFavorites || c.IsFavorite) &&
                        (wanted.Length == 0 || c.Search.Contains(wanted, StringComparison.Ordinal)))
            .ToList();
        Featured = cards.FirstOrDefault(c => c.HasImage) ?? cards.FirstOrDefault();
        Cards.Clear();
        foreach (var card in cards.Where(c => c != Featured).Take(_shown))
        {
            Cards.Add(card);
        }

        HasMore = cards.Count - 1 > _shown;
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }
}

/// <summary>A source among the chips above the cards: its name, colour and number of articles; a click shows only it.</summary>
public sealed class SourceChip(string name, int count, Color color, InterestsPageViewModel page) : ObservableObject
{
    private bool _selected;

    public string Name { get; } = name;

    public string Text => $"{Name} · {count}";

    public Brush Accent { get; } = PremiereTabItem.Frozen(color);

    public Brush Tint { get; } = PremiereTabItem.Frozen(Color.FromArgb(0x33, color.R, color.G, color.B));

    public bool IsSelected { get => _selected; set => SetProperty(ref _selected, value); }

    public RelayCommand ChooseCommand => _choose ??= new RelayCommand(() => page.Source = IsSelected ? null : Name);

    private RelayCommand? _choose;
}

/// <summary>An article as a card: the picture, the source, when, the title and the summary (translated when it can be).</summary>
public sealed class InterestCard : ObservableObject
{
    private readonly FeedTranslation? _translation;
    private readonly AppServices _services;
    private bool _showOriginal;
    private ImageSource? _image;
    private bool _asked;

    public InterestCard(FeedItem item, string language, Color color, InterestsPageViewModel page)
    {
        Item = item;
        _services = page.Services;
        _translation = item.Translations.GetValueOrDefault(language);
        MarkKey = "clanok:" + item.Link;
        Accent = PremiereTabItem.Frozen(color);
        Tint = PremiereTabItem.Frozen(Color.FromArgb(0x26, color.R, color.G, color.B));
        Search = Words.Normalize($"{item.Title} {item.Summary} {_translation?.Title} {_translation?.Summary} {item.Source}");
        OpenCommand = new RelayCommand(() =>
        {
            if (Uri.TryCreate(item.Link, UriKind.Absolute, out var link))
            {
                Browser.Open(link);
            }
        });
        ToggleOriginalCommand = new RelayCommand(() => ShowOriginal = !ShowOriginal, () => IsTranslated);
        FavoriteCommand = new RelayCommand(() => _services.Marks.Set(MarkKey, _services.Marks.Get(MarkKey) with { Chosen = !IsFavorite }));
    }

    public FeedItem Item { get; }

    internal string MarkKey { get; }

    internal string Search { get; }

    public Brush Accent { get; }

    public Brush Tint { get; }

    public bool IsTranslated => _translation is not null;

    public bool ShowOriginal
    {
        get => _showOriginal;
        set
        {
            if (SetProperty(ref _showOriginal, value))
            {
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(Summary));
                OnPropertyChanged(nameof(LanguageText));
            }
        }
    }

    public string Title => !ShowOriginal && _translation is { } t ? t.Title : Item.Title;

    public string Summary => !ShowOriginal && _translation is { } t && t.Summary.Length > 0 ? t.Summary : Item.Summary;

    /// <summary>"preložené z EN", "originál EN" or the language.</summary>
    public string LanguageText => IsTranslated ? (ShowOriginal ? $"originál {Item.Language.ToUpperInvariant()}" : $"preložené z {Item.Language.ToUpperInvariant()}") : Item.Language.ToUpperInvariant();

    public string Source => Item.Source;

    /// <summary>"pred 3 h", "včera 14:05", "28. 10. 2026".</summary>
    public string When
    {
        get
        {
            var local = Item.Published.LocalDateTime;
            var ago = DateTime.Now - local;
            return ago < TimeSpan.FromMinutes(60) ? $"pred {Math.Max(1, (int)ago.TotalMinutes)} min"
                : ago < TimeSpan.FromHours(24) && local.Date == DateTime.Today ? $"pred {(int)ago.TotalHours} h"
                : local.Date == DateTime.Today.AddDays(-1) ? $"včera {local:H:mm}"
                : $"{local.Day}. {local.Month}. {local.Year}";
        }
    }

    public bool HasImage => Item.Image is not null && _services.Images is not null;

    /// <summary>The picture, loaded when the card is first shown.</summary>
    public ImageSource? Image
    {
        get
        {
            if (!_asked)
            {
                _asked = true;
                _ = LoadAsync();
            }

            return _image;
        }
    }

    public bool IsFavorite => _services.Marks.Get(MarkKey).Chosen;

    public RelayCommand OpenCommand { get; }

    public RelayCommand ToggleOriginalCommand { get; }

    public RelayCommand FavoriteCommand { get; }

    internal void MarkChanged() => OnPropertyChanged(nameof(IsFavorite));

    private async Task LoadAsync()
    {
        if (Item.Image is not { } address || _services.Images is not { } images || await images.GetAsync(address) is not { } file)
        {
            return;
        }

        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 640;
            bitmap.UriSource = new Uri(file);
            bitmap.EndInit();
            bitmap.Freeze();
            _image = bitmap;
            OnPropertyChanged(nameof(Image));
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or InvalidOperationException or ArgumentException or FileFormatException)
        {
            // a picture WPF cannot read stays the coloured placeholder
        }
    }
}
