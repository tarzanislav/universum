using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Universum.Core.MkvToolNix;
using Universum.Core.Updates;

namespace Universum.Tests;

/// <summary>Update check (specification, section 4) against invented answers in the real formats.</summary>
public sealed class UpdateTests
{
    // invented change log in the NEWS.md layout of MKVToolNix
    private const string News = """
        # Version 105.0 "Invented Three" 2026-12-20

        ## New features and enhancements

        * mkvmerge: an invented improvement.

        ## Security fixes

        * mkvextract: an invented out-of-bounds read was fixed (CVE-2026-00000).

        # Version 104.0 "Invented Two" 2026-11-15

        ## Bug fixes

        * mkvpropedit: an invented bug was fixed.

        # Version 103.0 "Invented One" 2026-10-18

        ## Bug fixes

        * an invented fix.
        """;

    private const string Release = """
        {"tag_name":"v0.3.0","name":"UNIVERSUM 0.3.0","draft":false,"prerelease":false,
         "html_url":"https://github.com/tarzanislav/universum/releases/tag/v0.3.0","body":"Čo je nové: knižnica.",
         "assets":[
           {"name":"UNIVERSUM_Setup_0.3.0.exe","browser_download_url":"https://github.com/tarzanislav/universum/releases/download/v0.3.0/UNIVERSUM_Setup_0.3.0.exe"},
           {"name":"SHA256SUMS.txt","browser_download_url":"https://github.com/tarzanislav/universum/releases/download/v0.3.0/SHA256SUMS.txt"},
           {"name":"SHA256SUMS.txt.sig","browser_download_url":"https://github.com/tarzanislav/universum/releases/download/v0.3.0/SHA256SUMS.txt.sig"},
           {"name":"evil.exe","browser_download_url":"https://example.com/evil.exe"}]}
        """;

    [Test]
    public void NewsBetweenVersions()
    {
        var news = MkvToolNixNews.Parse(News);
        Assert.Sequence(["105.0", "104.0", "103.0"], news.Select(n => MkvToolNixDownloads.Format(n.Version)), "versions");
        var newer = MkvToolNixNews.Between(news, new Version(103, 0), new Version(105, 0));
        Assert.Sequence(["105.0", "104.0"], newer.Select(n => MkvToolNixDownloads.Format(n.Version)), "newer than installed, newest first");
        Assert.True(newer[0].HasSecurityFixes && !newer[1].HasSecurityFixes, "security fixes found");
        Assert.Equal("105.0 „Invented Three“ (20. 12. 2026)", newer[0].Heading);
        Assert.Equal("* mkvpropedit: an invented bug was fixed.", newer[1].Sections.Single().Text);
    }

    [Test]
    public void ReleaseFromGitHub()
    {
        var release = UniversumRelease.Parse(Release)!;
        Assert.Equal(new Version(0, 3, 0), release.Version);
        Assert.Equal("UNIVERSUM_Setup_0.3.0.exe", Path.GetFileName(release.Installer!.AbsolutePath));
        Assert.True(release.Checksums is not null && release.ChecksumsSignature is not null, "checksums and signature");
        Assert.Equal<UniversumRelease?>(null, UniversumRelease.Parse(Release.Replace("\"prerelease\":false", "\"prerelease\":true")), "pre-releases are not offered");
        Assert.True(!UniversumRelease.IsOfficial(new Uri("http://github.com/x")) && !UniversumRelease.IsOfficial(new Uri("https://example.com/x")), "only GitHub over HTTPS");
        var foreign = UniversumRelease.Parse(Release.Replace("tarzanislav/universum/releases/download/v0.3.0/UNIVERSUM_Setup", "someone/else/releases/download/v0.3.0/UNIVERSUM_Setup"))!;
        Assert.Equal<Uri?>(null, foreign.Installer, "downloads only from this repository");
        Assert.Equal<UniversumRelease?>(null, UniversumRelease.Parse(Release.Replace("\"tag_name\":\"v0.3.0\"", "\"tag_name\":\"v0.3\"")), "releases are numbered 1.2.3");
        Assert.Equal<UniversumRelease?>(null, UniversumRelease.Parse(Release.Replace("https://github.com/tarzanislav/universum/releases/tag", "https://github.com/someone/else/releases/tag")), "release page of this repository only");
    }

    [Test]
    public void SignedChecksumListIsRequired()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
        var version = new Version(0, 3, 0);
        var sums = Encoding.UTF8.GetBytes("# UNIVERSUM 0.3.0\n" + new string('A', 64) + "  UNIVERSUM_Setup_0.3.0.exe\n");
        var signature = key.SignData(sums, HashAlgorithmName.SHA256);

