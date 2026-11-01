using System.Text;
using Universum.Core.MkvToolNix;
using Universum.Core.Text;

namespace Universum.Core.Updates;

/// <summary>A newer MKVToolNix: versions, what is new since the installed one, verified compatibility.</summary>
public sealed record MkvToolNixUpdate(Version? Installed, Version Latest, IReadOnlyList<NewsVersion> News, Compatibility? Compatibility)
{
    public bool IsAvailable => Installed is null || Normalize(Latest) > Normalize(Installed);

    public bool HasSecurityFixes => News.Any(n => n.HasSecurityFixes);

    public string CompatibilityText => Updates.Compatibility.Describe(Compatibility, Latest);

    internal static Version Normalize(Version v) => new(v.Major, Math.Max(v.Minor, 0), Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}

/// <summary>A newer UNIVERSUM release.</summary>
public sealed record UniversumUpdate(Version Current, UniversumRelease Release)
{
    public bool IsAvailable => MkvToolNixUpdate.Normalize(Release.Version) > MkvToolNixUpdate.Normalize(Current);

    /// <summary>
    /// UNIVERSUM installs its own update only when the release is signed with the release key; otherwise it offers the
    /// release page.
    /// </summary>
    public bool CanInstall => ReleaseSignature.IsConfigured && Release is { Installer: not null, Checksums: not null, ChecksumsSignature: not null };
}

/// <summary>What the update check found; problems (e.g. no internet) do not hide the parts that worked.</summary>
public sealed record UpdateReport(MkvToolNixUpdate? MkvToolNix, UniversumUpdate? Universum, IReadOnlyList<string> Problems)
{
    public bool AnyAvailable => MkvToolNix?.IsAvailable == true || Universum?.IsAvailable == true;
}

/// <summary>Reads (bytes of) an address; null when it does not exist (404). Throws on other failures.</summary>
public delegate Task<byte[]?> WebReader(Uri address, CancellationToken cancellationToken);

/// <summary>
/// Checks for updates of MKVToolNix (release feed, NEWS.md, compatibility from the CI) and UNIVERSUM (latest GitHub
/// release). Only these public addresses are asked; nothing about the user is sent.
/// </summary>
public static class UpdateChecker
{
    public static async Task<UpdateReport> CheckAsync(Version? installedMkvToolNix, Version currentUniversum, WebReader read, CancellationToken cancellationToken = default)
    {
        var problems = new List<string>();
        MkvToolNixUpdate? mkvToolNix = null;
        try
        {
            var feed = await read(MkvToolNixDownloads.LatestReleaseFeed, cancellationToken).ConfigureAwait(false)
                       ?? throw new MkvToolNixException("the release feed is missing");
            var latest = MkvToolNixReleaseFeed.ParseLatestVersion(MkvToolNixReleaseFeed.Decode(feed));
            IReadOnlyList<NewsVersion> news = [];
            try
            {
                if (await read(MkvToolNixDownloads.News, cancellationToken).ConfigureAwait(false) is { } markdown)
                {
                    news = MkvToolNixNews.Between(MkvToolNixNews.Parse(Encoding.UTF8.GetString(markdown)), installedMkvToolNix, latest);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add(CoreText.T("Zoznam zmien MKVToolNix sa nepodarilo načítať: ") + ex.Message);
            }

            Compatibility? compatibility = null;
            try
            {
                if (await read(Compatibility.Address, cancellationToken).ConfigureAwait(false) is { } json)
                {
                    compatibility = Compatibility.Parse(Encoding.UTF8.GetString(json));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                problems.Add(CoreText.T("Výsledok testu kompatibility sa nepodarilo načítať: ") + ex.Message);
            }

            mkvToolNix = new MkvToolNixUpdate(installedMkvToolNix, latest, news, compatibility);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problems.Add(CoreText.T("Najnovšiu verziu MKVToolNix sa nepodarilo zistiť: ") + ex.Message);
        }

        UniversumUpdate? universum = null;
        try
        {
            // 404: no release yet
            if (await read(UniversumRelease.LatestApi, cancellationToken).ConfigureAwait(false) is { } json &&
                UniversumRelease.Parse(Encoding.UTF8.GetString(json)) is { } release)
            {
                universum = new UniversumUpdate(currentUniversum, release);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problems.Add(CoreText.T("Najnovšiu verziu UNIVERSUM sa nepodarilo zistiť: ") + ex.Message);
        }

        return new UpdateReport(mkvToolNix, universum, problems);
    }
}

/// <summary>When to check by itself and which offers the user turned down (specification, section 4).</summary>
public static class UpdatePolicy
{
    public static TimeSpan Interval { get; } = TimeSpan.FromHours(24);

    /// <summary>Automatic check at start and then once a day, unless switched off.</summary>
    public static bool ShouldCheck(bool automatic, DateTime? lastCheckUtc, DateTime nowUtc) =>
        automatic && (lastCheckUtc is not { } last || nowUtc - last >= Interval || last > nowUtc);

    /// <summary>
    /// The report is shown by itself unless the user chose "remind me later" (until then) or "skip this version" for every
    /// available update. Security fixes of MKVToolNix are shown even when reminding later.
    /// </summary>
    public static bool ShouldOffer(UpdateReport report, string? skippedMkvToolNix, string? skippedUniversum, DateTime? remindAfterUtc, DateTime nowUtc)
    {
        var mkv = report.MkvToolNix is { IsAvailable: true } m && m.Latest.ToString() != skippedMkvToolNix;
        var own = report.Universum is { IsAvailable: true } u && u.Release.Version.ToString() != skippedUniversum;
        if (!mkv && !own)
        {
            return false;
        }

        return remindAfterUtc is not { } remind || nowUtc >= remind || (mkv && report.MkvToolNix!.HasSecurityFixes);
    }
}
