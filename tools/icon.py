"""WAFluent's icon, drawn the way Microsoft's app-icon guidance describes: a few flat layers
stacked front to back, soft drop shadows between them, gentle 120-degree gradients with the
light at the top left, one metaphor (a conversation), no lettering.

Back layer : a deep green speech bubble.
Front layer: a frosted-glass speech bubble with its tail at the bottom left and two lines of
             "text", the app's old mark. What's behind it shows through, blurred.

Everything is laid out on Microsoft's 48 x 48 grid and drawn at S pixels per grid unit.
"""
import sys
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

OUT = sys.argv[1] if len(sys.argv) > 1 else "."
S = 64                      # pixels per grid unit while drawing (48 units -> 3072 px)
N = 48 * S


def canvas():
    return Image.new("L", (N, N), 0)


def smooth(mask, radius):
    """Rounds every corner of a shape (inside and outside ones) by `radius` grid units."""
    r = radius * S
    return mask.filter(ImageFilter.GaussianBlur(r)).point(lambda v: 255 if v > 127 else 0).filter(ImageFilter.GaussianBlur(S * 0.02))


def bubble(cx, cy, r, tail=None, corner=1.0):
    """A round speech bubble; `tail` is the tip of its tail."""
    m = canvas()
    d = ImageDraw.Draw(m)
    d.ellipse([(cx - r) * S, (cy - r) * S, (cx + r) * S, (cy + r) * S], fill=255)
    if tail is not None:
        tx, ty = tail
        # The tail leaves the bubble's lower left: a wedge from two points on the rim to the tip.
        a1, a2 = np.radians(116), np.radians(152)
        p1 = (cx + r * np.cos(a1), cy + r * np.sin(a1))
        p2 = (cx + r * np.cos(a2), cy + r * np.sin(a2))
        d.polygon([(p1[0] * S, p1[1] * S), (p2[0] * S, p2[1] * S), (tx * S, ty * S)], fill=255)
    return smooth(m, corner)


def gradient(c_top_left, c_bottom_right, angle_deg=120, lo=0.0, hi=1.0):
    """A straight colour ramp across the canvas, light end at the top left (Microsoft's 120 degrees)."""
    a = np.radians(angle_deg - 90)          # 120 degrees: mostly downwards, leaning right
    ys, xs = np.mgrid[0:N, 0:N].astype(np.float32) / N
    t = xs * np.sin(a) + ys * np.cos(a)
    t = (t - t.min()) / (t.max() - t.min())
    t = np.clip((t - lo) / (hi - lo), 0, 1)[..., None]
    c0, c1 = np.array(c_top_left, np.float32), np.array(c_bottom_right, np.float32)
    return c0 * (1 - t) + c1 * t


def rgba(rgb, mask, opacity=1.0):
    a = (np.asarray(mask, np.float32) / 255.0 * opacity)[..., None]
    return np.concatenate([rgb, a * 255.0], axis=2)


def over(dst, src):
    """Straight-alpha 'source over' for float RGBA arrays (0-255)."""
    sa, da = src[..., 3:4] / 255.0, dst[..., 3:4] / 255.0
    oa = sa + da * (1 - sa)
    rgb = (src[..., :3] * sa + dst[..., :3] * da * (1 - sa)) / np.maximum(oa, 1e-6)
    return np.concatenate([rgb, oa * 255.0], axis=2)


def shadow(mask, dy, blur, opacity, dx=0.0):
    """A layer's drop shadow: its shape, moved and blurred (values in grid units, as the guide gives them at 48 px)."""
    moved = Image.new("L", (N, N), 0)
    moved.paste(mask, (int(dx * S), int(dy * S)))
    soft = moved.filter(ImageFilter.GaussianBlur(blur * S))
    return rgba(np.zeros((N, N, 3), np.float32), soft, opacity)


def blur_rgba(img, radius):
    """Blurs a float RGBA picture without dark fringes (colour is blurred weighted by its alpha)."""
    a = img[..., 3:4] / 255.0
    pre = Image.fromarray(np.clip(np.concatenate([img[..., :3] * a, img[..., 3:4]], axis=2), 0, 255).astype(np.uint8))
    pre = np.asarray(pre.filter(ImageFilter.GaussianBlur(radius * S)), np.float32)
    pa = pre[..., 3:4] / 255.0
    return np.concatenate([pre[..., :3] / np.maximum(pa, 1e-6), pre[..., 3:4]], axis=2)


def inset(mask, by):
    """The shape pulled in from its edge by `by` grid units."""
    return mask.filter(ImageFilter.GaussianBlur(by * S * 0.6)).point(lambda v: 255 if v > 232 else 0).filter(ImageFilter.GaussianBlur(S * 0.03))


def bar(x0, x1, y, h):
    m = canvas()
    ImageDraw.Draw(m).rounded_rectangle([x0 * S, (y - h / 2) * S, x1 * S, (y + h / 2) * S], radius=h / 2 * S, fill=255)
    return m


