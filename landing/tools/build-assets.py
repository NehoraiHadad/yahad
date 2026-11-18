"""Regenerate the landing page's derived static assets.

This is a one-off maintenance tool, not a build step: its outputs are committed
under dist/assets/ and the page is served exactly as stored.

    python -X utf8 tools/build-assets.py --hebrew NotoSansHebrew[wdth,wght].ttf --latin NotoSans[wdth,wght].ttf

Font sources are the variable Noto fonts from https://github.com/google/fonts
(ofl/notosanshebrew, ofl/notosans; SIL OFL 1.1, see dist/assets/FONT-LICENSE.txt).
Requires: fonttools, brotli, pillow.
"""
import argparse
import hashlib
import io
from pathlib import Path

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer
from PIL import Image, ImageDraw, ImageFont

ASSETS = Path(__file__).resolve().parents[1] / 'dist' / 'assets'
WEIGHTS = (400, 900)

# Each script's face carries only its own characters; the @font-face rules in
# style.css declare the same unicode-range so the browser picks per character.
RANGES = {
    'hebrew': ['U+0590-05FF', 'U+200C-200F', 'U+20AA', 'U+25CC', 'U+FB1D-FB4F'],
    'latin': ['U+0020-007E', 'U+00A0', 'U+00A9', 'U+00AB', 'U+00B7', 'U+00BB', 'U+00D7', 'U+2010-2027', 'U+2030-203A', 'U+20AC', 'U+2122', 'U+2190-2193'],
}

INK, PAPER, PASS, MUTED = (16, 35, 30), (242, 245, 244), (17, 107, 90), (90, 106, 102)


def unicodes(ranges):
    out = []
    for r in ranges:
        lo, _, hi = r[2:].partition('-')
        out.extend(range(int(lo, 16), int(hi or lo, 16) + 1))
    return out


def build_font(source, script):
    font = TTFont(source, lazy=False, recalcTimestamp=False)  # reproducible output: keep the source head.modified
    axes = {a.axisTag: (a.minValue, a.maxValue) for a in font['fvar'].axes}
    assert axes['wght'][0] <= WEIGHTS[0] and axes['wght'][1] >= WEIGHTS[1], (script, axes)
    font = instancer.instantiateVariableFont(font, {'wdth': 100, 'wght': WEIGHTS})
    buffer = io.BytesIO()
    font.save(buffer)  # round-trip so the subsetter sees fully compiled tables
    buffer.seek(0)
    font = TTFont(buffer, lazy=False, recalcTimestamp=False)
    options = subset.Options()
    options.flavor = 'woff2'
    options.layout_features = ['*']
    options.name_IDs = ['*']
    options.notdef_outline = True
    sub = subset.Subsetter(options)
    sub.populate(unicodes=unicodes(RANGES[script]))
    sub.subset(font)
    out = ASSETS / f'{script}.woff2'
    font.flavor = 'woff2'
    font.save(out)
    print(f'{out.name}: {out.stat().st_size} bytes from {Path(source).name} sha256={hashlib.sha256(Path(source).read_bytes()).hexdigest()}')
    return out


def rtl(text):
    # face() pins Pillow's BASIC layout, which draws code points left-to-right with
    # no bidi. These lines are Hebrew letters and trailing punctuation only, so
    # reversing yields the correct visual order (RAQM is never used, even if present).
    assert not any(c.isascii() and c.isalnum() for c in text), text
    return text[::-1]


def build_images(hebrew_source):
    art = Image.open(ASSETS / 'friends-at-the-park.webp').convert('RGBA')
    small = art.resize((768, 512), Image.LANCZOS)
    small.save(ASSETS / 'friends-at-the-park-768.webp', 'WEBP', quality=82, method=6)

    def face(weight, size):
        f = ImageFont.truetype(hebrew_source, size, layout_engine=ImageFont.Layout.BASIC)
        f.set_variation_by_axes([weight, 100])  # fvar order: wght, wdth
        return f

    og = Image.new('RGB', (1200, 630), PAPER)
    piece = art.resize((630, 420), Image.LANCZOS)
    og.paste(piece, (24, 150), piece)
    d = ImageDraw.Draw(og)

    def right(y, text, font, fill, x=1136):
        t = rtl(text)
        d.text((x - d.textlength(t, font=font), y), t, font=font, fill=fill)

    right(96, 'יחד', face(900, 132), INK)
    right(300, 'הילדים מדברים.', face(800, 54), INK)
    right(370, 'אתם בוחרים.', face(800, 54), INK)
    right(472, 'מתכתבים בשיחות שאישרתם.', face(400, 32), MUTED)
    og.save(ASSETS / 'og-friends.jpg', 'JPEG', quality=86, optimize=True, progressive=True)

    icon = Image.new('RGB', (180, 180), PASS)
    di = ImageDraw.Draw(icon)
    f, t = face(900, 76), rtl('יחד')
    box = di.textbbox((0, 0), t, font=f)
    di.text(((180 - (box[2] - box[0])) / 2 - box[0], (180 - (box[3] - box[1])) / 2 - box[1]), t, font=f, fill=(255, 255, 255))
    icon.save(ASSETS / 'apple-touch-icon.png', optimize=True)
    for name in ('friends-at-the-park-768.webp', 'og-friends.jpg', 'apple-touch-icon.png'):
        print(f'{name}: {(ASSETS / name).stat().st_size} bytes')


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--hebrew', required=True)
    parser.add_argument('--latin', required=True)
    args = parser.parse_args()
    build_font(args.hebrew, 'hebrew')
    build_font(args.latin, 'latin')
    build_images(args.hebrew)
