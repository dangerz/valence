"""Builds the viewer's UI sprite sheets from the original art in images/ui-source/.

    sips -s format png images/ui-source/icons.webp --out /tmp/icons.png
    sips -s format png images/ui-source/panels.webp --out /tmp/panels.png
    python3 tools/build_ui_sheets.py /tmp/icons.png /tmp/panels.png images/logo.png valence/images/ui

icons.png: the source icons (cleaned of compression speckle) on an even 128px grid, 8 per row, in
the order listed in ICON_NAMES (valence/Scripts/UiAssets.cs uses the same list). The source's old
logo is replaced by the Valence mark, and icons the source didn't have are drawn in its style: a
light cyan stroke with a thin darker outline.

panels.png: the nine-patch panels the UI uses (subtle, accent, active, focus frame), 2 per row.
The source's duplicate cyan, purple and white-preview panels are left out.

Pure Python (zlib only).
"""
import math
import struct
import sys
import zlib

CELL = 128
WINDOW = 160                       # source pixels around each icon; scaled to CELL
STROKE = 13 * CELL / WINDOW        # the source's stroke width, at CELL scale
OUTLINE = 1.6 * CELL / WINDOW      # its darker outline
FILL = (164, 235, 253)
EDGE = (80, 144, 215)

ICON_NAMES = [
    "logo", "menu", "search", "star", "file", "eye", "eye-off", "folder",
    "globe", "chevron-right", "chevron-down", "branch", "layers", "filter", "focus", "grid",
    "play", "more", "database", "cube", "link", "cycle", "users", "graph",
    "pointer", "orbit", "move", "zoom-in", "info", "mouse", "close", "copy",
    "eye-quiet", "pause", "stop", "more-vertical", "plus-square", "minus-square", "clock", "dot",
    "camera", "bolt", "arrow-down", "arrow-up", "trash", "pencil",
]

# Source panels kept, by (row, column) in the source sheet, in output order.
PANELS = [("panel", 0, 1), ("accent", 0, 2), ("active", 0, 0), ("focus", 1, 2)]


# ---------------------------------------------------------------- PNG

def read_png(path):
    data = open(path, "rb").read()
    pos, idat = 8, b""
    while pos < len(data):
        n, = struct.unpack(">I", data[pos:pos + 4])
        kind, chunk = data[pos + 4:pos + 8], data[pos + 8:pos + 8 + n]
        if kind == b"IHDR":
            w, h, depth, ctype = struct.unpack(">IIBB", chunk[:10])
        if kind == b"IDAT":
            idat += chunk
        pos += 12 + n
    assert depth == 8 and ctype in (2, 6), (depth, ctype)
    bpp = 3 if ctype == 2 else 4
    raw, stride = zlib.decompress(idat), w * bpp
    out, prev, off = bytearray(w * h * 4), bytearray(stride), 0
    for y in range(h):
        f, line = raw[off], bytearray(raw[off + 1:off + 1 + stride])
        off += 1 + stride
        if f == 1:
            for i in range(bpp, stride):
                line[i] = (line[i] + line[i - bpp]) & 255
        elif f == 2:
            for i in range(stride):
                line[i] = (line[i] + prev[i]) & 255
        elif f == 3:
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 255
        elif f == 4:
            for i in range(stride):
                a = line[i - bpp] if i >= bpp else 0
                b, c = prev[i], prev[i - bpp] if i >= bpp else 0
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        prev = line
        for x in range(w):
            o, i = (y * w + x) * 4, x * bpp
            out[o:o + 3] = line[i:i + 3]
            out[o + 3] = line[i + 3] if bpp == 4 else 255
    return w, h, out


def write_png(path, w, h, rgba):
    raw = b"".join(b"\x00" + bytes(rgba[y * w * 4:(y + 1) * w * 4]) for y in range(h))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    open(path, "wb").write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0))
                           + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


# ---------------------------------------------------------------- helpers

def spans(values):
    out, start = [], None
    for i, v in enumerate(values):
        if v and start is None:
            start = i
        if not v and start is not None:
            out.append((start, i - 1))
            start = None
    if start is not None:
        out.append((start, len(values) - 1))
    return out