def draw(g):
    """The icon for one geometry (see FULL and SMALL below), as a big RGBA picture."""
    # ───── The two layers (grid units) ─────
    back = bubble(*g["back"], corner=0.6)                       # behind, up and to the right
    front = bubble(*g["front"], tail=g["tail"], corner=g["corner"])    # in front, down and to the left, with the tail

    # Greens: one hue in three tones (light, mid, dark), as the guide's monochrome palette.
    LIGHT, MID, DARK, DEEP = (104, 240, 150), (37, 211, 102), (13, 156, 84), (6, 104, 62)

    art = np.zeros((N, N, 4), np.float32)

    # A whisper of shadow under the whole mark, so it sits on a light desktop too.
    art = over(art, shadow(ImageChopsMax := Image.fromarray(np.maximum(np.asarray(back), np.asarray(front))), 0.7, 1.1, 0.16))

    # Back layer: mid to deep green.
    art = over(art, rgba(gradient(MID, DEEP, lo=0.15, hi=0.95), back))
    # A soft light along its top-left edge (ambient light from the top left, not a reflection).
    rim_back = Image.fromarray(np.clip(np.asarray(back, np.int16) - np.asarray(inset(back, 0.55), np.int16), 0, 255).astype(np.uint8))
    art = over(art, rgba(gradient((190, 255, 215), (60, 200, 120), lo=0.1, hi=0.6), rim_back, 0.55))

    # The front layer's shadow, cast only on the layer behind it ("separate objects": masked into the shape below).
    cast = over(shadow(front, 1.0, 2.0, 0.34), shadow(front, 0.25, 0.5, 0.22))
    cast[..., 3] *= np.asarray(back, np.float32) / 255.0
    art = over(art, cast)

    # Front layer: frosted glass. What's behind it, blurred, under a pale mint tint that's lighter at the top left.
    behind = blur_rgba(art, 2.6)
    glass = rgba(gradient((226, 253, 236), (132, 224, 172), lo=0.15, hi=0.95), front)         # the glass's own body
    seen = behind.copy()
    seen[..., 3] *= 0.68                                                                     # how much shows through
    glass_rgb = over(glass, seen)
    tint = rgba(gradient((255, 255, 255), (214, 255, 232), lo=0.15, hi=0.85), front, 1.0)
    tint[..., 3] *= gradient((0.44,), (0.06,), lo=0.1, hi=0.9)[..., 0]
    glass_rgb = over(glass_rgb, tint)
    glass_rgb[..., 3] = np.asarray(front, np.float32) * 0.985
    art = over(art, glass_rgb)

    # The glass's edge: a bright line at the top left fading to a faint one at the bottom right.
    edge = Image.fromarray(np.clip(np.asarray(front, np.int16) - np.asarray(inset(front, 0.42), np.int16), 0, 255).astype(np.uint8))
    edge_layer = rgba(np.full((N, N, 3), 255, np.float32), edge, 1.0)
    edge_layer[..., 3] *= gradient((0.95,), (0.18,), lo=0.1, hi=0.8)[..., 0]
    art = over(art, edge_layer)

    # And a faint darker line on the side away from the light, so the glass keeps its shape on a pale desktop.
    shade = rgba(np.full((N, N, 3), 0, np.float32) + np.array(DEEP, np.float32), edge, 1.0)
    shade[..., 3] *= gradient((0.0,), (0.55,), lo=0.45, hi=0.95)[..., 0]
    art = over(art, shade)

    # The two lines of text: the most prominent layer carries the only detail. Deep green, with the faintest shadow.
    lines = Image.fromarray(np.maximum(np.asarray(bar(*g["bars"][0])), np.asarray(bar(*g["bars"][1]))))
    soft = shadow(lines, 0.35, 0.5, 0.20)
    soft[..., 3] *= np.asarray(front, np.float32) / 255.0
    art = over(art, soft)
    art = over(art, rgba(gradient(DARK, DEEP, lo=0.2, hi=0.8), lines))



    return Image.fromarray(np.clip(art, 0, 255).astype(np.uint8))


# The full drawing, and a bolder one for 16 to 32 px (the guide: keep it legible small; detail only on the front layer):
# the layers sit closer together, the front bubble fills more of the square and its lines are thicker.
FULL = dict(back=(29.0, 18.5, 15.0), front=(19.5, 27.5, 15.5), tail=(4.6, 44.4), corner=0.9,
            bars=[(11.6, 27.4, 23.6, 3.0), (11.6, 22.0, 30.2, 3.0)])
SMALL = dict(back=(31.0, 17.0, 14.5), front=(21.0, 26.0, 18.0), tail=(3.4, 45.2), corner=1.0,
             bars=[(11.5, 30.5, 21.5, 4.4), (11.5, 24.0, 30.5, 4.4)])
big, small = draw(FULL), draw(SMALL)


def at(size):
    return (small if size <= 32 else big).resize((size, size), Image.LANCZOS)


at(1024).save(f"{OUT}/AppIcon-1024.png")
at(256).save(f"{OUT}/AppIcon.png")
sizes = [16, 20, 24, 32, 40, 48, 64, 256]
at(256).save(f"{OUT}/AppIcon.ico", sizes=[(s, s) for s in sizes], append_images=[at(s) for s in sizes[:-1]])

# A sheet to look at: big on light and dark, and the small sizes the taskbar and title bar use.
sheet = Image.new("RGB", (1500, 620), (243, 243, 243))
ImageDraw.Draw(sheet).rectangle([750, 0, 1500, 620], fill=(32, 32, 32))
for x0 in (0, 750):
    sheet.paste(at(384), (x0 + 40, 40), at(384))
    x = x0 + 470
    for s in (96, 48, 32, 24, 16):
        sheet.paste(at(s), (x, 232 - s // 2), at(s))
        x += s + 26
    sheet.paste(at(160), (x0 + 470, 330), at(160))
sheet.save(f"{OUT}/icon-sheet.png")
print("drawn")
