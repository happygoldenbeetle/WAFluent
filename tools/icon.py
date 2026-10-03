"""WAFluent's icon: the frosted-glass speech bubble (two lines of text in it) on a full green
rounded square, as WhatsApp's own mark sits on its square. Layers, shadows and gradients as in
Microsoft's app-icon guidance; drawn on its 48 x 48 grid, centred on the square keyline.

  python tools/icon.py <output folder>

writes AppIcon.png (256), AppIcon.ico (16 to 256), AppIcon-1024.png and a sheet to look at."""
import sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter, ImageFont

OUT = sys.argv[1] if len(sys.argv) > 1 else "."
S = 56
N = 48 * S


def blank():
    return Image.new("L", (N, N), 0)


def smooth(mask, radius):
    if radius <= 0:
        return mask
    return mask.filter(ImageFilter.GaussianBlur(radius * S)).point(lambda v: 255 if v > 127 else 0).filter(ImageFilter.GaussianBlur(S * 0.03))


def px(points):
    return [(x * S, y * S) for x, y in points]


def circle(cx, cy, r):
    m = blank()
    ImageDraw.Draw(m).ellipse([(cx - r) * S, (cy - r) * S, (cx + r) * S, (cy + r) * S], fill=255)
    return m


def rrect(x0, y0, x1, y1, rad):
    m = blank()
    ImageDraw.Draw(m).rounded_rectangle([x0 * S, y0 * S, x1 * S, y1 * S], radius=rad * S, fill=255)
    return m


def poly(points, round_by=0.6):
    m = blank()
    ImageDraw.Draw(m).polygon(px(points), fill=255)
    return smooth(m, round_by)


def stroke(points, width):
    """A line with round ends and joins."""
    m = blank()
    d = ImageDraw.Draw(m)
    d.line(px(points), fill=255, width=int(width * S), joint="curve")
    for x, y in points:
        d.ellipse([(x - width / 2) * S, (y - width / 2) * S, (x + width / 2) * S, (y + width / 2) * S], fill=255)
    return m


def union(*masks):
    out = np.zeros((N, N), np.uint8)
    for m in masks:
        out = np.maximum(out, np.asarray(m))
    return Image.fromarray(out)


def bubble(cx, cy, r, tail, a=(116, 152), round_by=0.9):
    m = circle(cx, cy, r)
    a1, a2 = np.radians(a[0]), np.radians(a[1])
    ImageDraw.Draw(m).polygon(px([(cx + r * np.cos(a1), cy + r * np.sin(a1)), (cx + r * np.cos(a2), cy + r * np.sin(a2)), tail]), fill=255)
    return smooth(m, round_by)


def bar(x0, x1, y, h):
    return rrect(x0, y - h / 2, x1, y + h / 2, h / 2)


def gradient(c0, c1, lo=0.0, hi=1.0, angle_deg=120):
    a = np.radians(angle_deg - 90)
    ys, xs = np.mgrid[0:N, 0:N].astype(np.float32) / N
    t = xs * np.sin(a) + ys * np.cos(a)
    t = (t - t.min()) / (t.max() - t.min())
    t = np.clip((t - lo) / (hi - lo), 0, 1)[..., None]
    return np.array(c0, np.float32) * (1 - t) + np.array(c1, np.float32) * t


def rgba(rgb, mask, opacity=1.0):
    a = (np.asarray(mask, np.float32) / 255.0 * opacity)[..., None]
    return np.concatenate([np.broadcast_to(rgb, (N, N, 3)), a * 255.0], axis=2)


def over(dst, src):
    sa, da = src[..., 3:4] / 255.0, dst[..., 3:4] / 255.0
    oa = sa + da * (1 - sa)
    rgb = (src[..., :3] * sa + dst[..., :3] * da * (1 - sa)) / np.maximum(oa, 1e-6)
    return np.concatenate([rgb, oa * 255.0], axis=2)


def shadow(mask, dy, blur, opacity):
    moved = Image.new("L", (N, N), 0)
    moved.paste(mask, (0, int(dy * S)))
    return rgba(np.zeros(3, np.float32), moved.filter(ImageFilter.GaussianBlur(blur * S)), opacity)


def blur_rgba(img, radius):
    a = img[..., 3:4] / 255.0
    pre = Image.fromarray(np.clip(np.concatenate([img[..., :3] * a, img[..., 3:4]], axis=2), 0, 255).astype(np.uint8))
    pre = np.asarray(pre.filter(ImageFilter.GaussianBlur(radius * S)), np.float32)
    return np.concatenate([pre[..., :3] / np.maximum(pre[..., 3:4] / 255.0, 1e-6), pre[..., 3:4]], axis=2)


def edge_of(mask, width):
    inner = mask.filter(ImageFilter.GaussianBlur(width * S * 0.6)).point(lambda v: 255 if v > 232 else 0).filter(ImageFilter.GaussianBlur(S * 0.04))
    return Image.fromarray(np.clip(np.asarray(mask, np.int16) - np.asarray(inner, np.int16), 0, 255).astype(np.uint8))


