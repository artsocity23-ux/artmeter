# Turns the frames written by Demo.cs into looping GIFs: demo\make-demo.cmd runs it. Needs Pillow (pip install pillow).
# Usage: make-gif.py <frames dir> <docs dir>
import glob
import os
import sys

from PIL import Image

FRAME_MS = 500  # one sample per frame, twice as fast as the app


def make(frames_dir, prefix, out):
    frames = [Image.open(p).convert('RGB') for p in sorted(glob.glob(os.path.join(frames_dir, prefix + '-*.png')))]
    w, h = frames[0].size
    # One palette for the whole loop, without dithering, so nothing that stays still flickers between frames.
    sheet = Image.new('RGB', (w, h * len(frames)))
    for i, f in enumerate(frames):
        sheet.paste(f, (0, i * h))
    palette = sheet.quantize(256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.NONE)
    q = [f.quantize(palette=palette, dither=Image.Dither.NONE) for f in frames]
    # Pillow stores only the changed rectangle of each frame and merges repeated frames.
    q[0].save(out, save_all=True, append_images=q[1:], duration=FRAME_MS, loop=0, disposal=1)
    print('%s: %dx%d, %d frames, %.1f s, %d KB' % (out, w, h, len(frames), len(frames) * FRAME_MS / 1000, os.path.getsize(out) // 1024))


make(sys.argv[1], 'full', os.path.join(sys.argv[2], 'demo.gif'))
make(sys.argv[1], 'compact', os.path.join(sys.argv[2], 'demo-compact.gif'))
