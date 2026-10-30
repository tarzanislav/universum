# Pravidlá titulkov UNIVERSUM

Pravidlá podľa zadania vlastníka (29. a 30. 10. 2026). Platia všade, kde sa používajú pravidlá UNIVERSUM:
Single spracovanie, Hromadné spracovanie, MEGA dávky, Úprava hlavičky (tlačidlo „Pravidlá UNIVERSUM“) a Zlučovanie
(„Pravidlá UNIVERSUM“). Jazyk z textu titulkov (časť 1) číta Single spracovanie a Úprava hlavičky pre otvorený súbor
a Hromadné spracovanie pre prvý súbor každej skupiny (MEGA dávka si ho pamätá); pri Zlučovaní príde spolu s externými
titulkami. Všetko, čo UNIVERSUM zistí samo, je **návrh UNIVERSUM** – používateľ ho môže pri stope zmeniť.

## 1. Jazyk každej titulkovej stopy

- **Nečítajú sa všetky titulky** (vlastník 30. 10. 2026): ako pri rozpoznávaní jazyka zvuku UNIVERSUM vyberie
  **niekoľko častí filmu** – päť častí po 90 sekúnd na 20, 35, 50, 65 a 80 % filmu (krátky film: jedna časť od začiatku)
  –, prečíta ich cez FFmpeg, z prečítaného textu urobí analýzu a jazyk priradí.
  - Z každej časti najviac **25 titulkov** stopy. Podľa **indexu súboru** (Cues – mkvmerge indexuje každý titulok; číta
    sa len index, nie film) FFmpeg vie, koľko titulkov v časti je, a po poslednom prestane čítať.
  - Stopa s malým počtom titulkov (**forced**), ktorá má v týchto častiach menej ako 10 titulkov, sa prečíta aj
    v **najviac troch ďalších častiach tam, kde podľa indexu jej titulky sú**.
  - Plná stopa bez indexu sa číta do konca časti; stopa s malým počtom titulkov bez indexu sa nečíta (jazyk ostane zo
    súboru).
  - Obrazové titulky (PGS, VobSub) sa čítať nedajú, prázdne stopy sa nečítajú. Bez FFmpeg (nie je nainštalovaný)
    prečíta celé stopy mkvextract.
- Zistený jazyk sa zapíše do parametra **Language** stopy – aj keď je v súbore zapísaný iný. Výnimka: len
  najpravdepodobnejší jazyk (bez istých znakov) nikdy neprepíše iný jazyk zapísaný v súbore (napr. *slv*, *eng*) –
  prepíše len neurčený, český alebo slovenský; a angličtina, nemčina … podľa častých slov neprepíšu jazyk, ktorý
  UNIVERSUM rozpoznať nevie (holandčina, španielčina …).
- **Čeština – isté:** písmená **ě, ř, ů**; slová *mě, tě, mně, něm, těch, těm, dvě, pět, měl, chtěl, směl, měj, běž, věř,
  věc, věk, věž, svět, směr, spěch, děj, běh, směs, zpět, tobě, sobě, něho, němu, těmi, obě, něco, někdo, někde, něčem,
  ještě, prostě, klidně, pěkně, skvěle, špatně, správně, opět, děti, město, země, oběd, peněz, věci*.
- **Slovenčina – isté:** písmená **ä, ĺ, ľ, ô, ŕ**; slová *môj, skôr, päť, späť, môcť, dôjsť, žiaľ, veľmi, veľa, podľa,
  koľko, toľko, vôbec, môžem, môžeš, môže, môžu, kvôli, zatiaľ, odtiaľ, pokiaľ, ľudia, ľudí, veľký, veľká, veľké, rôzne,
  najmä*.
- **dz, dž** sú len slabý znak slovenčiny (čeština ich má tiež: podzim, džus).
- Znaky jedného jazyka = isté. Znaky oboch = rozhoduje ich počet (české meno v slovenských titulkoch ich neurobí českými);
  pri vyrovnanom počte sa nastaví najpravdepodobnejší a pri stope je „overte“.
- **ä, ô** samotné (bez ľ, ĺ, ŕ a slovenských slov) – majú ich aj nemčina, francúzština, fínčina – sú len
  najpravdepodobnejšia slovenčina, a len keď text má aj háčky alebo slovenské krátke slová.
