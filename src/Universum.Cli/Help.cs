namespace Universum.Cli;

internal static class Help
{
    public const string Text = """
        UNIVERSUM – príkazový riadok (hromadné spracovanie súborov MKV)

        universum-cli analyze <priečinok> [voľby]   ukáže skupiny, stopy a čo by sa urobilo
        universum-cli apply   <priečinok> [voľby]   spracuje súbory (po overení každého výsledku)

        Voľby:
          -r, --recursive              aj podpriečinky
          -t, --template <súbor.json>  šablóna (uložená v programe UNIVERSUM)
          --keep                       originál zachovať (výsledok vedľa neho alebo v --target)
          --delete                     originál presunúť do Koša
          --target <priečinok>         priečinok pre výsledky (podpriečinky sa zachovajú)
          --dry-run                    len simulácia, nič sa nezapíše
          -l, --language A:audio:2=cs  jazyk neurčenej stopy pre celú skupinu A
          --forced A:subtitles:1       titulky 1 skupiny A sú forced
          --confirm-suggestions        prijať návrhy (podobná stopa, počet titulkov)
          --report <súbor.html|csv>    uložiť správu
          --mkvtoolnix <priečinok>     priečinok s MKVToolNix

        Návratový kód: 0 = všetko v poriadku, 1 = niečo ostalo na kontrolu alebo zlyhalo, 2 = chyba zadania.
        """;
}
