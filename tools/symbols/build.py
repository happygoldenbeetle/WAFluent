"""
Builds src/Assets/Fonts/WAFluentSymbols.ttf and src/Helpers/Sf.cs from Framework7 Icons
(MIT, iOS / SF Symbols style; see LICENSE-Framework7Icons.txt).

Framework7 Icons only reaches its glyphs through ligatures ("chat_bubble_fill"), which
XAML icons can't use. This copy:
  * gives every icon its own private-use code point in the basic plane (XAML's &#x...; can't
    reach U+F0000 and up), picked from those Segoe Fluent Icons / MDL2 Assets leave unused
    and frozen in codepoints.json so they never move; Helpers/Sf.cs names them;
  * also puts icons on the Segoe Fluent Icons code points WAFluent and WinUI's own control
    templates use (REMAP below), so the app's existing glyphs and the system's menu chevrons,
    checkmarks, dropdown arrows... all draw SF-style. App.xaml makes this font the symbol
    font with Segoe Fluent Icons as fallback, so anything not remapped still draws;
  * uses Segoe Fluent Icons' metrics (2048 units, ascent 2048, descent 0), scaling
    Framework7's 512-unit glyphs onto it so they look as big as Segoe's at the same FontSize;
  * softens every corner (round()): outer corners and line ends get a ROUND_OUT radius, inner
    corners ROUND_IN, both well under half the ~135-unit stroke so no detail disappears.

Run: python tools/symbols/build.py   (needs fontTools and shapely)
"""
import json
import os
import re

from fontTools.pens.basePen import BasePen
from fontTools.pens.transformPen import TransformPen
from fontTools.pens.ttGlyphPen import TTGlyphPen
from shapely.geometry import MultiPolygon, Polygon
from shapely.geometry.polygon import orient
from fontTools.ttLib import TTFont, newTable
from fontTools.ttLib.tables._c_m_a_p import cmap_format_4, cmap_format_12

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))
SOURCE = os.path.join(HERE, 'Framework7Icons-Regular.ttf')
FONT = os.path.join(ROOT, 'src', 'Assets', 'Fonts', 'WAFluentSymbols.ttf')
CLASS = os.path.join(ROOT, 'src', 'Helpers', 'Sf.cs')
CODEPOINTS = os.path.join(HERE, 'codepoints.json')
SEGOE = ['C:/Windows/Fonts/SegoeIcons.ttf', 'C:/Windows/Fonts/segmdl2.ttf']
FAMILY = 'WAFluent Symbols'
ROUND_OUT = 64   # font units (2048 per em); strokes are ~135-165, so under half
ROUND_IN = 64

