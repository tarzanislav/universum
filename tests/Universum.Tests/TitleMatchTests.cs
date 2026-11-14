using Universum.Core.Library;
using Universum.Core.Library.Tmdb;

namespace Universum.Tests;

/// <summary>
/// The film found for the name of a file and the title meant by a tab of the browser – decided in the core since 14. 11.
/// 2026 (step 2 of the review of the code; before, in Hromadné spracovanie and the Zásobník TMDB). Invented titles only.
/// </summary>
public sealed class TitleMatchTests
{
    private static TmdbTitle Film(int id, string name, int? year) => new(MediaKind.Movie, id, name, name, "sk", year, []);

    [Test]
    public void TheFilmOfAFileAndWhatSpeaksAgainstIt()
    {
        var exact = new TitleResults([Film(1, "Vymyslený film", 2020), Film(2, "Iný vymyslený film", 2020)], "TMDB").Match("Vymyslený film", 2020);
        Assert.True(exact is { Title.Id: 1, Doubts.Count: 0 }, "named as the file, with its year: sure");

        var typo = new TitleResults([Film(3, "Zlatý vymyslený kapor", 2019), Film(4, "Úplne iný názov", 2019)], "TMDB").Match("Zlaty vymysleny kapro", 2019);
        Assert.True(typo.Title?.Id == 3 && typo.Doubts is [var different] && different.StartsWith("názov v mene súboru sa líši", StringComparison.Ordinal),
            "a similar name: suggested, with its doubt – " + string.Join("; ", typo.Doubts));

        var only = new TitleResults([Film(5, "Celkom iný názov", 2018)], "TMDB").Match("Vymyslená záhada", 2018);
        Assert.True(only.Title?.Id == 5 && only.Doubts.Single().StartsWith("jediný nájdený titul", StringComparison.Ordinal), "the only one found: suggested, not sure");

        var loosened = new TitleResults([Film(6, "Vymyslená cesta", 2011)], "TMDB") { Loosened = "hľadané bez roka" }.Match("Vymyslená cesta", 2015);
        Assert.Sequence(["hľadané bez roka", "rok v mene súboru 2015, na TMDB 2011"], loosened.Doubts, "loosened and the year far off – both told");
        Assert.Equal(6, loosened.Title?.Id, "found without the year");

        var near = new TitleResults([Film(7, "Vymyslený rok", 2020)], "TMDB").Match("Vymyslený rok", 2021);
        Assert.True(near.Title?.Id == 7 && near.Doubts.Count == 0, "a year off is the year it came out here – no doubt");

        var none = new TitleResults([Film(8, "Prvý vymyslený", 2020), Film(9, "Druhý vymyslený", 2020)], "TMDB").Match("Celkom iný film", 2020);
        Assert.True(none.Title is null && none.Doubts.Count == 0, "several titles, none named so: none (the user chooses)");
    }

    [Test]
    public void TheTitleOfATab()
    {
        var series = new TmdbTitle(MediaKind.Series, 10, "Vymyslený seriál", "Vymyslený seriál", "sk", 2024, []);
        var results = new TitleResults([Film(11, "Vymyslený seriál", 2024), series, new TmdbTitle(MediaKind.Series, 12, "Vymyslený seriál", "Vymyslený seriál", "sk", 2010, [])], "TMDB");
        Assert.True(results.Likeliest(MediaKind.Series, "Vymyslený seriál", 2024) is { Title.Id: 10, Sure: true }, "the only series of the name and year: sure");
        Assert.True(results.Likeliest(MediaKind.Series, "Vymyslený seriál", 2030) is { Title.Id: 10, Sure: false }, "no such year: the likeliest, to be checked");
        Assert.True(results.Likeliest(MediaKind.Movie, "Iný film", 2024) is { Title.Id: 11, Sure: false }, "only films are films");
        Assert.True(new TitleResults([series], "TMDB").Likeliest(MediaKind.Movie, "Vymyslený seriál", 2024) is null, "no title of the kind: none");
    }
}
