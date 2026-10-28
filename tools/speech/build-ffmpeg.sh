#!/usr/bin/env bash
# Builds FFmpeg for 64-bit Windows (cross-compiled with mingw-w64 on Linux): the whole program with all of FFmpeg's own
# components, LGPL (no --enable-gpl, no external libraries but zlib; Windows' own SChannel, DXVA2, D3D11VA and Media
# Foundation). UNIVERSUM uses it to read the audio of video files for the recognition of the spoken language, and
# later for more. Needs: mingw-w64, nasm, libz-mingw-w64-dev.
#   tools/speech/build-ffmpeg.sh <FFmpeg source folder> <output folder>
set -euo pipefail

source_dir="$(cd "$1" && pwd)"
output="$(mkdir -p "$2" && cd "$2" && pwd)"
build="$(mktemp -d)"
trap 'rm -rf "$build"' EXIT

cd "$build"
"$source_dir/configure" \
  --enable-cross-compile --cross-prefix=x86_64-w64-mingw32- --arch=x86_64 --target-os=mingw32 \
  --disable-autodetect --enable-zlib --enable-w32threads \
  --enable-schannel --enable-d3d11va --enable-dxva2 --enable-mediafoundation \
  --disable-ffplay --disable-doc --disable-debug \
  --extra-ldflags=-static
make -j"$(nproc 2>/dev/null || echo 4)"
cp ffmpeg.exe ffprobe.exe "$output/"
grep -E '^(CONFIG_GPL|CONFIG_NONFREE|CONFIG_VERSION3) ' config.h | tee "$output/ffmpeg-license-config.txt"
if grep -qE '^#define (CONFIG_GPL|CONFIG_NONFREE) 1' config.h; then echo "not an LGPL build" >&2; exit 1; fi
sed -n 's/^#define FFMPEG_CONFIGURATION "\(.*\)"$/\1/p' config.h > "$output/ffmpeg-configuration.txt"
ls -l "$output"
