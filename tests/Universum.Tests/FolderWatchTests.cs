using Universum.Core.IO;

namespace Universum.Tests;

/// <summary>The input folder watched (owner, 31. 10. 2026): new films are told once they have settled.</summary>
public sealed class FolderWatchTests
{
    [Test]
    public async Task NewFilmsAreToldOnceSettled()
    {
        var folder = Directory.CreateTempSubdirectory("universum-watch-").FullName;
        try
        {
            var arrived = new List<string>();
            using var watch = new FolderWatch(folder, TimeSpan.FromMilliseconds(600), path => path.Contains("preskocit", StringComparison.Ordinal));
            watch.Arrived += files =>
            {
                lock (arrived)
                {
                    arrived.AddRange(files);
                }
            };

            var film = Path.Combine(folder, "Vymysleny.Film.2026.mkv");
            await File.WriteAllBytesAsync(film, new byte[1000]);
            Directory.CreateDirectory(Path.Combine(folder, "Serial"));
            await File.WriteAllBytesAsync(Path.Combine(folder, "Serial", "Vymysleny.Serial.S01E01.mp4"), new byte[500]);
            await File.WriteAllBytesAsync(Path.Combine(folder, "Vymysleny.Film.2026.mkv.part"), new byte[100]);
            await File.WriteAllBytesAsync(Path.Combine(folder, "Vymysleny.Film.universum-tmp.mkv"), new byte[100]);
            await File.WriteAllBytesAsync(Path.Combine(folder, "popis.txt"), new byte[10]);
            await File.WriteAllBytesAsync(Path.Combine(folder, "preskocit.mkv"), new byte[10]);

            // a copy in progress: still growing, not told yet
            var growing = Path.Combine(folder, "Rastuci.Film.mkv");
            await using (var stream = new FileStream(growing, FileMode.Create, FileAccess.Write, FileShare.Read))
            {
                for (var i = 0; i < 8; i++)
                {
                    await stream.WriteAsync(new byte[100]);
                    await stream.FlushAsync();
                    await Task.Delay(150);
                }

                lock (arrived)
                {
                    Assert.True(!arrived.Contains(growing), "a file still being written is not told");
                }
            }

            for (var i = 0; i < 60 && arrived.Count < 3; i++)
            {
                await Task.Delay(100);
            }

            lock (arrived)
            {
                Assert.Sequence(["Rastuci.Film.mkv", "Vymysleny.Film.2026.mkv", "Vymysleny.Serial.S01E01.mp4"], arrived.Select(Path.GetFileName).Order(StringComparer.Ordinal),
                    "the films and the episode, not downloads, files of UNIVERSUM, other files or skipped ones");
            }

            Assert.True(FolderWatch.IsVideo("a.m2ts") && !FolderWatch.IsVideo("a.mkv.crdownload") && !FolderWatch.IsVideo("~a.mkv"), "what a film is");
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
