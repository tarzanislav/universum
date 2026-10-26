# UNIVERSUM

Grafická nadstavba pre Windows nad nástrojmi [MKVToolNix](https://mkvtoolnix.download/) – úprava stôp
v súboroch MKV podľa jasných pravidiel, jednotlivo aj hromadne pre celý priečinok.

> Stav: **vo vývoji** (etapa E2b – knižnica). Program zatiaľ nie je určený na bežné používanie.

## Čo bude UNIVERSUM vedieť

- **Rozhranie ako MKVToolNix GUI** – zlučovanie, informácie o súbore, úprava hlavičky, kapitoly, fronta úloh.
- **Pravidlá UNIVERSUM jedným klikom** – názvy stôp, jazyky, príznaky *Default*, *Enabled*, *Forced*,
  poradie stôp (CZ → SK → ostatné) a odstránenie tagov.
- **Hromadné spracovanie** – analýza celého priečinka, skupiny súborov podľa štruktúry a tolerantné šablóny,
  ktoré si poradia aj so súbormi s chýbajúcou stopou.
- **Knižnica pre Jellyfin** – výsledky idú do výstupného priečinka: `MOVIES\Creep (2004)\Creep (2004) - CZ EN FHD H264 BIG.mkv`
  a `TVSHOWS\Nine Puzzles (2025) [tmdbid-227191]\Season 01\Nine Puzzles S01E04 - CZ titulky FHD H264 SMALL.mkv`.
  Názvy vie vyhľadať na [TMDB](https://www.themoviedb.org/) s vlastným bezplatným kľúčom používateľa.
  V Nastaveniach sa dá uložiť aj vlastný token [HISTRA](https://histra.net) (sledovanie filmov a seriálov).
- **Bezpečnosť súborov** – simulácia pred zápisom, overenie výsledku, Vrátiť späť, originál podľa voľby
  zachovaný alebo odstránený (presunutý do knižnice alebo do Koša, nikdy natrvalo).
- **Aktualizácie** UNIVERSUM aj MKVToolNix z jedného miesta.

## Požiadavky

- Windows 10 alebo 11 (64-bit)
- MKVToolNix (klasická inštalácia alebo prenosná verzia; vydanie z Microsoft Store nestačí).
  Inštalátor UNIVERSUM ho vie stiahnuť z oficiálnej stránky, overiť a nainštalovať.

## Licencia

Apache License 2.0 – pozri [LICENSE](LICENSE).

UNIVERSUM je samostatný projekt. Nie je súčasťou MKVToolNix a jeho autor ho nevydáva ani nepodporuje.
MKVToolNix (© Moritz Bunkus) má vlastnú licenciu; UNIVERSUM ho iba spúšťa ako samostatné programy.

This product uses the TMDB API but is not endorsed or certified by TMDB.

Logo TMDB (`src/Universum.App/Assets/tmdb-logo.*`) je ochranná známka [TMDB](https://www.themoviedb.org),
použitá nezmenená podľa [pravidiel uvádzania zdroja](https://www.themoviedb.org/about/logos-attribution);
licencia Apache 2.0 sa naň nevzťahuje.