def crop(w, h, px, x0, y0, cw, ch, min_alpha):
    """A cw×ch RGBA block from (x0, y0); outside the image and faint speckle become transparent."""
    out = bytearray(cw * ch * 4)
    for y in range(ch):
        sy = y0 + y
        if not 0 <= sy < h:
            continue
        for x in range(cw):
            sx = x0 + x
            if not 0 <= sx < w:
                continue
            s, d = (sy * w + sx) * 4, (y * cw + x) * 4
            if px[s + 3] >= min_alpha:
                out[d:d + 4] = px[s:s + 4]
    return out


def resample(src, sw, sh, dw, dh):
    """Area-average (premultiplied) resize."""
    def weights(src_len, dst_len):
        scale = src_len / dst_len
        table = []
        for d in range(dst_len):
            a, b = d * scale, (d + 1) * scale
            row, i = [], int(a)
            while i < b and i < src_len:
                overlap = min(b, i + 1) - max(a, i)
                if overlap > 0:
                    row.append((i, overlap / scale))
                i += 1
            table.append(row)
        return table

    wx, wy = weights(sw, dw), weights(sh, dh)
    out = bytearray(dw * dh * 4)
    for dy in range(dh):
        for dx in range(dw):
            r = g = b = a = 0.0
            for sy, fy in wy[dy]:
                for sx, fx in wx[dx]:
                    o = (sy * sw + sx) * 4
                    alpha = src[o + 3] * fx * fy
                    r += src[o] * alpha
                    g += src[o + 1] * alpha
                    b += src[o + 2] * alpha
                    a += alpha
            o = (dy * dw + dx) * 4
            if a > 0:
                out[o:o + 4] = bytes((min(255, round(r / a)), min(255, round(g / a)), min(255, round(b / a)), min(255, round(a))))
    return out


def paste(sheet, sheet_w, block, bw, bh, x0, y0):
    for y in range(bh):
        s, d = y * bw * 4, ((y0 + y) * sheet_w + x0) * 4
        sheet[d:d + bw * 4] = block[s:s + bw * 4]


# ---------------------------------------------------------------- drawn icons (same style)

def seg(a, b):
    def sd(px, py):
        ax, ay = a
        bx, by = b
        dx, dy = bx - ax, by - ay
        t = max(0.0, min(1.0, ((px - ax) * dx + (py - ay) * dy) / (dx * dx + dy * dy)))
        return math.hypot(px - ax - t * dx, py - ay - t * dy) - STROKE / 2
    return sd


def ring(c, r):
    return lambda px, py: abs(math.hypot(px - c[0], py - c[1]) - r) - STROKE / 2


def rbox(c, half, radius):
    def sd(px, py):
        qx = abs(px - c[0]) - half[0] + radius
        qy = abs(py - c[1]) - half[1] + radius
        outside = math.hypot(max(qx, 0.0), max(qy, 0.0)) + min(max(qx, qy), 0.0) - radius
        return abs(outside) - STROKE / 2
    return sd


def poly(points, closed=False):
    segments = [seg(points[i], points[i + 1]) for i in range(len(points) - 1)]
    if closed:
        segments.append(seg(points[-1], points[0]))
    return lambda px, py: min(s(px, py) for s in segments)


def render(prims, fill=FILL, edge=EDGE, outline=OUTLINE):
    out = bytearray(CELL * CELL * 4)
    for y in range(CELL):
        for x in range(CELL):
            d = min(p(x + 0.5, y + 0.5) for p in prims)
            cover = max(0.0, min(1.0, 0.5 - d))
            if cover <= 0:
                continue
            t = max(0.0, min(1.0, d + outline + 0.5)) if outline > 0 else 0.0
            color = [round(fill[i] + (edge[i] - fill[i]) * t) for i in range(3)]
            o = (y * CELL + x) * 4
            out[o:o + 4] = bytes(color + [round(255 * cover * 0.98)])
    return out


