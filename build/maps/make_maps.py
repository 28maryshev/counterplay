"""Силуэты карт из миникарт Data Dragon (map11 — Ущелье, map12 — Бездна).

Ущелье: полое — видны только три линии (внешнее кольцо и диагональ мида) и
базы в углах, полупрозрачные; лес и река — пусто.
Бездна: мост по диагонали — от оси до тёмной кромки — плюс коричневые выступы
под башни у краёв.

Цвет — градиент полосы LP: #2E7DD1 (левый нижний угол) → #36D6E7 (правый
верхний). Считаем на 512, сохраняем уменьшенным — края сглаживаются сами.

  python make_maps.py <map11.png> <map12.png> <папка> [размер]
"""
import sys

import numpy as np
from PIL import Image

src11, src12, out = sys.argv[1:4]
size = int(sys.argv[4]) if len(sys.argv) > 4 else 192
N = 512
ys, xs = np.mgrid[0:N, 0:N]

A = np.array([0x2E, 0x7D, 0xD1], float)
B = np.array([0x36, 0xD6, 0xE7], float)
t = ((xs + (N - 1 - ys)) / (2 * (N - 1)))[..., None]
color = A * (1 - t) + B * t

GROUND, LANE = 0.36, 0.92


def erode(mask, steps):
    """Сколько шагов до края: расстояние от области «снаружи» (крестом)."""
    dist = np.full(mask.shape, 10**6)
    cur = ~mask
    dist[cur] = 0
    for d in range(1, steps + 1):
        grown = cur.copy()
        grown[1:] |= cur[:-1]; grown[:-1] |= cur[1:]
        grown[:, 1:] |= cur[:, :-1]; grown[:, :-1] |= cur[:, 1:]
        dist[grown & ~cur] = d
        cur = grown
    return dist


def big_parts(mask, min_size):
    """Только связные куски маски не меньше min_size пикселей."""
    seen = np.zeros_like(mask)
    keep = np.zeros_like(mask)
    for y0, x0 in zip(*np.nonzero(mask)):
        if seen[y0, x0]:
            continue
        stack, part = [(y0, x0)], []
        seen[y0, x0] = True
        while stack:
            y, x = stack.pop()
            part.append((y, x))
            for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
                yy, xx = y + dy, x + dx
                if 0 <= yy < N and 0 <= xx < N and mask[yy, xx] and not seen[yy, xx]:
                    seen[yy, xx] = True
                    stack.append((yy, xx))
        if len(part) >= min_size:
            for y, x in part:
                keep[y, x] = True
    return keep


def save(alpha, name):
    rgba = np.dstack([color, alpha * 255]).clip(0, 255).astype(np.uint8)
    # BOX, а не LANCZOS: у того «звон» — тёмные штрихи вдоль краёв линий.
    img = Image.fromarray(rgba, "RGBA").resize((size, size), Image.BOX)
    img.save(f"{out}/{name}")
    return img


# ── Ущелье ──────────────────────────────────────────────────────────────────
im = np.asarray(Image.open(src11).convert("RGB")).astype(int)
lum = (im[..., 0] * 3 + im[..., 1] * 6 + im[..., 2]) / 10
land = lum > 22
olive = (abs(im[..., 0] - 145) < 32) & (abs(im[..., 1] - 154) < 32) & (abs(im[..., 2] - 111) < 32)

# «Снаружи» — чёрное, связанное с краем картинки: заливка от рамки.
outside = np.zeros_like(land)
outside[0, :] = outside[-1, :] = outside[:, 0] = outside[:, -1] = True
outside &= ~land
while True:
    g = outside.copy()
    g[1:] |= outside[:-1]; g[:-1] |= outside[1:]
    g[:, 1:] |= outside[:, :-1]; g[:, :-1] |= outside[:, 1:]
    g &= ~land
    if (g == outside).all():
        break
    outside = g
solid = ~outside
edge = erode(solid, 60)

corner = ((xs < 200) & (ys > N - 201)) | ((xs > N - 201) & (ys < 200))
# Базы — светлое не-оливковое в двух углах (серо-бирюзовые площадки и оба
# нексуса); тёмные обводки стен леса туда не берём.
base = big_parts(land & ~olive & corner & (lum > 70), 2000)
# Линии — ровные полосы: кольцо постоянной ширины вдоль настоящего внешнего
# контура (на миникарте оно шириной ~33 пикселя) и мид по диагонали (±28).
# По оливковому цвету выходили «зубчики» — входы в лес по кромке.
lanes = solid & ~base & ((edge <= 33) | (abs(xs + ys - (N - 1)) <= 28))
alpha = np.where(base, GROUND, 0.0)
alpha = np.where(lanes, LANE, alpha)
save(alpha, "map-rift.png")

# ── Бездна ──────────────────────────────────────────────────────────────────
im = np.asarray(Image.open(src12).convert("RGB")).astype(int)
r, g, b = im[..., 0], im[..., 1], im[..., 2]
lum = (r * 3 + g * 6 + b) / 10
k = (xs + ys - (N - 1)) / 2.0            # смещение поперёк диагонали, как в разборе
along = (xs - ys + (N - 1)) / 2.0        # положение вдоль моста

def grow(m):
    g = m.copy()
    g[1:] |= m[:-1]; g[:-1] |= m[1:]
    g[:, 1:] |= m[:, :-1]; g[:, :-1] |= m[:, 1:]
    return g

def shrink(m):
    return ~grow(~m)

# Мост — всё светлое, до что дотянется заливка от оси, не выходя за чёрную
# обводку (она темнее 22). Внутренние тонкие линии (жёлоб посередине) потом
# закрываются: расширить и сузить на три шага.
light = (lum > 22) & (abs(k) <= 80)
seed = np.zeros((N, N), bool)
for kk in (-16, 0, 16):
    for aa in range(40, 2 * N - 40):
        x = int(round(aa / 2 + kk)); y = int(round((N - 1) - aa / 2 + kk))
        if 0 <= x < N and 0 <= y < N and light[y, x]:
            seed[y, x] = True
bridge = seed
while True:
    nxt = grow(bridge) & light
    if (nxt == bridge).all():
        break
    bridge = nxt
for _ in range(3):
    bridge = grow(bridge)
for _ in range(3):
    bridge = shrink(bridge)

# Выступы под башни — тёмно-коричневые квадраты у краёв моста.
notch = (abs(r - 49) <= 6) & (abs(g - 44) <= 6) & (abs(b - 41) <= 6) & (abs(k) <= 75)
# Между мостом и выступом — чёрная обводка; без неё выступы висели бы
# отдельно. Закрываем объединение, и выступ становится выступом края.
joined = bridge | notch
for _ in range(8):
    joined = grow(joined)
for _ in range(8):
    joined = shrink(joined)
# Оставить только настоящие выступы: обрывки обводки вдоль моста мельче.
keep = big_parts(joined & ~bridge, 120)
alpha = np.where(bridge, LANE, 0.0)
alpha = np.where(keep, GROUND + 0.25, alpha)
save(alpha, "map-abyss.png")
print("готово:", out)
