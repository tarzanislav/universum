#!/usr/bin/env bash
# Generates the small, invented test MKV files in testdata/media (CC0).
# Requires ffmpeg, mkvmerge and espeak-ng (synthetic speech). Run from the repository root: tools/make-testdata.sh
set -euo pipefail

out="testdata/media"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

ffmpeg -loglevel error -y -f lavfi -i "testsrc=size=64x36:rate=10" -t 2 -c:v libx264 -preset ultrafast -bf 0 "$work/v.h264"
ffmpeg -loglevel error -y -f lavfi -i "sine=frequency=440:duration=2" -ac 1 -ar 8000 -c:a aac -b:a 16k "$work/a-en.aac"
ffmpeg -loglevel error -y -f lavfi -i "sine=frequency=660:duration=2" -ac 1 -ar 32000 -c:a ac3 -b:a 32k "$work/a-cz.ac3"
ffmpeg -loglevel error -y -f lavfi -i "sine=frequency=880:duration=2" -ac 1 -ar 32000 -c:a ac3 -b:a 32k "$work/a-sk.ac3"

printf '1\n00:00:00,200 --> 00:00:00,900\nAhoj, ako sa máš?\n\n2\n00:00:01,000 --> 00:00:01,800\nĎakujem, dobre.\n' > "$work/s-sk.srt"
printf '1\n00:00:00,200 --> 00:00:00,900\nDobrý den, jak se máte?\n\n2\n00:00:01,000 --> 00:00:01,800\nDěkuji, mám se dobře.\n' > "$work/s-cz.srt"
printf '1\n00:00:01,000 --> 00:00:01,800\nNa shledanou.\n' > "$work/s-cz-forced.srt"
printf '1\n00:00:00,200 --> 00:00:00,900\nHello, how are you?\n\n2\n00:00:01,000 --> 00:00:01,800\nFine, thanks.\n' > "$work/s-en.srt"

cat > "$work/tags.xml" <<'XML'
<?xml version="1.0"?>
<Tags><Tag><Targets><TargetTypeValue>50</TargetTypeValue></Targets>
<Simple><Name>ENCODER</Name><String>Invented Release Group</String></Simple></Tag></Tags>
XML
printf 'CHAPTER01=00:00:00.000\nCHAPTER01NAME=Úvod\nCHAPTER02=00:00:01.000\nCHAPTER02NAME=Koniec\n' > "$work/chapters.txt"

common=(--disable-track-statistics-tags --no-date)

# English audio first, Czech dub without language tag, forced subtitles recognisable only by name, tags, chapters.
mkvmerge -q "${common[@]}" -o "$out/en-und-cz-forced.mkv" --title "Invented.Film.2026.1080p" \
  --global-tags "$work/tags.xml" --chapters "$work/chapters.txt" \
  --track-name 0:"x264 by Invented Group" "$work/v.h264" \
  --language 0:eng --track-name 0:"English 2.0" "$work/a-en.aac" \
  --language 0:und --track-name 0:"CZ Dabing" --default-track-flag 0:0 "$work/a-cz.ac3" \
  --language 0:und --track-name 0:"Forced" "$work/s-cz-forced.srt" \
  --language 0:cze --track-name 0:"Czech full" "$work/s-cz.srt" \
  --language 0:eng "$work/s-en.srt"

# Already clean Czech-only file.
mkvmerge -q "${common[@]}" -o "$out/cz-only.mkv" \
  --track-name 0:"Ab12Cd34" "$work/v.h264" \
  --language 0:cze "$work/a-cz.ac3" \
  --language 0:cze --default-track-flag 0:0 "$work/s-cz.srt"

# Foreign audio only: Czech subtitles must become the default ones.
mkvmerge -q "${common[@]}" -o "$out/en-only-cz-subs.mkv" \
  "$work/v.h264" \
  --language 0:eng "$work/a-en.aac" \
  --language 0:eng "$work/s-en.srt" \
  --language 0:cze --track-name 0:"Czech" "$work/s-cz.srt" \
  --language 0:cze --track-name 0:"Czech forced" --forced-display-flag 0:1 "$work/s-cz-forced.srt"