- **Titulky bez diakritiky:** rozhodnú krátke slová, ktoré píše len čeština alebo len slovenčina (*jsem, jsou, tady,
  nebo / som, sme, pre, lebo, teraz, alebo* …; slová ako *se, ne, sa, ste, ako* majú aj rumunčina, taliančina,
  chorvátčina, preto nerozhodujú) – len ako najpravdepodobnejší jazyk.
- **Krátky text s háčkami** bez uvedených písmen (napr. forced „Ako sa máš?“): krátke slová a dz, dž – len ako
  najpravdepodobnejší jazyk.
- **Iné jazyky:** text s písmenami, ktoré čeština ani slovenčina nepíše (*ö, ü, ß, è, à, ç, ñ, ¿, ć, đ, ș, ț, ă, ł, å,
  ı, ş* …), nie je český ani slovenský. Angličtina, nemčina, poľština, maďarčina, ruština, ukrajinčina sa rozpoznajú
  podľa častých slov a písmen (turečtina s ö, ü sa nepovažuje za nemčinu). Iný jazyk sa nezistí – stopa ostane, ako ju
  má súbor.

## 2. Forced titulky – rozpoznanie

Titulky sú forced (v tomto poradí): voľba používateľa → príznak Forced v súbore → slovo *forced, vynútené, nútené …*
v názve stopy → **malý počet titulkov**: najviac 30 % titulkov (a bajtov) najväčšej titulkovej stopy **rovnakého druhu**
(textové s textovými, obrázkové PGS/VobSub s obrázkovými – PGS má na titulok dva snímky a megabajty obrázkov), alebo
menej ako 1,5 titulku na minútu filmu, keď plná stopa jej druhu (aspoň 100 titulkov) nie je. Rozpoznávajú sa forced
titulky všetkých jazykov.

## 3. Forced titulky – príznaky

- **Aktívne** sú len **CZ** forced titulky; keď CZ nie sú, **SK** forced titulky: *Default = áno, Enabled = áno,
  Forced = áno*, ostatné príznaky nie.
- Všetky ostatné forced titulky (SK vedľa CZ, EN, …): *Default = nie, Enabled = áno, Forced = nie*.
- Všetky forced titulky majú **Track name = „Forced“**.
- **Cudzí zvuk** (žiadny CZ/SK dabing): predvolené sú **úplné CZ titulky** (inak SK) – *Default = áno, Enabled = áno*,
  ostatné príznaky nie; aktívne CZ forced ostanú *Forced = áno*, *Default = nie*. Keď úplné CZ/SK titulky nie sú,
  predvolené sú aktívne forced.

## 4. Ostatné titulky

- *Default = nie, Enabled = áno, Forced = nie*; titulky pre nepočujúcich (SDH, CC …) majú príznak *pre nepočujúcich*.
- Poradie: CZ, SK, EN, PL, HU, RU, potom jazyky zvukových stôp; ostatné jazyky sa odstránia (dajú sa ponechať).
- **Prázdna titulková stopa** (bez jediného titulku podľa štatistík súboru) sa odstráni.

## 5. Úpravy titulkov (stránka Titulky)

Stránka **Titulky** (MULTIPLEXING) otvorí video alebo súbory titulkov. Originály sa nikdy nemenia: video sa uloží ako
nový súbor v knižnici, súbory titulkov ako nové SRT vedľa pôvodných (nikdy cez existujúci súbor).

- **Zmena FPS titulkov:** pôvodné a nové FPS z ponuky 23,976; 24; 25; 29,97; 30; 50; 23,978 (špeciálne). Časy sa
  násobia pomerom pôvodné / nové (25 → 23,976: o 4,27 % neskôr).
- **Zmena rýchlosti titulkov (percentá):** posuvník a pole od −99 do +100 %, tlačidlá ±0,1 % a ±1 %; +100 % =
  titulky dvakrát rýchlejšie, −50 % = dvakrát pomalšie; −100 % nie je možné (titulky by stáli).
