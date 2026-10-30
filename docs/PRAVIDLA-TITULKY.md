# Pravidlá titulkov UNIVERSUM

Pravidlá podľa zadania vlastníka (29. a 30. 10. 2026). Platia všade, kde sa používajú pravidlá UNIVERSUM
(Single spracovanie, Hromadné spracovanie, MEGA dávky, Úprava hlavičky a Zlučovanie pri „Použiť pravidlá“).
Všetko, čo UNIVERSUM zistí samo, je **návrh UNIVERSUM** – používateľ ho môže pri stope zmeniť.

## 1. Jazyk každej titulkovej stopy

- UNIVERSUM prečíta text každej textovej titulkovej stopy (SRT, ASS/SSA, WebVTT, MP4 text): začiatok filmu cez FFmpeg,
  celú stopu cez mkvextract, keď začiatok nestačí (forced titulky). Obrazové titulky (PGS, VobSub) sa čítať nedajú.
- Zistený jazyk sa zapíše do parametra **Language** stopy – aj keď je v súbore zapísaný iný.
- **Čeština – isté:** písmená **ě, ř, ů**; slová *mě, tě, mně, něm, těch, těm, dvě, pět, měl, chtěl, směl, měj, běž, věř,
  věc, věk, věž, svět, směr, spěch, děj, běh, směs, zpět, tobě, sobě, něho, němu, těmi, obě, něco, někdo, někde, něčem,
  ještě, prostě, klidně, pěkně, skvěle, špatně, správně, opět, děti, město, země, oběd, peněz, věci*.
- **Slovenčina – isté:** písmená **ä, ĺ, ľ, ô, ŕ**; slová *môj, skôr, päť, späť, môcť, dôjsť, žiaľ, veľmi, veľa, podľa,
  koľko, toľko, vôbec, môžem, môžeš, môže, môžu, kvôli, zatiaľ, odtiaľ, pokiaľ, ľudia, ľudí, veľký, veľká, veľké, rôzne,
  najmä*.
- **dz, dž** sú len slabý znak slovenčiny (čeština ich má tiež: podzim, džus).
- Znaky jedného jazyka = isté. Znaky oboch = rozhoduje ich počet (české meno v slovenských titulkoch ich neurobí českými);
  pri vyrovnanom počte sa nastaví najpravdepodobnejší a pri stope je „overte“.
- Bez týchto znakov rozhodnú krátke slová (se / sa, jsem / som …) – len ako najpravdepodobnejší jazyk.
- Iné jazyky (angličtina, nemčina, poľština, maďarčina, ruština, ukrajinčina) podľa častých slov a písmen; nemecké,
  poľské a maďarské písmená sa nikdy nepovažujú za slovenčinu.

## 2. Forced titulky – rozpoznanie

Titulky sú forced (v tomto poradí): voľba používateľa → príznak Forced v súbore → slovo *forced, vynútené, nútené …*
v názve stopy → **malý počet titulkov**: najviac 30 % titulkov (a bajtov) najväčšej titulkovej stopy súboru, alebo menej
ako 1,5 titulku na minútu filmu, keď je stopa jediná. Rozpoznávajú sa forced titulky všetkých jazykov.

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