# Segoe Fluent Icons code point -> Framework7 icon.
REMAP = {
    # ── WAFluent (Helpers/Glyphs.cs and the XAML) ──
    0xE8BD: 'chat_bubble_2',             # Chats (rail)
    0xE717: 'phone',
    0xE714: 'videocam',
    0xE715: 'envelope',
    0xEB9F: 'photo',
    0xEA3A: 'smallcircle_circle',        # Status
    0xE734: 'star',
    0xE735: 'star_fill',
    0xE7B8: 'archivebox',
    0xE713: 'gear_alt',
    0xE77B: 'person_crop_circle',
    0xE720: 'mic',
    0xF781: 'mic_slash',
    0xE8A5: 'doc',
    0xE81D: 'location',
    0xE9D5: 'chart_bar_alt_fill',        # Poll
    0xE768: 'play',
    0xE769: 'pause',
    0xE7BA: 'exclamationmark_triangle',
    0xE8C8: 'doc_on_doc',                # Copy
    0xE74E: 'square_arrow_down',         # Save as
    0xE890: 'eye',
    0xE8A7: 'arrow_up_right_square',     # Open externally
    0xE838: 'folder',
    0xE946: 'info_circle',
    0xE72C: 'arrow_clockwise',
    0xE916: 'speedometer',
    0xE97A: 'arrowshape_turn_up_left',   # Reply
    0xE72D: 'arrowshape_turn_up_right',  # Forward
    0xE718: 'pin',
    0xE77A: 'pin_slash',
    0xE840: 'pin_fill',                  # Pinned message banner
    0xE8FA: 'person_badge_plus',
    0xE74F: 'speaker_slash',
    0xE767: 'speaker_2',
    0xE8C3: 'envelope_open',             # Mark as read
    0xEB51: 'heart',
    0xEB52: 'heart_fill',
    0xEA39: 'xmark_circle',
    0xE733: 'nosign',                    # Block
    0xE74D: 'trash',
    0xE762: 'checkmark_circle',          # Select
    0xE8E0: 'hand_thumbsdown',           # Report
    0xE8B9: 'photo_on_rectangle',        # Media, links and docs
    0xE72E: 'lock',
    0xE896: 'square_arrow_up',           # Export
    0xE721: 'search',
    0xEA8F: 'bell',
    0xE7ED: 'bell_slash',
    0xE932: 'square_pencil',             # New chat
    0xE8A3: 'zoom_in',
    0xE71F: 'zoom_out',
    0xE895: 'arrow_2_circlepath',        # Sync contact
    0xE7F6: 'headphones',
    0xE783: 'exclamationmark_circle',    # Not sent
    0xE76E: 'smiley',
    0xE724: 'paperplane_fill',           # Send
    0xE723: 'paperclip',
    0xE712: 'ellipsis',
    0xE707: 'placemark',                 # Map placeholder
    0xE716: 'person_2',                  # Groups
    0xE710: 'plus',
    0xE73E: 'checkmark',
    0xE711: 'xmark',
    0xE894: 'xmark',                     # Clear (text boxes)
    # ── WinUI control templates (menus, combo boxes, check boxes, navigation, dialogs) ──
    0xE70D: 'chevron_down',
    0xE70E: 'chevron_up',
    0xE76B: 'chevron_left',
    0xE76C: 'chevron_right',
    0xE96D: 'chevron_up',                # ChevronUpSmall
    0xE96E: 'chevron_down',              # ChevronDownSmall
    0xE973: 'chevron_left',              # ChevronLeftMed
    0xE974: 'chevron_right',             # ChevronRightMed (submenus)
    0xE0E2: 'chevron_left',
    0xE0E3: 'chevron_right',
    0xE001: 'checkmark',                 # CheckMark (legacy)
    0xE10A: 'xmark',
    0xE8BB: 'xmark',                     # ChromeClose
    0xE700: 'line_horizontal_3',         # Navigation menu button
    0xE72B: 'arrow_left',                # Back
    0xE72A: 'arrow_right',               # Forward (navigation)
    0xE930: 'checkmark_circle',          # Completed
}


class FlattenPen(BasePen):
    """Collects contours as point lists, curves cut into short straight segments."""

    STEPS = 12

    def __init__(self):
        super().__init__(None)
        self.contours, self._current = [], []

    def _moveTo(self, p):
        self._current = [p]

    def _lineTo(self, p):
        self._current.append(p)

    def _curveToOne(self, p1, p2, p3):
        (x0, y0) = self._current[-1]
        for i in range(1, self.STEPS + 1):
            t = i / self.STEPS
            u = 1 - t
            self._current.append((u**3 * x0 + 3 * u * u * t * p1[0] + 3 * u * t * t * p2[0] + t**3 * p3[0],
                                  u**3 * y0 + 3 * u * u * t * p1[1] + 3 * u * t * t * p2[1] + t**3 * p3[1]))

    def _qCurveToOne(self, p1, p2):
        (x0, y0) = self._current[-1]
        for i in range(1, self.STEPS + 1):
            t = i / self.STEPS
            u = 1 - t
            self._current.append((u * u * x0 + 2 * u * t * p1[0] + t * t * p2[0],
                                  u * u * y0 + 2 * u * t * p1[1] + t * t * p2[1]))

    def _closePath(self):
        if len(self._current) >= 3:
            self.contours.append(self._current)
        self._current = []

    _endPath = _closePath


def signed_area(points):
    return sum(x0 * y1 - x1 * y0 for (x0, y0), (x1, y1) in zip(points, points[1:] + points[:1])) / 2