DRAWN = {
    "pause": [seg((50, 36), (50, 92)), seg((78, 36), (78, 92))],
    "stop": [rbox((64, 64), (30, 30), 10)],
    "plus-square": [rbox((64, 64), (42, 42), 14), seg((64, 44), (64, 84)), seg((44, 64), (84, 64))],
    "minus-square": [rbox((64, 64), (42, 42), 14), seg((44, 64), (84, 64))],
    "clock": [ring((64, 64), 44), seg((64, 64), (64, 38)), seg((64, 64), (84, 74))],
    "camera": [rbox((64, 72), (48, 30), 12), ring((64, 72), 16), poly([(42, 42), (50, 30), (78, 30), (86, 42)])],
    "bolt": [poly([(74, 16), (40, 70), (64, 70), (54, 112), (90, 54), (66, 54)], closed=True)],
    "arrow-down": [seg((64, 22), (64, 104)), poly([(38, 78), (64, 104), (90, 78)])],
    "arrow-up": [seg((64, 24), (64, 106)), poly([(38, 50), (64, 24), (90, 50)])],
    "trash": [seg((30, 38), (98, 38)), poly([(52, 38), (52, 24), (76, 24), (76, 38)]),
              poly([(38, 38), (44, 104), (84, 104), (90, 38)]), seg((56, 54), (56, 88)), seg((72, 54), (72, 88))],
    "pencil": [poly([(28, 100), (32, 80), (82, 30), (98, 46), (48, 96)], closed=True), seg((72, 40), (88, 56))],
}


def dot():
    """A plain white disc: tinted in code (project colors), so no outline."""
    disc = lambda px, py: math.hypot(px - 64, py - 64) - 24
    return render([disc], fill=(255, 255, 255), edge=(255, 255, 255), outline=0)


# ---------------------------------------------------------------- the Valence mark

def mark_icon(logo_path):
    """The logo's mark (not the wordmark) in the icon style: light fill, thin darker outline."""
    w, h, px = read_png(logo_path)
    lum = lambda o: 0.2126 * px[o] + 0.7152 * px[o + 1] + 0.0722 * px[o + 2]
    inked = [any(lum((y * w + x) * 4) < 128 for x in range(0, w, 2)) for y in range(h)]
    first = inked.index(True)
    cut = next(y for y in range(first, h) if not inked[y])          # the wordmark starts below this gap
    x0 = min(x for y in range(first, cut) for x in range(w) if lum((y * w + x) * 4) < 128)
    x1 = max(x for y in range(first, cut) for x in range(w) if lum((y * w + x) * 4) < 128)
    mw, mh = x1 - x0 + 1, cut - first
    mask = [[1 if lum(((first + y) * w + x0 + x) * 4) < 128 else 0 for x in range(mw)] for y in range(mh)]

    scale = 100 / max(mw, mh)
    radius = max(1, round(OUTLINE / scale))
    # Square erosion, separable: the fill sits inside the outline.
    rows = [[min(row[max(0, x - radius):x + radius + 1]) for x in range(mw)] for row in mask]
    eroded = [[min(rows[yy][x] for yy in range(max(0, y - radius), min(mh, y + radius + 1))) for x in range(mw)]
              for y in range(mh)]

    dw, dh = round(mw * scale), round(mh * scale)
    ox, oy = (CELL - dw) // 2, (CELL - dh) // 2
    out = bytearray(CELL * CELL * 4)
    for dy in range(dh):
        sy0, sy1 = int(dy / scale), max(int(dy / scale) + 1, int((dy + 1) / scale))
        for dx in range(dw):
            sx0, sx1 = int(dx / scale), max(int(dx / scale) + 1, int((dx + 1) / scale))
            n = (sy1 - sy0) * (sx1 - sx0)
            cover = sum(mask[y][x] for y in range(sy0, min(sy1, mh)) for x in range(sx0, min(sx1, mw))) / n
            inner = sum(eroded[y][x] for y in range(sy0, min(sy1, mh)) for x in range(sx0, min(sx1, mw))) / n
            if cover <= 0:
                continue
            t = 1 - inner / cover
            color = [round(FILL[i] + (EDGE[i] - FILL[i]) * t) for i in range(3)]
            o = ((oy + dy) * CELL + ox + dx) * 4
            out[o:o + 4] = bytes(color + [round(255 * cover * 0.98)])
    return out


