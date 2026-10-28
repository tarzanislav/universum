# Test data

Small, invented video files for automated tests (Matroska and, for batch conversion, MP4, AVI and TS). They contain generated test patterns, sine tones, invented
subtitles and invented sentences read by a synthetic voice; they are released under **CC0 1.0** (public domain). Real films never go into this repository.

| File | What it tests |
| --- | --- |
| `media/en-und-cz-forced.mkv` | English audio first, Czech dub without a language (`und`), forced subtitles recognisable only by their name, global tags, chapters, segment title |
| `media/cz-only.mkv` | an already clean Czech file |
| `media/en-only-cz-subs.mkv` | foreign audio only: Czech subtitles must become the default, forced Czech subtitles present |
| `media/sk-cz-mixed.mkv` | Slovak, Czech and English audio in the wrong order, Slovak and Czech subtitles |
| `media/en-cz.mp4` | MP4 with English AAC, Czech AC-3 and Czech timed-text subtitles – converted to MKV |
| `media/sk-en.ts` | MPEG transport stream (like a TV recording): Slovak AC-3 first, English AAC – converted to MKV |
| `media/und.avi` | AVI without any language tags (MP3 audio) – the language must be chosen, then converted to MKV |
| `media/speech-und.mkv` | three audio tracks without a language or name with invented sentences read by a synthetic voice (espeak-ng): Czech, English, Slovak – recognition of the spoken language |
| `identify/*.json` | recorded `mkvmerge -J` output for parser tests |

Regenerate the files with `tools/make-testdata.sh` (needs ffmpeg, mkvmerge and espeak-ng).