- **Posun (Delay v ms):** 1000 = 1 sekunda; kladné = neskôr, záporné = skôr; tlačidlá ±100 a ±1000.
- Poradie: FPS, potom rýchlosť, potom posun. Vo videu to zapíše mkvmerge (`--sync`) – funguje aj pre obrazové titulky
  (PGS, VobSub). Náhľad ukazuje časy pred a po zmene.
- **Synchronizácia podľa dvoch titulkov:** kedy je titulok teraz a kedy má byť – pre jeden na začiatku a jeden na
  konci; UNIVERSUM vypočíta rýchlosť a posun.
- **Konverzia do SRT** (UTF-8) z WebVTT, TTML/DFXP, ASS/SSA, SAMI, SubViewer, MicroDVD (podľa FPS videa alebo hlavičky),
  MPL2, TMPlayer – formát podľa obsahu, nie podľa prípony (.txt, .sub). Kódovanie sa rozpozná (UTF-8, UTF-16,
  Windows-1250, ISO-8859-2, Windows-1252).
- **Opravy textu:** odstránenie prvkov pre nepočujúcich ([hudba], (smiech), MENO:), formátovania (kurzíva, tučné),
  oprava prekrývania a príliš krátkych časov (najmenej 0,7 s).
- Pravidlá UNIVERSUM (poradie, príznaky, Forced, odstránenie prázdnych stôp) sa použijú, ak sú zapnuté; keď ich
  zastaví neurčený jazyk inej stopy, stránka to povie.

## 6. Externé titulky

- Externé titulky sú súbory titulkov priložené k videu, ku ktorému patria (textové aj obrazové .sup, .idx).
- **Patria k videu,** keď:
  1. sa volajú **rovnako ako video** (aj s jazykom alebo „forced“ za názvom: *Film.cze.forced.srt*) – najistejšie;
  2. sa zhodujú **slová názvu** (bodky, podčiarkovníky a pomlčky medzi slovami nevadia) a rok, ak ho majú oba;
  3. pri **epizóde** navyše vždy **rovnaká séria a epizóda** (S01E1 = S01E01 = 1x01); titulky inej epizódy alebo
     iného filmu nepatria nikdy;
  4. v priečinku s **jediným videom** aj súbor bez názvu titulu (*titulky.srt*).
- **Jazyk** z textu (časť 1 – návrh UNIVERSUM), inak z názvu (cs, cz, cze, czech, sk, slo, en, eng …); *forced*
  v názve = forced, *sdh, cc, hi* = pre nepočujúcich.
- Single, Hromadné aj MEGA spracovanie ich pridajú ako stopy videa – pravidlá ich zoradia a nastavia ako ostatné
  titulky; pri zápise sa textové prevedú do SRT v UTF-8. MEGA dávka si pamätá veľkosť a čas súboru – zmenený alebo
  chýbajúci súbor titulkov = video sa preskočí.

## 7. Stiahnutie a preklad titulkov

- **OpenSubtitles.com** (anglické titulky, aj SDH): vlastný API kľúč a účet v Nastaveniach (šifrovane). Hľadá sa podľa
  TMDB ID titulu (inak podľa názvu a roku) a epizódy; sťahovanie potrebuje prihlásenie (denný limit podľa účtu).
- **opensubtitles.org:** staré rozhranie neprijíma nové programy – len cez prehliadač.
- **titulky.com** (CZ/SK): pravidlá stránky povoľujú len prehliadač, automatické sťahovanie programom zakazujú.
  UNIVERSUM otvorí hľadanie titulu v prehliadači; stiahnuté titulky (aj ZIP) do 30 minút sám vezme zo Stiahnutých
  súborov. **edna.cz:** rozhranie pre programy nemá – len prehliadač.
- **Preklad (svsub.net),** keď titulky neexistujú: vlastný token v Nastaveniach (šifrovane), poskytovateľ AI (napr.
  openai; model sa vyberá v nastaveniach služby), cieľový jazyk (cs, sk, en) – zdrojový a cieľový jazyk nesmú byť
  rovnaké. Posielajú sa len titulky pod neutrálnym názvom, úloha sa po stiahnutí prekladu na službe zmaže.
- Stiahnuté a preložené titulky sa uložia **vedľa videa** ako *Názov videa.jazyk(.sdh)(.forced).srt* (nikdy cez
  existujúci súbor), takže ich každé spracovanie nájde ako externé titulky.
