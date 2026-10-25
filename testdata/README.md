# Test data

Small, invented Matroska files for automated tests. They contain generated test patterns, sine tones and invented
subtitles; they are released under **CC0 1.0** (public domain). Real films never go into this repository.

| File | What it tests |
| --- | --- |
| `media/en-und-cz-forced.mkv` | English audio first, Czech dub without a language (`und`), forced subtitles recognisable only by their name, global tags, chapters, segment title |
| `media/cz-only.mkv` | an already clean Czech file |
| `media/en-only-cz-subs.mkv` | foreign audio only: Czech subtitles must become the default, forced Czech subtitles present |
| `media/sk-cz-mixed.mkv` | Slovak, Czech and English audio in the wrong order, Slovak and Czech subtitles |
| `identify/*.json` | recorded `mkvmerge -J` output for parser tests |

Regenerate the files with `tools/make-testdata.sh` (needs ffmpeg and mkvmerge).