# ---------------------------------------------------------------- sheets

def build_icons(icons_path, logo_path, out_path):
    w, h, px = read_png(icons_path)
    alpha = lambda x, y: px[(y * w + x) * 4 + 3]
    rows = spans([any(alpha(x, y) > 60 for x in range(0, w, 2)) for y in range(h)])
    cols = spans([any(alpha(x, y) > 60 for y in range(0, h, 2)) for x in range(w)])
    assert len(rows) == 4 and len(cols) == 8, (rows, cols)

    cells = {}
    names = iter(ICON_NAMES)
    for (y0, y1) in rows:
        for (x0, x1) in cols:
            cx, cy = (x0 + x1) // 2, (y0 + y1) // 2
            block = crop(w, h, px, cx - WINDOW // 2, cy - WINDOW // 2, WINDOW, WINDOW, min_alpha=16)
            cells[next(names)] = resample(block, WINDOW, WINDOW, CELL, CELL)

    cells["logo"] = mark_icon(logo_path)
    eye = cells["eye"]
    cells["eye-quiet"] = bytearray(eye[i] if i % 4 != 3 else eye[i] // 2 for i in range(len(eye)))
    more = cells["more"]
    vertical = bytearray(len(more))
    for y in range(CELL):
        for x in range(CELL):
            s, d = (y * CELL + x) * 4, (x * CELL + y) * 4
            vertical[d:d + 4] = more[s:s + 4]
    cells["more-vertical"] = vertical
    cells["dot"] = dot()
    for name, prims in DRAWN.items():
        cells[name] = render(prims)

    per_row = 8
    sheet_rows = (len(ICON_NAMES) + per_row - 1) // per_row
    sw, sh = per_row * CELL, sheet_rows * CELL
    sheet = bytearray(sw * sh * 4)
    for i, name in enumerate(ICON_NAMES):
        paste(sheet, sw, cells[name], CELL, CELL, (i % per_row) * CELL, (i // per_row) * CELL)
    write_png(out_path, sw, sh, sheet)
    print(f"{out_path}: {len(ICON_NAMES)} icons, {sw}x{sh}")


def build_panels(panels_path, out_path):
    w, h, px = read_png(panels_path)
    alpha = lambda x, y: px[(y * w + x) * 4 + 3]
    rows = spans([any(alpha(x, y) > 40 for x in range(0, w, 3)) for y in range(h)])
    cols = spans([any(alpha(x, y) > 40 for y in range(0, h, 3)) for x in range(w)])
    assert len(rows) == 2 and len(cols) == 3, (rows, cols)

    pad = 3
    blocks = []
    for name, r, c in PANELS:
        (y0, y1), (x0, x1) = rows[r], cols[c]
        bw, bh = x1 - x0 + 1 + 2 * pad, y1 - y0 + 1 + 2 * pad
        blocks.append((name, bw, bh, crop(w, h, px, x0 - pad, y0 - pad, bw, bh, min_alpha=10)))

    cell_w = max(b[1] for b in blocks) + 8
    cell_h = max(b[2] for b in blocks) + 8
    sw, sh = 2 * cell_w, 2 * cell_h
    sheet = bytearray(sw * sh * 4)
    for i, (name, bw, bh, block) in enumerate(blocks):
        x, y = (i % 2) * cell_w + 4, (i // 2) * cell_h + 4
        paste(sheet, sw, block, bw, bh, x, y)
        print(f'  {name}: new Rect2({x}, {y}, {bw}, {bh})')
    write_png(out_path, sw, sh, sheet)
    print(f"{out_path}: {len(blocks)} panels, {sw}x{sh}")


if __name__ == "__main__":
    icons_png, panels_png, logo_png, out_dir = sys.argv[1:5]
    build_icons(icons_png, logo_png, f"{out_dir}/icons.png")
    build_panels(panels_png, f"{out_dir}/panels.png")
