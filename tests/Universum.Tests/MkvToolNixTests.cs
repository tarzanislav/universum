using Universum.Core.MkvToolNix;
using Universum.Core.Security;
using Universum.Platform.Windows;

namespace Universum.Tests;

public sealed class MkvToolNixTests
{
    [Test]
    public void VersionFromToolOutput()
    {
        Assert.True(MkvToolNixVersion.TryParseToolOutput("mkvmerge v103.0 ('The Darkness') 64-bit", out var v103), "v103");
        Assert.Equal(new Version(103, 0), v103);
        Assert.True(MkvToolNixVersion.TryParseToolOutput("mkvmerge v81.0.1 ('x') 64-bit\n", out var v81), "v81.0.1");
        Assert.Equal(new Version(81, 0, 1), v81);
        Assert.True(!MkvToolNixVersion.TryParseToolOutput("something else", out _), "no version");
    }

    [Test]
    public void DownloadAddresses()
    {
        var version = new Version(103, 0);
        Assert.Equal("mkvtoolnix-64-bit-103.0-setup.exe", MkvToolNixDownloads.WindowsInstallerFileName(version));
        Assert.Equal(
            "https://mkvtoolnix.download/windows/releases/103.0/mkvtoolnix-64-bit-103.0-setup.exe",
            MkvToolNixDownloads.WindowsReleaseFile(version, MkvToolNixDownloads.WindowsInstallerFileName(version)).ToString());
        Assert.Equal("https://mkvtoolnix.download/windows/releases/103.0/sha256sums.txt", MkvToolNixDownloads.WindowsSha256Sums(version).ToString());
        Assert.Equal("81.0.1", MkvToolNixDownloads.Format(new Version(81, 0, 1)));
        Assert.True(MkvToolNixDownloads.IsOfficial(new Uri("https://mkvtoolnix.download/x")), "official");
        Assert.True(!MkvToolNixDownloads.IsOfficial(new Uri("http://mkvtoolnix.download/x")), "http is refused");
        Assert.True(!MkvToolNixDownloads.IsOfficial(new Uri("https://mkvtoolnix.download.evil.example/x")), "other host");
    }

    [Test]
    public void ReleaseFeedWithLatestSource()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <mkvtoolnix-releases>
              <latest-source><version>103.0</version><url>https://mkvtoolnix.download/sources/mkvtoolnix-103.0.tar.xz</url></latest-source>
              <latest-windows-pre><version>104.0</version></latest-windows-pre>
            </mkvtoolnix-releases>
            """;
        Assert.Equal(new Version(103, 0), MkvToolNixReleaseFeed.ParseLatestVersion(xml));
    }

    [Test]
    public void ReleaseFeedFallsBackToHighestVersion()
    {
        Assert.Equal(new Version(103, 0), MkvToolNixReleaseFeed.ParseLatestVersion("<r><a><version>99.0</version></a><b><version>103.0</version></b></r>"));
        Assert.Throws<MkvToolNixException>(() => MkvToolNixReleaseFeed.ParseLatestVersion("<r/>"), "no version");
        Assert.Throws<MkvToolNixException>(() => MkvToolNixReleaseFeed.ParseLatestVersion("not xml"), "broken xml");
    }

    [Test]
    public void ReleaseFeedGzipIsDecoded()
    {
        var xml = "<r><latest-source><version>103.0</version></latest-source></r>";
        using var buffer = new MemoryStream();
        using (var gzip = new System.IO.Compression.GZipStream(buffer, System.IO.Compression.CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(System.Text.Encoding.UTF8.GetBytes(xml));
        }

        Assert.Equal(xml, MkvToolNixReleaseFeed.Decode(buffer.ToArray()));
        Assert.Equal(xml, MkvToolNixReleaseFeed.Decode(System.Text.Encoding.UTF8.GetBytes(xml)));
    }

    /// <summary>CI downloads the real feed and passes it in UNIVERSUM_LATEST_RELEASE_XML.</summary>
    [Test]
    public void RealReleaseFeed()
    {
        var path = Environment.GetEnvironmentVariable("UNIVERSUM_LATEST_RELEASE_XML");
        if (string.IsNullOrEmpty(path))
        {
            Assert.Skip("UNIVERSUM_LATEST_RELEASE_XML not set");
        }

        var version = MkvToolNixReleaseFeed.ParseLatestVersion(MkvToolNixReleaseFeed.Decode(File.ReadAllBytes(path!)));
        Console.WriteLine($"      latest MKVToolNix release: {version}");
        Assert.True(version >= new Version(103, 0), $"latest version {version} is at least 103.0");
    }

    [Test]
    public void Sha256SumsFormats()
    {
        var hash = new string('a', 64);
        var sums = Sha256Sums.Parse($"{hash}  mkvtoolnix-64-bit-103.0-setup.exe\r\n{new string('B', 64)} *windows/releases/103.0/x.7z\njunk line\n");
        Assert.Equal(hash.ToUpperInvariant(), sums["mkvtoolnix-64-bit-103.0-setup.exe"]);
        Assert.Equal(new string('B', 64), sums["x.7z"]);
        Assert.Equal(2, sums.Count, "entries");
    }

    [Test]
    public void StoreEditionIsRejected()
    {
        Assert.True(MkvToolNixInstallation.IsStoreEdition(@"C:\Program Files\WindowsApps\MoritzBunkus.MKVToolNix_103.0.0.0_x64__abc"), "store");
        Assert.True(!MkvToolNixInstallation.IsStoreEdition(@"C:\Program Files\MKVToolNix"), "classic");
    }

    [Test]
    public void LocatorOrderAndDuplicates()
    {
        var locator = new MkvToolNixLocator(new Core.Processes.ProcessRunner(), [new FixedSource("/opt/a", "/opt/b/", "/opt/a")]);
        var candidates = locator.CandidateDirectories("/user/choice").ToList();
        Assert.Equal("/user/choice", candidates[0], "user folder first");
        Assert.True(candidates.IndexOf("/opt/a") > 0 && candidates.IndexOf("/opt/b") > candidates.IndexOf("/opt/a"), "source order");
        Assert.Equal(1, candidates.Count(c => c == "/opt/a"), "no duplicates");
    }

    [Test]
    public void UninstallEntryExecutable()
    {
        Assert.Equal(@"C:\Program Files\MKVToolNix\uninst.exe", UninstallEntry.ExecutablePath("\"C:\\Program Files\\MKVToolNix\\uninst.exe\" /S"));
        Assert.Equal(@"C:\Program Files\MKVToolNix\mkvtoolnix-gui.exe", UninstallEntry.ExecutablePath(@"C:\Program Files\MKVToolNix\mkvtoolnix-gui.exe,0"));
        Assert.Equal<string?>(null, UninstallEntry.ExecutablePath("nothing"));
    }

    private sealed class FixedSource(params string[] directories) : IMkvToolNixLocationSource
    {
        public IEnumerable<string> CandidateDirectories() => directories;
    }
}