def round_glyph(contours):
    """The filled shape (non-zero winding, sized biggest first) with softened corners."""
    if not contours:
        return None
    rings = sorted(contours, key=lambda c: abs(signed_area(c)), reverse=True)
    fill_sign = signed_area(rings[0]) > 0
    shape = Polygon()
    for ring in rings:
        piece = Polygon(ring).buffer(0)
        shape = shape.union(piece) if (signed_area(ring) > 0) == fill_sign else shape.difference(piece)
    # Icons with details thinner than the radius (hairlines, small dots) would lose them:
    # those get a smaller radius, or none.
    for scale in (1, 0.6, 0.35, 0):
        r_out, r_in = ROUND_OUT * scale, ROUND_IN * scale
        if r_out == 0:
            return shape
        opened = shape.buffer(-r_out, quad_segs=6).buffer(r_out, quad_segs=6)
        rounded = opened.buffer(r_in, quad_segs=6).buffer(-r_in, quad_segs=6).simplify(1.5)
        if shape.symmetric_difference(rounded).area < 0.04 * shape.area and \
                len(getattr(rounded, 'geoms', [rounded])) >= len(getattr(shape, 'geoms', [shape])):
            return rounded
    return shape


def draw_polygons(shape, pen):
    polygons = shape.geoms if isinstance(shape, MultiPolygon) else [shape]
    for polygon in polygons:
        if polygon.is_empty:
            continue
        polygon = orient(polygon, sign=-1.0)   # TrueType: outer clockwise, holes counter-clockwise
        for ring in [polygon.exterior, *polygon.interiors]:
            points = [(round(x), round(y)) for x, y in ring.coords[:-1]]
            points = [p for i, p in enumerate(points) if p != points[i - 1]]
            if len(points) < 3:
                continue
            pen.moveTo(points[0])
            for p in points[1:]:
                pen.lineTo(p)
            pen.closePath()


def assign(names):
    """Name -> code point: kept from codepoints.json; new names get the next free ones."""
    saved = {}
    if os.path.exists(CODEPOINTS):
        with open(CODEPOINTS, encoding='utf-8') as f:
            saved = {n: int(c, 16) for n, c in json.load(f).items()}
    taken = set(saved.values()) | set(REMAP)
    for path in SEGOE:
        taken |= set(TTFont(path).getBestCmap())
    free = (c for c in range(0xE000, 0xF900) if c not in taken)
    for n in names:
        if n not in saved:
            saved[n] = next(free)
    with open(CODEPOINTS, 'w', encoding='utf-8', newline='\n') as f:
        json.dump({n: f'{saved[n]:04X}' for n in sorted(saved)}, f, indent=1)
        f.write('\n')
    return saved


