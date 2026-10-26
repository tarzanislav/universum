namespace Universum.Cli;

internal static class Help
{
    public const string Text = """
        UNIVERSUM – príkazový riadok (hromadné spracovanie súborov MKV)

        universum-cli analyze <priečinok> [voľby]   ukáže skupiny, stopy, názvy v knižnici a čo by sa urobilo
        universum-cli apply   <priečinok> [voľby]   uloží súbory do knižnice (po overení každého výsledku)

        Výsledky idú do výstupného priečinka: MOVIES\Názov (rok)\… a TVSHOWS\Názov (rok)\Season 01\…

        Voľby:
          -o, --output <priečinok>     výstupný priečinok (inak ten z nastavení UNIVERSUM)
          --film | --serial            filmy alebo seriál (inak podľa názvov súborov)
          --nazov "Creep" --rok 2004   názov titulu: seriálu, pri filmoch pre všetky súbory (jeden film, aj viac verzií)
          --tmdb <ID alebo adresa>     titul z TMDB (s kľúčom); bez kľúča len ID k --nazov, pridá sa ako [tmdbid-ID]
          --tmdb-key <kľúč>            kľúč TMDB (inak UNIVERSUM_TMDB_KEY alebo kľúč uložený v programe UNIVERSUM);
                                       s kľúčom sa návrhy názvov z mien súborov spresnia podľa TMDB
          --epizoda "súbor.mkv=S01E04" číslo epizódy súboru
          -r, --recursive              aj podpriečinky
          -t, --template <súbor.json>  šablóna (uložená v programe UNIVERSUM)
          --keep                       originál zachovať (predvolené; mazanie originálov je zatiaľ vypnuté)
          --dry-run                    len simulácia, nič sa nezapíše
          -l, --language A:audio:2=cs  jazyk neurčenej stopy pre celú skupinu A
          --forced A:subtitles:1       titulky 1 skupiny A sú forced
          --confirm-suggestions        prijať návrhy (jazyk podobnej stopy, forced, názvy z mien súborov, poradie epizód)
          --report <súbor.html|csv>    uložiť správu
          --mkvtoolnix <priečinok>     priečinok s MKVToolNix

        Návratový kód: 0 = všetko v poriadku, 1 = niečo ostalo na kontrolu alebo zlyhalo, 2 = chyba zadania.
        """;
}
