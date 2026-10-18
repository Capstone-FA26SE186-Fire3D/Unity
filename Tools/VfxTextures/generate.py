"""Generates the fire/smoke flipbooks used by the Unity VFX (8x8 frames, seamless loop).

Run:  python Tools/VfxTextures/generate.py
Writes Assets/_Fire3D/Art/VFX/{FlameFlipbook,SmokeFlipbook,SoftGlow}.png. Deterministic (fixed seeds).

Flame: domain-warped fBm scrolling upward inside a teardrop envelope; heat is mapped to a blackbody-like ramp
(white-yellow core -> orange -> deep red) with alpha from heat, for additive blending. Smoke: self-shadowed
billow puffs that slowly churn and grow, for alpha blending.
"""
import os
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "..", "Assets", "_Fire3D", "Art", "VFX"))
FRAMES = 64
GRID = 8
CELL = 128


class Noise3:
    """Tileable-in-time 3D value noise with smooth interpolation."""

    def __init__(self, seed, size=64):
        rng = np.random.default_rng(seed)
        self.size = size
        self.table = rng.random((size, size, size)).astype(np.float32)

    def __call__(self, x, y, z):
        s = self.size
        xi, yi, zi = np.floor(x).astype(int), np.floor(y).astype(int), np.floor(z).astype(int)
        xf, yf, zf = x - xi, y - yi, z - zi
        u, v, w = (t * t * t * (t * (t * 6 - 15) + 10) for t in (xf, yf, zf))
        def g(dx, dy, dz):
            return self.table[(xi + dx) % s, (yi + dy) % s, (zi + dz) % s]
        x00 = g(0, 0, 0) * (1 - u) + g(1, 0, 0) * u
        x10 = g(0, 1, 0) * (1 - u) + g(1, 1, 0) * u
        x01 = g(0, 0, 1) * (1 - u) + g(1, 0, 1) * u
        x11 = g(0, 1, 1) * (1 - u) + g(1, 1, 1) * u
        y0 = x00 * (1 - v) + x10 * v
        y1 = x01 * (1 - v) + x11 * v
        return y0 * (1 - w) + y1 * w

    def fbm(self, x, y, z, octaves=5, lacunarity=2.0, gain=0.5):
        total, amp, freq, norm = 0.0, 1.0, 1.0, 0.0
        for o in range(octaves):
            total = total + amp * self(x * freq + o * 17.3, y * freq + o * 9.1, z * freq + o * 5.7)
            norm += amp
            amp *= gain
            freq *= lacunarity
        return total / norm


def looped(fn, t):
    """Seamless loop over t in [0,1): cross-fade the field at t and t-1."""
    return fn(t) * (1 - t) + fn(t - 1.0) * t


def smoothstep(a, b, x):
    t = np.clip((x - a) / (b - a), 0, 1)
    return t * t * (3 - 2 * t)


def flame_frame(noise, t):
    ys, xs = np.mgrid[0:CELL, 0:CELL].astype(np.float32)
    u = (xs + 0.5) / CELL * 2 - 1          # -1..1 across
    v = 1 - (ys + 0.5) / CELL              # 0 bottom .. 1 top

    def field(tt):
        rise = tt * 3.0
        warp = noise.fbm(u * 1.6, v * 2.2 - rise, np.full_like(u, tt * 2.0), octaves=3)
        n = noise.fbm(u * 2.4 + (warp - 0.5) * 1.4, v * 3.4 - rise * 1.6, np.full_like(u, 3.7 + tt * 2.0), octaves=5)
        return n, warp

    n, warp = looped(lambda tt: np.stack(field(tt)), t)
    # Soft gaussian envelope narrowing upward; sideways sway grows with height.
    sway = (warp - 0.5) * 0.7 * v ** 1.1
    width = 0.52 * (1 - v) ** 0.6 + 0.07
    lateral = np.exp(-((u + sway) / width) ** 2 * 2.2)
    rounded = smoothstep(0.0, 0.2, v + 0.18 - np.abs(u) * 0.45)
    env = lateral * rounded * (1 - v) ** 0.45
    # Noise erodes the envelope into licking tongues, more strongly higher up.
    heat = env * 1.35 - (1 - n) * (0.32 + 0.85 * v)
    heat = np.clip(heat, 0, 1) ** 1.25
    heat *= 1 - smoothstep(0.85, 1.0, v)
    return heat


def ramp(heat):
    """Blackbody-ish: deep red -> orange -> yellow -> white core."""
    stops = np.array([0.0, 0.22, 0.48, 0.78, 1.0], dtype=np.float32)
    colors = np.array([
        [0.25, 0.02, 0.0],
        [0.85, 0.16, 0.02],
        [1.0, 0.46, 0.06],
        [1.0, 0.72, 0.24],
        [1.0, 0.93, 0.72],
    ], dtype=np.float32)
    out = np.empty(heat.shape + (3,), dtype=np.float32)
    for c in range(3):
        out[..., c] = np.interp(heat, stops, colors[:, c])
    return out


