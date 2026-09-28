"""Builds src/Assets/emoji.json for the emoji keyboard and :shortcode: autocomplete.

Inputs (not committed; emojibase-data 16, MIT, https://github.com/milesj/emojibase):
    en/compact.json            -> names, tags, categories, order, skin tones
    en/shortcodes/github.json  -> GitHub/Slack-style shortcodes (:sob:, :joy:, :+1:)
and the emoji font, so only emoji it can draw are listed.

    python tools/make-emoji-data.py compact.json github.json src/Assets/Fonts/AppleColorEmoji.ttf

Output: a JSON array, one entry per emoji in keyboard order:
    [emoji, tab, name, [shortcodes], "search words", [skin tone variants]]
tab: 0 smileys & people, 1 animals & nature, 2 food & drink, 3 activity,
     4 travel & places, 5 objects, 6 symbols, 7 flags
"""
import json
import re
import sys
from pathlib import Path

from fontTools.ttLib import TTFont

compact_path, github_path, font_path = sys.argv[1:4]
out_path = Path(__file__).resolve().parent.parent / 'src' / 'Assets' / 'emoji.json'

# emojibase groups -> keyboard tabs (2 = skin/hair components, not shown)
TAB = {0: 0, 1: 0, 3: 1, 4: 2, 6: 3, 5: 4, 7: 5, 8: 6, 9: 7}
VS16 = 0xFE0F


def font_support(path):
    """Code point sequences the font has a glyph for (singles + GSUB ligatures), VS16 ignored."""
    font = TTFont(path, lazy=True)
    cmap = font.getBestCmap()
    glyph_to_cp = {g: cp for cp, g in cmap.items()}
    seqs = {(cp,) for cp in cmap}
    for lookup in font['GSUB'].table.LookupList.Lookup:
        for sub in lookup.SubTable:
            sub = getattr(sub, 'ExtSubTable', sub)
            for first, ligs in getattr(sub, 'ligatures', {}).items():
                for lig in ligs:
                    names = [first, *lig.Component]
                    if all(n in glyph_to_cp for n in names):
                        seqs.add(tuple(glyph_to_cp[n] for n in names if glyph_to_cp[n] != VS16))
    return seqs


def drawable(emoji, support):
    return tuple(ord(c) for c in emoji if ord(c) != VS16) in support


support = font_support(font_path)
data = json.load(open(compact_path, encoding='utf-8'))
github = json.load(open(github_path, encoding='utf-8'))

entries = []
for e in data:
    group = e.get('group')
    if group not in TAB or not drawable(e['unicode'], support):
        continue
    codes = github.get(e['hexcode'], [])
    codes = [codes] if isinstance(codes, str) else list(codes)
    slug = re.sub(r'[^a-z0-9]+', '_', e['label'].lower()).strip('_')
    if slug and slug not in codes:
        codes.append(slug)                      # :loudly_crying_face: works too
    words = ' '.join(dict.fromkeys([e['label'].lower(), *e.get('tags', [])]))
    # Five uniform tones (couples etc. also come in mixed pairs; the keyboard shows one tone each).
    skins = [s['unicode'] for s in e.get('skins', [])
             if len({c for c in s['unicode'] if 0x1F3FB <= ord(c) <= 0x1F3FF}) == 1 and drawable(s['unicode'], support)]
    entries.append((e.get('order', 0), [e['unicode'], TAB[group], e['label'], codes, words, skins[:5]]))

entries.sort(key=lambda x: (x[1][1], x[0]))
out = [entry for _, entry in entries]
out_path.write_text(json.dumps(out, ensure_ascii=False, separators=(',', ':')), encoding='utf-8')
print(f'{len(out)} emoji -> {out_path} ({out_path.stat().st_size // 1024} KB)')
