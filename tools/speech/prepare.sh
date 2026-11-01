#!/usr/bin/env bash
# Prepares the "speech" folder installed with UNIVERSUM (recognition of the spoken language, decided by the owner on
# 28. 10. 2026: part of the installation, FFmpeg whole):
#   ffmpeg/   FFmpeg (LGPL 2.1+) built here from its source at a fixed commit – ffmpeg.exe, ffprobe.exe, licence, SOURCE.txt
#   whisper/  whisper.cpp (MIT) – the official Windows build, checked against a fixed SHA-256
#   models/   the speech model "large-v3-turbo" q5_0 (MIT) – the official file at a fixed revision, checked against a fixed SHA-256
#   manifest.json  SHA-256 of every file; UNIVERSUM starts nothing that does not match it
# and, for the release, the complete FFmpeg source with the build script (the LGPL asks for it to be offered with
# the program).
#   tools/speech/prepare.sh <speech folder> <source folder>
# Needs (Ubuntu): mingw-w64 nasm libz-mingw-w64-dev curl unzip xz-utils python3 git
set -euo pipefail

FFMPEG_VERSION=9.0.2
FFMPEG_COMMIT=946fcce07b6dcd0331c8cc609192aeff5e1924f8
WHISPER_VERSION=1.9.2
WHISPER_COMMIT=306c88f4d1286aec1bf96e544632897886af5501
WHISPER_ZIP_SHA256=49dcc16de826f20bd53d44f947a1ae49dfa81f86cad67a64d80820cb192d674a
MODEL_REVISION=98aa99a0a9db05ae2342309f5096248665f7cba3
MODEL_NAME=ggml-large-v3-turbo-q5_0.bin
MODEL_SHA256=394221709cd5ad1f40c46e6031ca61bce88931e6e088c188294c6d5a55ffa7e2

here="$(cd "$(dirname "$0")" && pwd)"
out="$(mkdir -p "$1" && cd "$1" && pwd)"
source_out="$(mkdir -p "$2" && cd "$2" && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$out/ffmpeg" "$out/whisper" "$out/models"

download() { # address, target, SHA-256
  curl -fsSL --proto '=https' --tlsv1.2 --retry 3 -o "$2" "$1"
  echo "$3  $2" | sha256sum -c --quiet - || { echo "SHA-256 of $1 differs from the pinned value" >&2; exit 1; }
}

clone() { # repository, tag, commit, folder
  git -c advice.detachedHead=false clone -q --depth 1 --branch "$2" "$1" "$4"
  [ "$(git -C "$4" rev-parse HEAD)" = "$3" ] || { echo "$1 $2 is not commit $3" >&2; exit 1; }
}