def main():
    font = TTFont(SOURCE)
    cmap = font.getBestCmap()
    chars = {glyph: chr(code) for code, glyph in cmap.items()}

    # Icon name -> glyph, from the ligature table.
    icons = {}
    for lookup in font['GSUB'].table.LookupList.Lookup:
        for sub in lookup.SubTable:
            for first, ligatures in getattr(sub, 'ligatures', {}).items():
                for lig in ligatures:
                    name = chars.get(first, '') + ''.join(chars.get(c, '') for c in lig.Component)
                    icons[name] = lig.LigGlyph
    names = sorted(icons)
    missing = sorted({n for n in REMAP.values() if n not in icons})
    assert not missing, f'not in Framework7 Icons: {missing}'

    # 512-unit glyphs centred on y=192 onto Segoe's 2048 em (centre 1024). 4.4x rather than
    # 4x: SF-style shapes carry more padding; this matches Segoe's ink size (median of the
    # REMAP pairs: 4.3).
    scale = 4.4
    shift = 1024 - 192 * scale
    glyph_set = font.getGlyphSet()
    glyf = font['glyf']
    keep = ['.notdef'] + [icons[n] for n in names]
    new_glyphs = {}
    for g in keep:
        flat = FlattenPen()
        glyph_set[g].draw(TransformPen(flat, (scale, 0, 0, scale, (2048 - 512 * scale) / 2, shift)))
        pen = TTGlyphPen(None)
        shape = round_glyph(flat.contours)
        if shape is not None and not shape.is_empty:
            draw_polygons(shape, pen)
        new_glyphs[g] = pen.glyph()
    font.setGlyphOrder(keep)
    glyf.glyphs = new_glyphs
    glyf.glyphOrder = keep
    hmtx = font['hmtx']
    hmtx.metrics = {}
    for g in keep:
        new_glyphs[g].recalcBounds(glyf)
        hmtx.metrics[g] = (2048, getattr(new_glyphs[g], 'xMin', 0))
    for table in ('GSUB', 'GPOS', 'GDEF', 'post'):
        if table in font and table != 'post':
            del font[table]
    font['post'].formatType = 3.0
    font['post'].extraNames = []
    font['post'].mapping = {}
    font['maxp'].numGlyphs = len(keep)

    font['head'].unitsPerEm = 2048
    hhea, os2 = font['hhea'], font['OS/2']
    hhea.ascent, hhea.descent, hhea.lineGap = 2048, 0, 0
    os2.sTypoAscender, os2.sTypoDescender, os2.sTypoLineGap = 2048, 0, 0
    os2.usWinAscent, os2.usWinDescent = 2048, 0
    os2.fsSelection |= 1 << 7   # USE_TYPO_METRICS

    assigned = assign(names)
    mapping = {assigned[n]: icons[n] for n in names}
    mapping.update({code: icons[n] for code, n in REMAP.items()})
    bmp = {c: g for c, g in mapping.items() if c <= 0xFFFF}
    cmap_table = newTable('cmap')
    cmap_table.tableVersion = 0
    subtables = []
    for platform, encoding in ((3, 1), (0, 3)):
        sub = cmap_format_4(4)
        sub.platformID, sub.platEncID, sub.language, sub.cmap = platform, encoding, 0, dict(bmp)
        subtables.append(sub)
    for platform, encoding in ((3, 10), (0, 4)):
        sub = cmap_format_12(12)
        sub.platformID, sub.platEncID, sub.language, sub.cmap = platform, encoding, 0, dict(mapping)
        sub.format, sub.reserved, sub.length, sub.nGroups = 12, 0, 0, 0
        subtables.append(sub)
    cmap_table.tables = subtables
    font['cmap'] = cmap_table

    name = font['name']
    for record in list(name.names):
        if record.nameID in (1, 3, 4, 6, 16, 17):
            name.removeNames(nameID=record.nameID)
    name.setName(FAMILY, 1, 3, 1, 0x409)
    name.setName('Regular', 2, 3, 1, 0x409)
    name.setName(FAMILY + ' Regular', 3, 3, 1, 0x409)
    name.setName(FAMILY, 4, 3, 1, 0x409)
    name.setName('WAFluentSymbols-Regular', 6, 3, 1, 0x409)
    name.setName('Framework7 Icons by Vladimir Kharlampidi (MIT), remapped for WAFluent.', 5, 3, 1, 0x409)

    os.makedirs(os.path.dirname(FONT), exist_ok=True)
    font.save(FONT)

    def pascal(n):
        s = ''.join(p[:1].upper() + p[1:] for p in re.split(r'_', n))
        return s if s[0].isalpha() else 'N' + s   # "1_circle" -> N1Circle

    lines = [
        '// Generated by tools/symbols/build.py; do not edit.',
        'namespace WhatsAppNative.Helpers;',
        '',
        '/// <summary>',
        '/// SF Symbols-style icons (Framework7 Icons, MIT) in Assets/Fonts/WAFluentSymbols.ttf,',
        '/// the app\'s symbol font: <c>new FontIcon { Glyph = Sf.ChatBubble2Fill }</c>. Names follow',
        '/// Framework7\'s, which follow SF Symbols (chat_bubble_2_fill ~ bubble.left.and.bubble.right.fill).',
        '/// </summary>',
        'public static class Sf',
        '{',
    ]
    for n in names:
        lines.append(f'    public const string {pascal(n)} = "\\u{assigned[n]:04X}";')
    lines.append('}')
    with open(CLASS, 'w', encoding='utf-8', newline='\n') as f:
        f.write('\n'.join(lines) + '\n')
    print(f'{len(names)} icons, {len(REMAP)} remapped -> {FONT}')


if __name__ == '__main__':
    main()
