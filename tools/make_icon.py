"""Turn the flat logo into a macOS-style app icon: squircle body, transparent outside, soft shadow.
Pure Python (zlib only). Usage: make_icon.py <in.png> <out.png> [size]"""
import struct, sys, zlib, math

def read_png(path):
    data = open(path, 'rb').read()
    pos, idat = 8, b''
    while pos < len(data):
        n, = struct.unpack('>I', data[pos:pos+4]); kind = data[pos+4:pos+8]; chunk = data[pos+8:pos+8+n]
        if kind == b'IHDR': w, h, depth, ctype = struct.unpack('>IIBB', chunk[:10])
        if kind == b'IDAT': idat += chunk
        pos += 12 + n
    assert depth == 8 and ctype in (2, 6), (depth, ctype)
    bpp = 3 if ctype == 2 else 4
    raw = zlib.decompress(idat); stride = w * bpp
    rows, prev, off = [], bytearray(stride), 0
    for _ in range(h):
        f = raw[off]; line = bytearray(raw[off+1:off+1+stride]); off += 1 + stride
        if f == 1:
            for i in range(bpp, stride): line[i] = (line[i] + line[i-bpp]) & 255
        elif f == 2:
            line = bytearray((a + b) & 255 for a, b in zip(line, prev))
        elif f == 3:
            for i in range(stride):
                a = line[i-bpp] if i >= bpp else 0
                line[i] = (line[i] + ((a + prev[i]) >> 1)) & 255
        elif f == 4:
            for i in range(stride):
                a = line[i-bpp] if i >= bpp else 0; b = prev[i]; c = prev[i-bpp] if i >= bpp else 0
                p = a + b - c; pa, pb, pc = abs(p-a), abs(p-b), abs(p-c)
                line[i] = (line[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
        rows.append(line); prev = line
    return w, h, bpp, rows

def write_png(path, w, h, rgba):
    raw = b''.join(b'\x00' + bytes(rgba[y*w*4:(y+1)*w*4]) for y in range(h))
    def chunk(k, d): return struct.pack('>I', len(d)) + k + d + struct.pack('>I', zlib.crc32(k + d) & 0xffffffff)
    open(path, 'wb').write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
                           + chunk(b'IDAT', zlib.compress(raw, 9)) + chunk(b'IEND', b''))

src, out = sys.argv[1], sys.argv[2]
SIZE = int(sys.argv[3]) if len(sys.argv) > 3 else 1024
w, h, bpp, rows = read_png(src)
lum = lambda r, g, b: 0.2126*r + 0.7152*g + 0.0722*b

BG = (253, 252, 249)                       # the logo's own off-white
# Ink colour and content bounds from the art itself.
ink_samples, x0, y0, x1, y1 = [], w, h, 0, 0
for y in range(0, h, 2):
    row = rows[y]
    for x in range(0, w, 2):
        r, g, b = row[x*bpp], row[x*bpp+1], row[x*bpp+2]
        if lum(r, g, b) < 128:
            x0, y0, x1, y1 = min(x0, x), min(y0, y), max(x1, x), max(y1, y)
            if lum(r, g, b) < 60: ink_samples.append((r, g, b))
INK = tuple(sorted(c[i] for c in ink_samples)[len(ink_samples)//2] for i in range(3))
L_BG, L_INK = lum(*BG), lum(*INK)
print('content', (x0, y0, x1, y1), 'ink', INK)

# Apple macOS grid (scaled from 1024): body 824 at offset 100; superellipse ~ continuous corners.
s = SIZE / 1024
body, margin, N = 824*s, 100*s, 5.0
half, cxb, cyb = body/2, SIZE/2, SIZE/2
fill = 0.66                                # content's larger side as a share of the body
scale = body*fill / max(x1-x0, y1-y0)      # output px per source px
ccx, ccy = (x0+x1)/2, (y0+y1)/2

def radius(px, py):                        # superellipse "radius": 1 on the edge
    dx, dy = abs(px-cxb)/half, abs(py-cyb)/half
    return (dx**N + dy**N) ** (1/N)

def sample(px, py):                        # bilinear sample of the art, cleaned to flat bg/ink
    sx, sy = ccx + (px-cxb)/scale, ccy + (py-cyb)/scale
    if sx < 0 or sy < 0 or sx >= w-1 or sy >= h-1: return BG
    ix, iy = int(sx), int(sy); fx, fy = sx-ix, sy-iy
    r0, r1 = rows[iy], rows[iy+1]
    def px_l(row, x): return lum(row[x*bpp], row[x*bpp+1], row[x*bpp+2])
    l = (px_l(r0, ix)*(1-fx) + px_l(r0, ix+1)*fx)*(1-fy) + (px_l(r1, ix)*(1-fx) + px_l(r1, ix+1)*fx)*fy
    t = min(1.0, max(0.0, (L_BG - l) / (L_BG - L_INK)))
    t = min(1.0, max(0.0, (t - 0.06) / 0.88)); t = t*t*(3-2*t)   # drop paper noise, keep edges crisp
    return tuple(round(BG[i] + (INK[i]-BG[i])*t) for i in range(3))

rgba = bytearray(SIZE*SIZE*4)
SS = [(i+0.5)/4 for i in range(4)]
for y in range(SIZE):
    for x in range(SIZE):
        r = radius(x+0.5, y+0.5)
        # Coverage of the squircle: supersample only near the edge.
        if r < 0.995: cov = 1.0
        elif r > 1.005: cov = 0.0
        else: cov = sum(radius(x+a, y+b) <= 1 for a in SS for b in SS) / 16
        # Apple-style shadow: soft, a little below the body.
        d = (radius(x+0.5, y+0.5 - 12*s) - 1) * half
        shadow = 0.32 * max(0.0, 1 - max(0.0, d) / (28*s)) ** 2
        i = (y*SIZE + x)*4
        if cov > 0:
            c = sample(x+0.5, y+0.5)
            a = cov + shadow*(1-cov)
            for k in range(3): rgba[i+k] = round(c[k]*cov/a)       # shadow is black: only body adds colour
            rgba[i+3] = round(a*255)
        elif shadow > 0:
            rgba[i+3] = round(shadow*255)
write_png(out, SIZE, SIZE, rgba)
print('wrote', out, SIZE)
