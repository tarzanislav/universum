#!/usr/bin/env python3
"""Generates the small, invented WebP test pictures in testdata/pictures (CC0) and their references.

The pictures are generated test patterns (ffmpeg's testsrc2, mandelbrot, drawn boxes), never real posters or photos.
Encoded by ffmpeg's libwebp and libvpx; the references come from other decoders than UNIVERSUM's own: the planes of a
lossy picture from ffmpeg's VP8 decoder, the pixels of the others from libwebp (Pillow).
Requires ffmpeg (with libwebp and libvpx) and Pillow. Run from the repository root: python3 tools/make-test-pictures.py
"""
import hashlib
import os
import struct
import subprocess
import tempfile

from PIL import Image

OUT = "testdata/pictures"
os.makedirs(OUT, exist_ok=True)
work = tempfile.mkdtemp()
references = []


def ffmpeg(*args):
    subprocess.run(["ffmpeg", "-loglevel", "error", "-y", *args], check=True)


def planes(path):
    # ffmpeg's own VP8 decoder: Y, then U and V of half the size (rounded up)
    return subprocess.run(["ffmpeg", "-loglevel", "error", "-i", path, "-frames:v", "1", "-f", "rawvideo", "-pix_fmt", "yuv420p", "-"],
                          capture_output=True, check=True).stdout


def pixels(path):
    # libwebp (Pillow): blue, green, red, alpha – the first frame of an animation laid on its canvas
    image = Image.open(path)
    image.seek(0)
    r, g, b, a = image.convert("RGBA").split()
    return Image.merge("RGBA", (b, g, r, a)).tobytes()


def note(name, kind, data):
    references.append(f"{name} {kind} {hashlib.sha256(data).hexdigest()}")


def vp8_key_frame(source, name, *options):
    # one key frame of libvpx (partitions, the simple filter, filter deltas) put into a WebP file of its own
    ivf = os.path.join(work, "frame.ivf")
    ffmpeg("-i", source, "-frames:v", "1", "-c:v", "libvpx", *options, "-f", "ivf", ivf)
    data = open(ivf, "rb").read()
    size = struct.unpack("<I", data[32:36])[0]
    frame = data[44:44 + size]
    chunk = b"VP8 " + struct.pack("<I", len(frame)) + frame + (b"\0" if len(frame) & 1 else b"")
    path = os.path.join(OUT, name + ".webp")
    open(path, "wb").write(b"RIFF" + struct.pack("<I", 4 + len(chunk)) + b"WEBP" + chunk)
    note(name, "yuv", subprocess.run(["ffmpeg", "-loglevel", "error", "-i", ivf, "-f", "rawvideo", "-pix_fmt", "yuv420p", "-"],
                                     capture_output=True, check=True).stdout)


pattern = os.path.join(work, "pattern.png")
ffmpeg("-f", "lavfi", "-i", "testsrc2=size=95x71", "-frames:v", "1", pattern)
fractal = os.path.join(work, "fractal.png")
ffmpeg("-f", "lavfi", "-i", "mandelbrot=size=160x144", "-frames:v", "1", fractal)

# lossy: the normal loop filter, 4 segments; another preset; a picture of one macroblock row
for name, source, options in (("lossy", pattern, ["-quality", "60"]),
                              ("lossy-drawing", fractal, ["-preset", "drawing", "-quality", "30"]),
                              ("lossy-thin", pattern, ["-quality", "90", "-vf", "crop=94:10:0:0"])):
    path = os.path.join(OUT, name + ".webp")
    ffmpeg("-i", source, *options, "-c:v", "libwebp", path)
    note(name, "yuv", planes(path))

vp8_key_frame(fractal, "vp8-partitions", "-slices", "8", "-qmin", "50", "-qmax", "63", "-b:v", "40k")
vp8_key_frame(fractal, "vp8-simple-filter", "-profile:v", "1", "-qmin", "40", "-qmax", "60", "-b:v", "40k")

# lossy with alpha (ALPH compressed losslessly, filtered); the colours are compared with a tolerance
path = os.path.join(OUT, "lossy-alpha.webp")
ffmpeg("-i", pattern, "-vf", "format=yuva420p,geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='if(lt(X,20),0,min(255,Y*4))'",
       "-c:v", "libwebp", "-quality", "70", path)
open(os.path.join(OUT, "lossy-alpha.bgra"), "wb").write(pixels(path))

# lossless: transforms and colour cache; with alpha; palettes of 2 and 16 colours (packed pixels); an animation
for name, source, options in (("lossless", fractal, ["-lossless", "1", "-compression_level", "6"]),
                              ("lossless-fast", pattern, ["-lossless", "1", "-compression_level", "0"]),
                              ("lossless-alpha", pattern, ["-lossless", "1", "-vf", "format=yuva420p,geq=lum='lum(X,Y)':cb='cb(X,Y)':cr='cr(X,Y)':a='X*3'", "-pix_fmt", "bgra"]),
                              ("palette2", pattern, ["-lossless", "1", "-vf", "format=gray,lutyuv=y='if(gt(val,128),255,0)',format=bgra"]),
                              ("palette16", pattern, ["-lossless", "1", "-vf", "lutrgb=r='floor(val/128)*255':g='floor(val/128)*255':b='floor(val/64)*85',format=bgra"])):
    path = os.path.join(OUT, name + ".webp")
    ffmpeg("-i", source, *options, "-c:v", "libwebp", path)
    note(name, "bgra", pixels(path))

path = os.path.join(OUT, "animation.webp")
ffmpeg("-f", "lavfi", "-i", "testsrc2=size=48x40:rate=5", "-frames:v", "3", "-c:v", "libwebp_anim", "-lossless", "1", "-loop", "0", path)
note("animation", "bgra", pixels(path))

open(os.path.join(OUT, "references.txt"), "w").write("\n".join(references) + "\n")
print("\n".join(references))
