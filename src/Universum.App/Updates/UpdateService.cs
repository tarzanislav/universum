using System.Net;
using System.Net.Http;
using Universum.Core.MkvToolNix;
using Universum.Core.Updates;

namespace Universum.App.Updates;

/// <summary>Checks for updates of MKVToolNix and UNIVERSUM (specification, section 4) and remembers the last result.</summary>
public sealed class UpdateService(AppServices services)
{
    private static readonly HttpClient Http = CreateClient();

    /// <summary>The version of this UNIVERSUM.</summary>
    public static Version CurrentVersion => typeof(UpdateService).Assembly.GetName().Version ?? new Version(0, 0);

    public UpdateReport? LastReport { get; private set; }

    /// <summary>Raised after every check.</summary>
    public event Action? Checked;

    public async Task<UpdateReport> CheckAsync(CancellationToken cancellationToken = default)
    {
        var installed = (services.CurrentMkvToolNix ?? await services.FindMkvToolNixAsync(cancellationToken))?.Version;
        var report = await UpdateChecker.CheckAsync(installed, CurrentVersion, ReadAsync, cancellationToken);
        services.Settings.LastUpdateCheckUtc = DateTime.UtcNow;
        services.Settings.Save();
        LastReport = report;
        Checked?.Invoke();
        return report;
    }

    /// <summary>Check by itself at start and then once a day (unless switched off); true when something should be offered.</summary>
    public async Task<bool> CheckIfDueAsync(CancellationToken cancellationToken = default)
    {
        var settings = services.Settings;
        if (!UpdatePolicy.ShouldCheck(settings.AutoCheckUpdates, settings.LastUpdateCheckUtc, DateTime.UtcNow))
        {
            return false;
        }

        var report = await CheckAsync(cancellationToken);
        return UpdatePolicy.ShouldOffer(report, settings.SkippedMkvToolNixVersion, settings.SkippedUniversumVersion, settings.RemindUpdatesAfterUtc, DateTime.UtcNow);
    }

    /// <summary>Reads one of the official addresses (MKVToolNix, GitHub); null for "not found".</summary>
    internal static async Task<byte[]?> ReadAsync(Uri address, CancellationToken cancellationToken)
    {
        RequireOfficial(address);
        using var response = await Http.GetAsync(address, cancellationToken);
        RequireOfficial(response.RequestMessage?.RequestUri ?? address);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static void RequireOfficial(Uri address)
    {
        if (!MkvToolNixDownloads.IsOfficial(address) && !UniversumRelease.IsOfficial(address))
        {
            throw new HttpRequestException($"{address} is not an official address");
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true, MaxAutomaticRedirections = 3 })
        {
            Timeout = TimeSpan.FromSeconds(30),
            MaxResponseContentBufferSize = 16 << 20, // feed, change log, release data
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"UNIVERSUM/{CurrentVersion.ToString(3)}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json, */*");
        return client;
    }
}
