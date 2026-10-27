# Diela tretích strán v UNIVERSUM

UNIVERSUM (licencia v súbore LICENSE) obsahuje alebo používa nasledujúce diela tretích strán.

## Slovenský kalendár mien – name-day-calendar

Mená v kalendári Dashboardu pochádzajú zo súboru `data/countries/SK.yaml` projektu
https://github.com/peterknezek/name-day-calendar (prevedené do `src/Universum.Core/Calendar/sk-namedays.txt`;
opravený preklep „Akexia“ → „Alexia“ 9. 1.).

```
MIT License

Copyright (c) 2021 Peter Knezek

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

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

## Služby, ktoré UNIVERSUM používa (nič z nich nie je súčasťou programu)

- **Počasie: Open-Meteo.com** – https://open-meteo.com, údaje pod licenciou CC BY 4.0
  (https://creativecommons.org/licenses/by/4.0/). Posiela sa len mesto zadané v Nastaveniach a jeho súradnice.
- **TMDB** – This product uses the TMDB API but is not endorsed or certified by TMDB (https://www.themoviedb.org).
- **HISTRA** – https://histra.net, s vlastným tokenom používateľa; posiela sa len hľadaný názov.
- **MKVToolNix** – https://mkvtoolnix.download (Moritz Bunkus, GPL-2.0); nie je súčasťou UNIVERSUM – UNIVERSUM spúšťa
  nainštalované nástroje MKVToolNix (ak chýbajú, stiahne oficiálny inštalátor a overí ho).
