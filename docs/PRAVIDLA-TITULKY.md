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