# FFmpeg: official repository (mirror of git.ffmpeg.org) at the release commit
clone https://github.com/FFmpeg/FFmpeg "n$FFMPEG_VERSION" "$FFMPEG_COMMIT" "$work/ffmpeg"
"$here/build-ffmpeg.sh" "$work/ffmpeg" "$work/ffmpeg-build"
cp "$work/ffmpeg-build/ffmpeg.exe" "$work/ffmpeg-build/ffprobe.exe" "$out/ffmpeg/"
cp "$work/ffmpeg/COPYING.LGPLv2.1" "$work/ffmpeg/LICENSE.md" "$out/ffmpeg/"
zlib_version="$(sed -n 's/^#define ZLIB_VERSION "\(.*\)"$/\1/p' /usr/x86_64-w64-mingw32/include/zlib.h)"
cat > "$out/ffmpeg/SOURCE.txt" <<TXT
FFmpeg $FFMPEG_VERSION (https://ffmpeg.org), licence: GNU Lesser General Public License 2.1 or later (COPYING.LGPLv2.1).
Built for UNIVERSUM from the official source, commit $FFMPEG_COMMIT (tag n$FFMPEG_VERSION),
with mingw-w64 (GCC) and zlib $zlib_version (zlib licence) linked statically, by tools/speech/build-ffmpeg.sh.

The complete source of this build (FFmpeg and the build script) is attached to every UNIVERSUM release on GitHub
as ffmpeg-$FFMPEG_VERSION-source.tar.xz: https://github.com/tarzanislav/universum/releases
FFmpeg's own source: https://git.ffmpeg.org/ffmpeg.git (commit $FFMPEG_COMMIT), https://ffmpeg.org/download.html

configure: $(cat "$work/ffmpeg-build/ffmpeg-configuration.txt")
TXT
git -C "$work/ffmpeg" archive --format=tar --prefix="ffmpeg-$FFMPEG_VERSION/" HEAD > "$work/ffmpeg-source.tar"
mkdir -p "$work/build-script/ffmpeg-$FFMPEG_VERSION/universum-build"
cp "$here/build-ffmpeg.sh" "$work/build-script/ffmpeg-$FFMPEG_VERSION/universum-build/"
cp "$out/ffmpeg/SOURCE.txt" "$work/build-script/ffmpeg-$FFMPEG_VERSION/universum-build/"
tar -rf "$work/ffmpeg-source.tar" -C "$work/build-script" "ffmpeg-$FFMPEG_VERSION/universum-build"
xz -9 -T0 -c "$work/ffmpeg-source.tar" > "$source_out/ffmpeg-$FFMPEG_VERSION-source.tar.xz"

# whisper.cpp: official Windows build (CPU, all CPU variants) and the licence from the release commit
download "https://github.com/ggml-org/whisper.cpp/releases/download/v$WHISPER_VERSION/whisper-bin-x64.zip" "$work/whisper.zip" "$WHISPER_ZIP_SHA256"
unzip -q "$work/whisper.zip" -d "$work/whisper"
cli="$(find "$work/whisper" -name whisper-cli.exe | head -n 1)"
[ -n "$cli" ] || { echo "whisper-cli.exe is not in the whisper.cpp build" >&2; exit 1; }
cp "$cli" "$out/whisper/"
find "$(dirname "$cli")" -maxdepth 1 -name '*.dll' ! -iname 'SDL2.dll' -exec cp {} "$out/whisper/" \;
clone https://github.com/ggml-org/whisper.cpp "v$WHISPER_VERSION" "$WHISPER_COMMIT" "$work/whisper-source"
cp "$work/whisper-source/LICENSE" "$out/whisper/LICENSE"
printf 'whisper.cpp %s (https://github.com/ggml-org/whisper.cpp), MIT licence (LICENSE).\nOfficial Windows build whisper-bin-x64.zip, SHA-256 %s.\n' \
  "$WHISPER_VERSION" "$WHISPER_ZIP_SHA256" > "$out/whisper/SOURCE.txt"

# the speech model "large-v3-turbo" (multilingual, quantised q5_0 – owner, 1. 11. 2026: "base" mixed up Czech and
# Slovak): OpenAI Whisper weights converted for whisper.cpp
download "https://huggingface.co/ggerganov/whisper.cpp/resolve/$MODEL_REVISION/$MODEL_NAME" "$out/models/$MODEL_NAME" "$MODEL_SHA256"
cat > "$out/models/LICENSE" <<'TXT'
The speech model ggml-large-v3-turbo-q5_0.bin is OpenAI's Whisper model "large-v3-turbo" converted for whisper.cpp
and quantised to q5_0
(https://huggingface.co/ggerganov/whisper.cpp, https://github.com/openai/whisper).

MIT License

Copyright (c) 2022 OpenAI

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
TXT
printf 'Model revision %s, SHA-256 %s.\n' "$MODEL_REVISION" "$MODEL_SHA256" > "$out/models/SOURCE.txt"

# the manifest: SHA-256 of every file (paths with "/")
python3 - "$out" <<'PY'
import hashlib, json, os, sys
root = sys.argv[1]
files = {}
for folder, _, names in os.walk(root):
    for name in names:
        path = os.path.join(folder, name)
        relative = os.path.relpath(path, root).replace(os.sep, "/")
        if relative == "manifest.json":
            continue
        with open(path, "rb") as f:
            files[relative] = hashlib.file_digest(f, "sha256").hexdigest().upper()
with open(os.path.join(root, "manifest.json"), "w", encoding="utf-8") as f:
    json.dump({"files": dict(sorted(files.items()))}, f, indent=2)
PY
du -sh "$out"/* "$source_out"/*