# Slovak and Czech audio in the wrong order, Slovak subtitles.
mkvmerge -q "${common[@]}" -o "$out/sk-cz-mixed.mkv" \
  "$work/v.h264" \
  --language 0:slo --track-name 0:"Slovensky" "$work/a-sk.ac3" \
  --language 0:cze --track-name 0:"Česky" --default-track-flag 0:0 "$work/a-cz.ac3" \
  --language 0:eng --default-track-flag 0:0 "$work/a-en.aac" \
  --language 0:slo "$work/s-sk.srt" \
  --language 0:cze "$work/s-cz.srt"

# Other containers for batch processing (owner, 27. 10. 2026: MP4, AVI, TS and others are converted to MKV).
# MP4: English AAC, Czech AC-3, Czech timed-text subtitles.
ffmpeg -loglevel error -y -fflags +genpts -r 10 -i "$work/v.h264" -i "$work/a-en.aac" -i "$work/a-cz.ac3" -i "$work/s-cz.srt" \
  -map 0 -map 1 -map 2 -map 3 -c:v copy -c:a copy -c:s mov_text \
  -metadata:s:a:0 language=eng -metadata:s:a:1 language=ces -metadata:s:s:0 language=ces "$out/en-cz.mp4"
# AVI: no language tags at all (the languages must be chosen), MP3 audio.
ffmpeg -loglevel error -y -fflags +genpts -r 10 -i "$work/v.h264" -i "$work/a-en.aac" \
  -map 0 -map 1 -c:v copy -c:a libmp3lame -b:a 32k -ar 32000 "$out/und.avi"
# MPEG transport stream (a TV recording): Slovak AC-3 first, English AAC.
ffmpeg -loglevel error -y -fflags +genpts -r 10 -i "$work/v.h264" -i "$work/a-sk.ac3" -i "$work/a-en.aac" \
  -map 0 -map 1 -map 2 -c copy -metadata:s:a:0 language=slk -metadata:s:a:1 language=eng "$out/sk-en.ts"

# Speech for the recognition of the spoken language: invented sentences read by espeak-ng (synthetic voice), three
# audio tracks without a language or name – Czech, English, Slovak.
cat > "$work/speech-cs.txt" <<'TXT'
Dobrý večer, vítejte u dnešního vymyšleného příběhu. Malá vesnice pod horami se probouzí do chladného rána. Starý mlynář otevírá dveře a dívá se na zamrzlou řeku. Říká svému synovi, že dnes musí opravit kolo, jinak nebude mouka na chleba. Syn se usměje, vezme kladivo a jde k potoku. Celý den pracují spolu a večer sedí u kamen a vyprávějí si o létě.
TXT
cat > "$work/speech-en.txt" <<'TXT'
Good evening, and welcome to tonight's invented story. A small village below the mountains wakes up to a cold morning. The old miller opens the door and looks at the frozen river. He tells his son that today they must repair the wheel, or there will be no flour for bread. The son smiles, takes a hammer and walks down to the stream. They work together all day, and in the evening they sit by the stove and talk about summer.
TXT
cat > "$work/speech-sk.txt" <<'TXT'
Dobrý večer, vitajte pri dnešnom vymyslenom príbehu. Malá dedina pod horami sa prebúdza do chladného rána. Starý mlynár otvára dvere a pozerá sa na zamrznutú rieku. Hovorí svojmu synovi, že dnes musia opraviť koleso, inak nebude múka na chlieb. Syn sa usmeje, vezme kladivo a ide k potoku. Celý deň pracujú spolu a večer sedia pri peci a rozprávajú sa o lete.
TXT
for language in cs en sk; do
  voice="$language"; [ "$language" = en ] && voice=en-us
  espeak-ng -v "$voice" -s 150 -f "$work/speech-$language.txt" -w "$work/speech-$language.wav"
  ffmpeg -loglevel error -y -i "$work/speech-$language.wav" -ac 1 -ar 16000 -c:a libopus -b:a 16k "$work/speech-$language.opus"
done
ffmpeg -loglevel error -y -f lavfi -i "testsrc=size=64x36:rate=5" -t 29 -c:v libx264 -preset ultrafast -bf 0 "$work/v-long.h264"
mkvmerge -q "${common[@]}" -o "$out/speech-und.mkv" \
  "$work/v-long.h264" \
  --language 0:und "$work/speech-cs.opus" \
  --language 0:und --default-track-flag 0:0 "$work/speech-en.opus" \
  --language 0:und --default-track-flag 0:0 "$work/speech-sk.opus"

ls -l "$out"