        Assert.Equal(new string('A', 64), ReleaseSignature.ExpectedInstallerHash(sums, signature, version, publicKey));
        Assert.Equal<string?>(null, ReleaseSignature.ExpectedInstallerHash(sums, signature, new Version(0, 4, 0), publicKey), "an older signed list passed off as a newer release");
        Assert.True(ReleaseSignature.Verify(sums, key.SignData(sums, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence), publicKey), "DER signature");

        var changed = (byte[])sums.Clone();
        changed[20] = (byte)'B';
        Assert.Equal<string?>(null, ReleaseSignature.ExpectedInstallerHash(changed, signature, version, publicKey), "changed list refused");
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal<string?>(null, ReleaseSignature.ExpectedInstallerHash(sums, other.SignData(sums, HashAlgorithmName.SHA256), version, publicKey), "other key refused");
        Assert.Equal<string?>(null, ReleaseSignature.ExpectedInstallerHash(sums, signature, version, ""), "no key: nothing installs");
    }

    [Test]
    public async Task CheckFindsBothUpdates()
    {
        var answers = new Dictionary<Uri, byte[]?>
        {
            [MkvToolNixDownloads.LatestReleaseFeed] = Gzip("<mkvtoolnix-releases><latest-source><version>105.0</version></latest-source></mkvtoolnix-releases>"),
            [MkvToolNixDownloads.News] = Encoding.UTF8.GetBytes(News),
            [Compatibility.Address] = Encoding.UTF8.GetBytes(new Compatibility(new Version(105, 0), "0.3.0", new DateTime(2026, 12, 21, 3, 0, 0, DateTimeKind.Utc), true).ToJson()),
            [UniversumRelease.LatestApi] = Encoding.UTF8.GetBytes(Release),
        };
        var report = await UpdateChecker.CheckAsync(new Version(103, 0), new Version(0, 2, 41), (uri, _) => Task.FromResult(answers.GetValueOrDefault(uri)));

        Assert.Equal(0, report.Problems.Count, string.Join("; ", report.Problems));
        Assert.True(report.MkvToolNix is { IsAvailable: true, HasSecurityFixes: true } && report.MkvToolNix.News.Count == 2, "MKVToolNix 105.0 with security fixes");
        Assert.Equal("overené s UNIVERSUM (21. 12. 2026)", report.MkvToolNix!.CompatibilityText);
        Assert.True(report.Universum is { IsAvailable: true, CanInstall: false }, "UNIVERSUM 0.3.0, not installed without the release key");

        var none = await UpdateChecker.CheckAsync(new Version(105, 0), new Version(0, 3, 0), (uri, _) => Task.FromResult(uri == UniversumRelease.LatestApi ? null : answers.GetValueOrDefault(uri)));
        Assert.True(!none.AnyAvailable && none.Universum is null, "nothing newer; no release yet");

        var offline = await UpdateChecker.CheckAsync(new Version(103, 0), new Version(0, 3, 0), (_, _) => throw new HttpRequestException("offline"));
        Assert.Equal(2, offline.Problems.Count, "both reported, nothing thrown");
    }

    [Test]
    public void WhenToCheckAndOffer()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(UpdatePolicy.ShouldCheck(true, null, now) && UpdatePolicy.ShouldCheck(true, now.AddHours(-25), now), "first and daily");
        Assert.True(!UpdatePolicy.ShouldCheck(true, now.AddHours(-3), now) && !UpdatePolicy.ShouldCheck(false, null, now), "not twice a day, not when off");

        var news = MkvToolNixNews.Parse(News);
        var plain = new UpdateReport(new MkvToolNixUpdate(new Version(104, 0), new Version(104, 0), [], null), null, []);
        var security = new UpdateReport(new MkvToolNixUpdate(new Version(103, 0), new Version(105, 0), MkvToolNixNews.Between(news, new Version(103, 0), new Version(105, 0)), null), null, []);
        var bugfix = new UpdateReport(new MkvToolNixUpdate(new Version(103, 0), new Version(104, 0), MkvToolNixNews.Between(news, new Version(103, 0), new Version(104, 0)), null), null, []);
        Assert.True(!UpdatePolicy.ShouldOffer(plain, null, null, null, now), "nothing new");
        Assert.True(!UpdatePolicy.ShouldOffer(bugfix, "104.0", null, null, now), "skipped version");
        Assert.True(!UpdatePolicy.ShouldOffer(bugfix, null, null, now.AddDays(3), now), "remind later");
        Assert.True(UpdatePolicy.ShouldOffer(security, null, null, now.AddDays(3), now), "security fixes are shown anyway");
    }

    private static byte[] Gzip(string text)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Fastest))
        {
            gzip.Write(Encoding.UTF8.GetBytes(text));
        }

        return output.ToArray();
    }
}
