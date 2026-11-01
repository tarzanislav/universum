using System.Diagnostics;
using System.Text;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Universum.App.Setup;
using Universum.Core.MkvToolNix;
using Universum.Core.Updates;
using Universum.App.Localization;

namespace Universum.App.Updates;

/// <summary>
/// One window for both updates (specification, section 4): versions, what is new, security fixes, verified
/// compatibility; MKVToolNix is always updated before UNIVERSUM (the UNIVERSUM installer restarts the program).
/// </summary>
public sealed class UpdatesViewModel : ObservableObject
{
    private static readonly string[] MkvToolNixProcesses = ["mkvtoolnix-gui", "mkvmerge", "mkvpropedit", "mkvextract", "mkvinfo"];

    private readonly AppServices _services;
    private readonly UpdateService _updates;
    private readonly Func<string?> _blocker;
    private UpdateReport? _report;
    private string _status = "";
    private double _progress;
    private bool _busy;

    /// <param name="blocker">Why updating is not possible right now (e.g. a running batch); null when it is.</param>
    public UpdatesViewModel(AppServices services, UpdateService updates, Func<string?> blocker)
    {
        _services = services;
        _updates = updates;
        _blocker = blocker;
        _report = updates.LastReport;
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !Busy);
        UpdateAllCommand = new AsyncRelayCommand(() => UpdateAsync(mkvToolNix: true, universum: true), () => !Busy && MkvAvailable && UniversumAvailable);
        UpdateMkvToolNixCommand = new AsyncRelayCommand(() => UpdateAsync(mkvToolNix: true, universum: false), () => !Busy && MkvAvailable);
        UpdateUniversumCommand = new AsyncRelayCommand(() => UpdateAsync(mkvToolNix: false, universum: true), () => !Busy && UniversumAvailable);
        RemindLaterCommand = new RelayCommand(RemindLater, () => !Busy && (MkvAvailable || UniversumAvailable));
        SkipCommand = new RelayCommand(Skip, () => !Busy && (MkvAvailable || UniversumAvailable));
        OpenReleasePageCommand = new RelayCommand(OpenReleasePage, () => _report?.Universum is not null);
    }

    public AsyncRelayCommand CheckCommand { get; }

    public AsyncRelayCommand UpdateAllCommand { get; }

    public AsyncRelayCommand UpdateMkvToolNixCommand { get; }

    public AsyncRelayCommand UpdateUniversumCommand { get; }

    public RelayCommand RemindLaterCommand { get; }

    public RelayCommand SkipCommand { get; }

    public RelayCommand OpenReleasePageCommand { get; }

    /// <summary>Raised when the window should close (reminded, skipped, UNIVERSUM installer started).</summary>
    public event Action? CloseRequested;

    public bool MkvAvailable => _report?.MkvToolNix is { IsAvailable: true };

    public bool UniversumAvailable => _report?.Universum is { IsAvailable: true };

    public string MkvToolNixLine => _report?.MkvToolNix switch
    {
        null => _report is null ? Loc.T("Zisťujem…") : Loc.T("Najnovšiu verziu sa nepodarilo zistiť."),
        { IsAvailable: false } m => Loc.F("Máte najnovšiu verziu {0}.", MkvToolNixDownloads.Format(m.Latest)),
        var m => Loc.F("Nainštalovaná {0} → najnovšia {1} · {2}", m.Installed is { } i ? MkvToolNixDownloads.Format(i) : Loc.T("žiadna"), MkvToolNixDownloads.Format(m.Latest), m.CompatibilityText),
    };

    public bool MkvSecurity => _report?.MkvToolNix is { IsAvailable: true, HasSecurityFixes: true };

    public string MkvNews => _report?.MkvToolNix is { IsAvailable: true } m ? FormatNews(m.News) : "";

    public bool HasMkvNews => MkvNews.Length > 0;

    public bool HasUniversumNotes => UniversumNotes.Length > 0;

    public string UniversumLine => _report switch
    {
        null => Loc.T("Zisťujem…"),
        { Universum: null } => Loc.F("Táto verzia {0} · zatiaľ nie je vydaná novšia verzia.", UpdateService.CurrentVersion.ToString(3)),
        { Universum: { IsAvailable: false } } => Loc.F("Máte najnovšiu verziu {0}.", UpdateService.CurrentVersion.ToString(3)),
        { Universum: var u } => Loc.F("Táto verzia {0} → najnovšia {1}", UpdateService.CurrentVersion.ToString(3), u!.Release.Version.ToString(3)),
    };

    public string UniversumNotes => _report?.Universum is { IsAvailable: true } u ? u.Release.Notes : "";

    public string UniversumInstallHint => _report?.Universum is { IsAvailable: true, CanInstall: false }
        ? Loc.T("Toto vydanie nemá podpis kľúčom vydaní UNIVERSUM, preto sa neinštaluje. Vydania UNIVERSUM sú vždy podpísané – nepodpísané radšej neinštalujte.")
        : "";

    public string Problems => _report is null ? "" : string.Join("\n", _report.Problems);

    public string Status { get => _status; private set => SetProperty(ref _status, value); }

    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public bool Busy
    {
        get => _busy;
        private set
        {
            if (SetProperty(ref _busy, value))
            {
                Refresh();
            }
        }
    }

    public async Task CheckAsync()
    {
        Busy = true;
        Status = Loc.T("Zisťujem dostupné verzie…");
        try
        {
            _report = await _updates.CheckAsync();
            Status = _report.AnyAvailable ? "" : Loc.T("Všetko je aktuálne.");
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>MKVToolNix first, then UNIVERSUM (its installer restarts the program).</summary>
    private async Task UpdateAsync(bool mkvToolNix, bool universum)
    {
        if (_blocker() is { } reason)
        {
            Status = reason;
            return;
        }

        if (mkvToolNix && RunningMkvToolNix() is { } running)
        {
            Status = Loc.F("Zatvorte najprv {0} – inak sa MKVToolNix nedá aktualizovať.", running);
            return;
        }

        Busy = true;
        try
        {
            if (mkvToolNix && _report?.MkvToolNix is { IsAvailable: true } mkv && !await UpdateMkvToolNixAsync(mkv.Latest))
            {
                return;
            }

            if (universum && _report?.Universum is { IsAvailable: true } own)
            {
                if (!own.CanInstall)
                {
                    OpenReleasePage();
                    Status = Loc.T("Stránka vydania UNIVERSUM je otvorená v prehliadači.");
                    return;
                }

                Status = Loc.F("Sťahujem a overujem UNIVERSUM {0}…", own.Release.Version.ToString(3));
                var installer = await UniversumInstaller.DownloadAsync(own.Release, new Progress<double>(p => Progress = p));
                Status = Loc.T("Spúšťam inštalátor UNIVERSUM – program sa zatvorí a po inštalácii sa spustí znova.");
                if (UniversumInstaller.Start(installer))
                {
                    CloseRequested?.Invoke();
                    Application.Current.Shutdown();
                }
                else
                {
                    Status = Loc.T("Inštalácia UNIVERSUM bola zrušená.");
                }
            }
        }
        catch (SetupException ex)
        {
            Status = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task<bool> UpdateMkvToolNixAsync(Version latest)
    {
        Status = Loc.F("Sťahujem a overujem MKVToolNix {0}…", MkvToolNixDownloads.Format(latest));
        Progress = 0;
        var installer = await MkvToolNixInstaller.DownloadAsync(latest, new Progress<double>(p => Progress = p));
        Status = Loc.T("Inštalujem MKVToolNix (Windows sa môže opýtať na povolenie)…");
        if (!await MkvToolNixInstaller.RunAsync(installer, silent: true))
        {
            Status = Loc.T("Inštalácia MKVToolNix bola zrušená.");
            return false;
        }

        var installation = await _services.FindMkvToolNixAsync();
        if (installation is null || installation.Version < latest)
        {
            Status = Loc.F("MKVToolNix {0} sa nenašiel ani po inštalácii – skontrolujte ho tlačidlom MKVToolNix dole.", MkvToolNixDownloads.Format(latest));
            return false;
        }

        Status = Loc.F("MKVToolNix {0} je nainštalovaný.", MkvToolNixDownloads.Format(installation.Version));
        _report = _report! with { MkvToolNix = _report.MkvToolNix! with { Installed = installation.Version } };
        Refresh();
        return true;
    }

    private void RemindLater()
    {
        _services.Settings.RemindUpdatesAfterUtc = DateTime.UtcNow.AddDays(3);
        _services.Settings.Save();
        CloseRequested?.Invoke();
    }

    private void Skip()
    {
        if (_report?.MkvToolNix is { IsAvailable: true } mkv)
        {
            _services.Settings.SkippedMkvToolNixVersion = mkv.Latest.ToString();
        }

        if (_report?.Universum is { IsAvailable: true } own)
        {
            _services.Settings.SkippedUniversumVersion = own.Release.Version.ToString();
        }

        _services.Settings.Save();
        CloseRequested?.Invoke();
    }

    private void OpenReleasePage()
    {
        if (_report?.Universum?.Release.Page is { } page && UniversumRelease.IsOfficial(page))
        {
            Process.Start(new ProcessStartInfo(page.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
    }

    private static string? RunningMkvToolNix()
    {
        foreach (var name in MkvToolNixProcesses)
        {
            var processes = Process.GetProcessesByName(name);
            var running = processes.Length > 0;
            foreach (var process in processes)
            {
                process.Dispose();
            }

            if (running)
            {
                return name == "mkvtoolnix-gui" ? "MKVToolNix GUI" : name;
            }
        }

        return null;
    }

    private static string FormatNews(IReadOnlyList<NewsVersion> news)
    {
        var text = new StringBuilder();
        foreach (var version in news)
        {
            text.Append(version.Heading).AppendLine(version.HasSecurityFixes ? Loc.T("   ⚠ BEZPEČNOSTNÉ OPRAVY") : "");
            foreach (var section in version.Sections)
            {
                text.Append("  ").AppendLine(section.IsSecurity ? section.Title.ToUpperInvariant() : section.Title);
                foreach (var line in section.Text.Split('\n').Where(l => l.Trim().Length > 0))
                {
                    text.Append("    ").AppendLine(line.Trim());
                }
            }

            text.AppendLine();
        }

        return text.ToString().TrimEnd();
    }

    private void Refresh()
    {
        OnPropertyChanged(string.Empty);
        foreach (var command in new IRelayCommand[] { CheckCommand, UpdateAllCommand, UpdateMkvToolNixCommand, UpdateUniversumCommand, RemindLaterCommand, SkipCommand, OpenReleasePageCommand })
        {
            command.NotifyCanExecuteChanged();
        }
    }
}
