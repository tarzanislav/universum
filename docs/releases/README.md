# Poznámky k vydaniam

Ku každému vydaniu patrí súbor `vX.Y.Z.md` s popisom zmien po slovensky. Zobrazí ho okno Aktualizácie v UNIVERSUM.

**Spustenie vydania (vlastník):** GitHub → **Actions → Release → Run workflow** (vetva `main`) → verzia, napr. `0.1.0`
→ **Run workflow**. Po zostavení a testoch (10–15 min) beh čaká: otvoriť ho → **Review deployments** → zaškrtnúť
`release` → **Approve and deploy**. Značka `vX.Y.Z` vznikne až pri zverejnení. Prostredie `release` musí povoliť
vetvu `main` (Settings → Environments → release → Deployment branches and tags → pravidlo typu Branch, `main`).

Vydanie vznikne len so súhlasom vlastníka (tlačidlom alebo značkou `vX.Y.Z`) a len ak je nastavený podpisový kľúč vydaní
(tajomstvo `UNIVERSUM_RELEASE_KEY`, verejný kľúč v `src/Universum.Core/Updates/ReleaseSignature.cs`).

Postup v `release.yml`: úloha `build` (len čítanie, bez tajomstiev) zostaví, otestuje a zabalí inštalátor a zoznam
`SHA256SUMS.txt`, ktorého prvý riadok je `# UNIVERSUM X.Y.Z`. Úloha `publish` v prostredí `release` zoznam podpíše
a vytvorí vydanie. Odporúčanie: tajomstvo uložiť ako tajomstvo prostredia `release` a v prostredí zapnúť
povinné schválenie vlastníkom.

## Podpisový kľúč – nastavenie (raz)

Hotové 26. 10. 2026: prostredie `release` s povinným schválením, tajomstvo `UNIVERSUM_RELEASE_KEY`, verejný kľúč
v programe. Postup ostáva pre prípad výmeny kľúča:


1. GitHub → repozitár `universum` → **Settings → Environments → New environment** → názov `release`.
   - **Required reviewers**: zaškrtnúť a pridať vlastníka (každé vydanie čaká na jeho schválenie).
   - **Deployment branches and tags** → *Selected branches and tags* → **Add deployment branch or tag rule**
     → typ *Tag*, vzor `v*.*.*`.
2. Na svojom počítači vložiť do okna PowerShell celý obsah `tools/New-ReleaseKey.ps1`. Súkromný kľúč sa
   nikam neuloží – skript ho dá do schránky (Enter ho skopíruje znova).
3. V prostredí `release` → **Add environment secret** (alebo ceruzka pri existujúcom) → názov
   `UNIVERSUM_RELEASE_KEY`, hodnota Ctrl+V. Medzitým nič nekopírovať ani nerobiť snímku obrazovky. Potom v
   PowerShell napísať H a Enter – schránka sa vymaže a až teraz sa vypíše verejný kľúč.
4. Verejný kľúč (nie je tajný) vložiť do `ReleaseSignature.PublicKey` v
   `src/Universum.Core/Updates/ReleaseSignature.cs`. Od tej verzie si UNIVERSUM overené aktualizácie
   nainštaluje sám.

Súkromný kľúč existuje len ako tajomstvo v GitHube (nedá sa z neho prečítať). Ak by sa stratil alebo
prezradil: vytvoriť nový kľúč rovnakým postupom, nový verejný kľúč dať do programu a toto jedno vydanie
nainštalovať ručne – ďalšie už pôjdu automaticky.
