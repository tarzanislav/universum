using Universum.Core.Text;

namespace Universum.Tests;

public sealed class TextTests
{
    [Test]
    public void PhraseMatchingIgnoresCaseAndDiacritics()
    {
        Assert.Equal("vynutene titulky", Words.Normalize("  Vynútené – TITULKY! "));
        Assert.True(Words.ContainsPhrase("Czech (Forced)", "forced"), "in brackets");
        Assert.True(Words.ContainsPhrase("Titulky pre nepočujúcich", "pre nepocujucich"), "two words");
        Assert.True(!Words.ContainsPhrase("Unforced", "forced"), "whole words only");
        Assert.True(!Words.ContainsPhrase("CCTV", "cc"), "whole words only (cc)");
        Assert.True(!Words.ContainsPhrase(null, "forced"), "null text");
    }

    [Test]
    public void ContextMenuRequestsAreRead()
    {
        var mux = Universum.Core.Shell.ShellRequest.Parse(["--zlucovanie", @"D:\Filmy\Film.mkv"]);
        Assert.True(mux is { Action: Universum.Core.Shell.ShellAction.Multiplexer, Paths: [@"D:\Filmy\Film.mkv"] }, "add to the multiplexer");
        var drive = Universum.Core.Shell.ShellRequest.Parse(["--hromadne", "C:\""]);
        Assert.True(drive is { Action: Universum.Core.Shell.ShellAction.Batch, Paths: ["C:\\"] }, "a drive (C:\" arrives as C:\")");
        var plain = Universum.Core.Shell.ShellRequest.Parse([@"D:\Filmy"]);
        Assert.Equal(Universum.Core.Shell.ShellAction.Open, plain.Action);
        Assert.True(Universum.Core.Shell.ShellRequest.Parse(["--informacie"]).Paths.Count == 0, "no path");
        Assert.Equal(Universum.Core.Shell.ShellAction.Single, Universum.Core.Shell.ShellRequest.Parse(["--SINGLE", "a.mkv"]).Action);
    }
}