def render(layers):
    art = np.zeros((N, N, 4), np.float32)
    whole = union(*[l["mask"] for l in layers])
    art = over(art, shadow(whole, 0.7, 1.1, 0.16))                 # a whisper under the whole mark
    for layer in layers:
        mask = layer["mask"]
        # Its shadow, only on what's already there.
        strength = layer.get("lift", 1.0)
        if strength > 0 and art[..., 3].max() > 0:
            cast = over(shadow(mask, 1.0 * strength, 2.0 * strength, 0.32), shadow(mask, 0.25, 0.5, 0.20))
            below = art[..., 3] / 255.0
            cast[..., 3] *= np.where(below > 0.5, 1.0, 0.0) if layer.get("hard", True) else below
            art = over(art, cast)
        if layer["kind"] == "solid":
            art = over(art, rgba(gradient(*layer["fill"], lo=0.12, hi=0.95), mask))
            light = rgba(np.full(3, 255, np.float32), edge_of(mask, layer.get("rim_width", 0.5)))
            light[..., 3] *= gradient((layer.get("rim", 0.5),), (0.0,), lo=0.1, hi=0.65)[..., 0]
            art = over(art, light)
        else:
            behind = blur_rgba(art, layer.get("blur", 2.6))
            behind[..., 3] *= layer.get("see", 0.7)
            pane = over(rgba(gradient(*layer["fill"], lo=0.15, hi=0.95), mask), behind)
            wash = rgba(gradient((255, 255, 255), (228, 255, 240), lo=0.15, hi=0.85), mask)
            tint = layer.get("tint", (0.42, 0.06))
            wash[..., 3] *= gradient((tint[0],), (tint[1],), lo=0.1, hi=0.9)[..., 0]
            pane = over(pane, wash)
            pane[..., 3] = np.asarray(mask, np.float32) * 0.985
            art = over(art, pane)
            edge = edge_of(mask, 0.42)
            bright = rgba(np.full(3, 255, np.float32), edge)
            bright[..., 3] *= gradient((0.95,), (0.18,), lo=0.1, hi=0.8)[..., 0]
            art = over(art, bright)
            dim = rgba(np.array(layer.get("edge", (6, 104, 62)), np.float32), edge)
            dim[..., 3] *= gradient((0.0,), (0.5,), lo=0.45, hi=0.95)[..., 0]
            art = over(art, dim)
    return Image.fromarray(np.clip(art, 0, 255).astype(np.uint8))


LIGHT, MID, DARK, DEEP = (104, 240, 150), (37, 211, 102), (13, 156, 84), (6, 104, 62)
GREEN, VIVID = (MID, DEEP), ((70, 228, 128), (9, 132, 74))
MINT = ((228, 253, 238), (136, 226, 176))
WHITE = ((255, 255, 255), (226, 250, 236))
INK = (DARK, DEEP)


def solid(mask, fill, **more):
    return dict(mask=mask, kind="solid", fill=fill, **more)


def glass(mask, fill=MINT, **more):
    return dict(mask=mask, kind="glass", fill=fill, **more)


def mark(mask, fill=INK):
    return solid(mask, fill, lift=0.35, rim=0.0)



def icon(bubble_at, bars, corner=10.0):
    cx, cy, r, tail = bubble_at
    return render([
        # The square: mid green at the top left to deep green at the bottom right.
        solid(rrect(3.0, 3.0, 45.0, 45.0, corner), ((58, 222, 118), (7, 112, 66)), rim=0.45, rim_width=0.55),
        # The glass bubble: the square shows through it, frosted, under a pale body lit from the top left.
        glass(bubble(cx, cy, r, tail, a=(114, 150)), ((240, 255, 246), (170, 238, 202)), see=0.5, blur=3.0, tint=(0.5, 0.1)),
        mark(bar(*bars[0])),
        mark(bar(*bars[1])),
    ])


# The full drawing, and a bolder one for 16 to 32 px: a bigger bubble and thicker lines.
big = icon((24.6, 22.8, 14.0, (10.6, 40.0)), [(17.0, 32.4, 19.4, 3.0), (17.0, 27.0, 25.8, 3.0)])
small = icon((24.6, 22.6, 15.4, (9.6, 41.0)), [(16.0, 33.2, 18.6, 4.0), (16.0, 27.4, 26.2, 4.0)], corner=10.5)


def at(size):
    return (small if size <= 32 else big).resize((size, size), Image.LANCZOS)


at(1024).save(f"{OUT}/AppIcon-1024.png")
at(256).save(f"{OUT}/AppIcon.png")
sizes = [16, 20, 24, 32, 40, 48, 64, 256]
at(256).save(f"{OUT}/AppIcon.ico", sizes=[(s, s) for s in sizes], append_images=[at(s) for s in sizes[:-1]])

sheet = Image.new("RGB", (1500, 520), (243, 243, 243))
ImageDraw.Draw(sheet).rectangle([750, 0, 1500, 520], fill=(32, 32, 32))
for x0 in (0, 750):
    sheet.paste(at(384), (x0 + 40, 60), at(384))
    x = x0 + 470
    for s in (96, 48, 32, 24, 16):
        sheet.paste(at(s), (x, 180 - s // 2), at(s))
        x += s + 26
    sheet.paste(at(160), (x0 + 470, 280), at(160))
sheet.save(f"{OUT}/icon-sheet.png")
print("drawn")
