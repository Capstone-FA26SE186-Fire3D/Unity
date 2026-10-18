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
    # Strong top-lit shading: bright crowns, dark crevices between billows, so a column reads as rolling smoke.
    shade = np.clip(0.55 + (-gx * 0.5 + gy * 1.3) * 7.0 - y * 0.18 + (n - 0.5) * 0.5, 0.12, 1.0)
    alpha = smoothstep(0.04, 0.5, density) * 0.95
    return shade, alpha


SIX_N = 64            # voxel grid per frame
SIX_K = 6.0           # extinction scale (optical depth of a full-density column)


def smoke_density(noise, t, puff):
    """3D smoke density in [-1,1]^3, indexed [iy, ix, iz] (iy 0 = bottom), looping in t."""
    n = SIX_N
    c = (np.arange(n, dtype=np.float32) + 0.5) / n * 2 - 1
    y, x, z = np.meshgrid(c, c, c, indexing="ij")
    r = np.sqrt(x * x + y * y + z * z)
    grow = 0.78 + 0.16 * t

    def field(tt):
        return noise.fbm(x * 2.3 + puff * 3.1 + tt * 0.5, y * 2.3 - tt * 0.7, z * 2.3 + puff * 1.3 + tt * 0.4, octaves=5, gain=0.6)

    f = looped(field, t)
    f = np.clip((f - 0.5) * 3.2 + 0.5, 0, 1)
    rr = r + (f - 0.5) * 1.15
    soft = np.exp(-(rr / grow) ** 2 * 1.5) * (1 - smoothstep(0.85, 1.0, r))
    return np.clip(soft * 1.7 - (1 - f) * 0.95, 0, 1)


def six_way_frame(noise, t):
    """Bakes one frame: transparency plus the light reaching the visible smoke from each of six directions.

    The camera looks down -z (image x right, y up). A light "from the right" shines along -x, and so on. For each
    direction the lit value of a pixel is the density- and visibility-weighted mean of exp(-K * density between the
    light and the voxel), so the lit side of a billow is bright and its far side dark.
    """
    d = smoke_density(noise, t, puff=0.0)          # [iy, ix, iz]
    dz = 2.0 / SIX_N
    k = SIX_K * dz

    def transmit(axis, from_high):
        # Optical depth accumulated from the light's side up to (not including) each voxel.
        a = np.flip(d, axis) if from_high else d
        depth = np.cumsum(a, axis=axis) - a
        depth = np.flip(depth, axis) if from_high else depth
        return np.exp(-k * depth)

    # Camera sits at +z: visibility of a voxel = transmittance from +z.
    view = transmit(2, True)
    weight = d * view
    norm = weight.sum(axis=2) + 1e-4

    def lit(axis, from_high):
        return (weight * transmit(axis, from_high)).sum(axis=2) / norm

    alpha = 1 - np.exp(-k * d.sum(axis=2))
    right, left = lit(1, True), lit(1, False)         # +x / -x
    top, bottom = lit(0, True), lit(0, False)         # +y / -y
    front, back = lit(2, True), lit(2, False)         # +z (camera side) / -z
    flip = lambda a: np.flipud(a)                      # row 0 = top of the image
    return [flip(c).astype(np.float32) for c in (right, top, back, alpha, left, bottom, front)]


def upscale(chan):
    img = Image.fromarray((np.clip(chan, 0, 1) * 255 + 0.5).astype(np.uint8), "L").resize((CELL, CELL), Image.BICUBIC)
    return np.asarray(img, dtype=np.float32) / 255.0


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

    # Six-way lit smoke: two linear RGBA atlases (see Unity "Realistic smoke with 6-way lighting").
    # A: R=light from right, G=from top, B=from behind, A=transparency.  B: R=from left, G=from bottom, B=from the camera side.
    volume_noise = Noise3(2718)
    frames_a, frames_b = [], []
    for f in range(FRAMES):
        right, top, back, alpha, left, bottom, front = (upscale(c) for c in six_way_frame(volume_noise, f / FRAMES))
        frames_a.append(np.dstack([right, top, back, alpha]))
        frames_b.append(np.dstack([left, bottom, front, np.ones_like(alpha)]))
    atlas(frames_a).save(os.path.join(OUT, "SmokeLightA.png"))
    atlas(frames_b).save(os.path.join(OUT, "SmokeLightB.png"))

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
