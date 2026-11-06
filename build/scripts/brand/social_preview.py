"""Builds the repository social preview: the product ring, drawn with the letters' stroke width, is the
"o" of "Resource". The output is plain SVG paths, so it needs no font to display.

Requires fonttools, uharfbuzz, skia-pathops and the Outfit variable font
(SIL Open Font License 1.1, https://github.com/google/fonts/tree/main/ofl/outfit).

    python social_preview.py --font "Outfit[wght].ttf" --out social-preview.svg
"""
import argparse
import io
import math

import pathops
import uharfbuzz as hb
from fontTools.pens.svgPathPen import SVGPathPen
from fontTools.pens.transformPen import TransformPen
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

WIDTH, HEIGHT = 1280, 640
BACKGROUND = "#f8fafc"
# Ring segments of src/Shared/Assets/ProductIcon/icon.svg: colour, start and end angle in degrees (y down).
SEGMENTS = {
    "green": ("#16a34a", 222, 264),
    "blue": ("#0284c7", -84, 36),
    "purple": ("#8b5cf6", 48, 134),
    "orange": ("#ea580c", 146, 210),
}
ORANGE_OFFSET = (-13.991, 0.489)  # the icon sets the orange segment out, in its 256-unit space
ICON_RADIUS, ICON_STROKE = 82.0, 44.0

SIZE = 206            # font size of both lines
WEIGHT = 800
LEADING = 0.98        # baseline distance, in font sizes
LEFT_PAD = 0.035      # extra space between "s" and the set-out orange segment, in font sizes
OVERSHOOT = 0.012     # the ring dips below the baseline like an o, in font sizes
OPTICAL_CENTRE = 308  # vertical centre of the whole mark
COLOURS = {"left": "#ea580c", "right": "#0284c7", "second": "#8b5cf6"}


def polygon(points):
    path = pathops.Path()
    path.moveTo(*points[0])
    for point in points[1:]:
        path.lineTo(*point)
    path.close()
    return path


def sector(cx, cy, radius, stroke, a0, a1, steps=160):
    outer, inner = radius + stroke / 2, radius - stroke / 2
    angles = [math.radians(a0 + (a1 - a0) * i / steps) for i in range(steps + 1)]
    return polygon([(cx + outer * math.cos(a), cy + outer * math.sin(a)) for a in angles]
                   + [(cx + inner * math.cos(a), cy + inner * math.sin(a)) for a in reversed(angles)])


def union(paths):
    result = pathops.Path()
    for path in paths:
        result = pathops.op(result, path, pathops.PathOp.UNION)
    return result


def moved(path, dx, dy):
    result = pathops.Path()
    path.draw(TransformPen(result.getPen(), (1, 0, 0, 1, dx, dy)))
    return result


def svg_path(path, colour):
    pen = SVGPathPen(None, ntos=lambda v: "%.2f" % v)
    path.draw(pen)
    return f'<path d="{pen.getCommands()}" fill="{colour}"/>'


class Face:
    def __init__(self, font_path, weight):
        instance = instantiateVariableFont(TTFont(font_path), {"wght": weight})
        data = io.BytesIO()
        instance.save(data)
        self.font = TTFont(io.BytesIO(data.getvalue()))
        self.shaper = hb.Font(hb.Face(data.getvalue()))
        self.glyphs = self.font.getGlyphSet()
        self.order = self.font.getGlyphOrder()
        self.upem = self.font["head"].unitsPerEm

    def line(self, text, size, x, baseline):
        """One path per glyph, in canvas coordinates, and the advance width."""
        buffer = hb.Buffer()
        buffer.add_str(text)
        buffer.guess_segment_properties()
        hb.shape(self.shaper, buffer, {"kern": True, "liga": True})
        scale = size / self.upem
        glyphs, pen_x = [], x
        for info, position in zip(buffer.glyph_infos, buffer.glyph_positions):
            path = pathops.Path()
            transform = (scale, 0, 0, -scale, pen_x + position.x_offset * scale, baseline - position.y_offset * scale)
            self.glyphs[self.order[info.codepoint]].draw(TransformPen(path.getPen(), transform))
            glyphs.append(path)
            pen_x += position.x_advance * scale
        return glyphs, pen_x - x


def build(font_path):
    face = Face(font_path, WEIGHT)
    cx, baseline1 = WIDTH / 2, 300.0
    baseline2 = baseline1 + LEADING * SIZE

    # The letters' stroke width, read from the stem of R, sets the ring's stroke; the radius keeps the icon's ratio.
    r_glyph = face.line("R", SIZE, 0, baseline1)[0][0]
    band = polygon([(-50, baseline1 - 31), (900, baseline1 - 31), (900, baseline1 - 29), (-50, baseline1 - 29)])
    stem = sorted(pathops.op(r_glyph, band, pathops.PathOp.INTERSECTION).contours, key=lambda c: c.bounds[0])[0].bounds
    stroke = stem[2] - stem[0]
    radius = stroke * ICON_RADIUS / ICON_STROKE
    outer = radius + stroke / 2
    unit = radius / ICON_RADIUS
    set_out = (ORANGE_OFFSET[0] * unit, ORANGE_OFFSET[1] * unit)

    # First line: drop the o, open the word around the ring, and put the ring on the centre line.
    glyphs, _ = face.line("Resource", SIZE, 0, baseline1)
    o_bounds = glyphs[3].bounds
    o_centre, o_width = (o_bounds[0] + o_bounds[2]) / 2, o_bounds[2] - o_bounds[0]
    open_right = outer - o_width / 2
    open_left = outer - o_width / 2 - set_out[0] + LEFT_PAD * SIZE
    left = [moved(g, cx - o_centre - open_left, 0) for g in glyphs[:3]]
    right = [moved(g, cx - o_centre + open_right, 0) for g in glyphs[4:]]
    ring_cy = baseline1 + OVERSHOOT * SIZE - outer

    second, second_width = face.line("Manager", SIZE, 0, baseline2)
    second = [moved(g, cx - second_width / 2, 0) for g in second]

    top = ring_cy - outer
    bottom = max(g.bounds[3] for g in second)
    dy = OPTICAL_CENTRE - (top + bottom) / 2

    parts = [f'<svg xmlns="http://www.w3.org/2000/svg" width="{WIDTH}" height="{HEIGHT}" viewBox="0 0 {WIDTH} {HEIGHT}">',
             f'<rect width="{WIDTH}" height="{HEIGHT}" fill="{BACKGROUND}"/>',
             f'<g transform="translate(0 {dy:.2f})">']
    for name, (colour, a0, a1) in SEGMENTS.items():
        sx, sy = set_out if name == "orange" else (0, 0)
        parts.append(svg_path(sector(cx + sx, ring_cy + sy, radius, stroke, a0, a1), colour))
    parts.append(svg_path(union(left), COLOURS["left"]))
    parts.append(svg_path(union(right), COLOURS["right"]))
    parts.append(svg_path(union(second), COLOURS["second"]))
    parts += ["</g>", "</svg>"]
    return "\n".join(parts) + "\n"


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Build the repository social preview SVG.")
    parser.add_argument("--font", required=True, help="Outfit variable font file")
    parser.add_argument("--out", required=True, help="SVG file to write")
    options = parser.parse_args()
    with open(options.out, "w", encoding="utf-8", newline="\n") as handle:
        handle.write(build(options.font))
