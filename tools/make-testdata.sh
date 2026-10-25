#!/usr/bin/env bash
# Generates the small, invented test MKV files in testdata/media (CC0).
# Requires ffmpeg and mkvmerge. Run from the repository root: tools/make-testdata.sh
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

ls -l "$out"