def smoke_frame(noise, t, puff):
    ys, xs = np.mgrid[0:CELL, 0:CELL].astype(np.float32)
    x = (xs + 0.5) / CELL * 2 - 1
    y = (ys + 0.5) / CELL * 2 - 1
    r = np.sqrt(x * x + y * y)
    grow = 0.78 + 0.18 * t

    def field(tt):
        return noise.fbm(x * 1.3 + puff * 3.1, y * 1.3 - tt * 0.45, np.full_like(x, puff * 1.7 + tt * 1.1), octaves=4, gain=0.55)

    n = np.clip((looped(field, t) - 0.5) * 2.6 + 0.5, 0, 1)  # fBm clusters near 0.5: stretch contrast.
    rr = r + (n - 0.5) * 0.7
    soft = np.exp(-(rr / grow) ** 2 * 1.3) * (1 - smoothstep(0.85, 1.0, r))
    density = np.clip(soft * 1.35 - (1 - n) * 0.8, 0, 1)
    # Gentle self-shadowing from above: billows read as volume without hard craters.
    blur = density
    for _ in range(2):
        blur = (blur + np.roll(blur, 1, 0) + np.roll(blur, -1, 0) + np.roll(blur, 1, 1) + np.roll(blur, -1, 1)) / 5
    gy, gx = np.gradient(blur)
    shade = np.clip(0.72 + (-gx * 0.4 + gy * 1.0) * 3.0 - y * 0.1, 0.4, 1.0)
    alpha = smoothstep(0.0, 0.6, density) * 0.92
    return shade, alpha


def atlas(frames_rgba):
    img = np.zeros((GRID * CELL, GRID * CELL, 4), dtype=np.float32)
    for i, f in enumerate(frames_rgba):
        gx, gy = i % GRID, i // GRID
        img[gy * CELL:(gy + 1) * CELL, gx * CELL:(gx + 1) * CELL] = f
    return Image.fromarray((np.clip(img, 0, 1) * 255 + 0.5).astype(np.uint8), "RGBA")


def main():
    os.makedirs(OUT, exist_ok=True)
    flame_noise = Noise3(1729)
    frames = []
    for f in range(FRAMES):
        heat = flame_frame(flame_noise, f / FRAMES)
        rgb = ramp(heat)
        alpha = np.clip(heat ** 0.7 * 1.15, 0, 1)
        frames.append(np.dstack([rgb, alpha]))
    atlas(frames).save(os.path.join(OUT, "FlameFlipbook.png"))

    smoke_noise = Noise3(4242)
    frames = []
    for f in range(FRAMES):
        shade, alpha = smoke_frame(smoke_noise, f / FRAMES, puff=0.0)
        frames.append(np.dstack([shade, shade, shade, alpha]))
    atlas(frames).save(os.path.join(OUT, "SmokeFlipbook.png"))

    n = 128
    ys, xs = np.mgrid[0:n, 0:n].astype(np.float32)
    d = np.sqrt(((xs + 0.5) / n * 2 - 1) ** 2 + ((ys + 0.5) / n * 2 - 1) ** 2)
    a = np.clip(1 - d, 0, 1) ** 2.4
    glow = np.dstack([np.ones_like(a), np.ones_like(a), np.ones_like(a), a])
    Image.fromarray((glow * 255 + 0.5).astype(np.uint8), "RGBA").save(os.path.join(OUT, "SoftGlow.png"))
    # Tileable distortion noise for the heat haze (R, G = two independent offsets). A table of period 8 sampled
    # over [0, 8) with integer lacunarity keeps every octave periodic.
    hn_r, hn_g = Noise3(77, size=8), Noise3(91, size=8)
    n = 128
    ys, xs = np.mgrid[0:n, 0:n].astype(np.float32)
    x, y = xs / n * 8, ys / n * 8
    z = np.zeros_like(x)
    r = hn_r.fbm(x, y, z, octaves=4)
    g = hn_g.fbm(x, y, z, octaves=4)
    stretch = lambda a: np.clip((a - 0.5) * 2.2 + 0.5, 0, 1)
    haze = np.dstack([stretch(r), stretch(g), np.zeros_like(r), np.ones_like(r)])
    Image.fromarray((haze * 255 + 0.5).astype(np.uint8), "RGBA").save(os.path.join(OUT, "HazeNoise.png"))
    print("wrote", OUT)


if __name__ == "__main__":
    main()
