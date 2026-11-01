# Diela tretích strán v UNIVERSUM

UNIVERSUM (licencia v súbore LICENSE) obsahuje alebo používa nasledujúce diela tretích strán.

## .NET Community Toolkit (CommunityToolkit.Mvvm)

https://github.com/CommunityToolkit/dotnet

```
Copyright © .NET Foundation and Contributors

All rights reserved.

MIT License (MIT)

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated
documentation files (the “Software”), to deal in the Software without restriction, including without limitation the
rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit
persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the
Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE
WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NON-INFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

## Rozpoznávanie jazyka reči – priečinok `speech` inštalácie

Inštalácia obsahuje v priečinku `speech` tieto samostatné programy a dáta (UNIVERSUM ich spúšťa ako samostatné
procesy; všetko beží len v počítači). Pri každom je jeho licencia a súbor `SOURCE.txt` s pôvodom; zostavu pripravuje
`tools/speech/prepare.sh` a `manifest.json` obsahuje SHA-256 všetkých súborov.

- **FFmpeg 9.0.2** (`speech/ffmpeg`) – https://ffmpeg.org, licencia **GNU LGPL 2.1 alebo novšia** (`COPYING.LGPLv2.1`).
  Zostavený pre UNIVERSUM z oficiálneho zdrojového kódu (commit `946fcce07b6dcd0331c8cc609192aeff5e1924f8`, značka
  `n9.0.2`) skriptom `tools/speech/build-ffmpeg.sh`, bez súčastí pod GPL; staticky pripojená knižnica zlib (licencia zlib).
  **Úplný zdrojový kód tejto zostavy** (FFmpeg a skript zostavenia) je priložený ku každému vydaniu UNIVERSUM na
  GitHube ako `ffmpeg-9.0.2-source.tar.xz` (https://github.com/tarzanislav/universum/releases).
- **whisper.cpp 1.9.2** (`speech/whisper`) – https://github.com/ggml-org/whisper.cpp, licencia MIT (`LICENSE`),
  oficiálna zostava pre Windows `whisper-bin-x64.zip`.
- **Modely reči Whisper „base“ a „large-v3-turbo“ (kvantizovaný q5_0)** (`speech/models/ggml-base.bin`, `speech/models/ggml-large-v3-turbo-q5_0.bin`) – model OpenAI Whisper (https://github.com/openai/whisper)
  prevedený pre whisper.cpp (https://huggingface.co/ggerganov/whisper.cpp), licencia MIT, Copyright (c) 2022 OpenAI.

## Služby, ktoré UNIVERSUM používa (nič z nich nie je súčasťou programu)

- **Počasie: Open-Meteo.com** – https://open-meteo.com, údaje pod licenciou CC BY 4.0
  (https://creativecommons.org/licenses/by/4.0/). Posiela sa len mesto zadané v Nastaveniach a jeho súradnice.
- **TMDB** – This product uses the TMDB API but is not endorsed or certified by TMDB (https://www.themoviedb.org).
- **HISTRA** – https://histra.net, s vlastným tokenom používateľa; posiela sa len hľadaný názov.
- **MKVToolNix** – https://mkvtoolnix.download (Moritz Bunkus, GPL-2.0); nie je súčasťou UNIVERSUM – UNIVERSUM spúšťa
  nainštalované nástroje MKVToolNix (ak chýbajú, stiahne oficiálny inštalátor a overí ho).
