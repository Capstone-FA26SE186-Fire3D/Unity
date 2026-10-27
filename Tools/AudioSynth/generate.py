"""Synthesises the sound effects that have no free recording (mono 16-bit WAV, 22.05 kHz).

Run:  python Tools/AudioSynth/generate.py
Writes Assets/_Fire3D/Art/Audio/:
  ExtinguisherPin   safety pin pulled out of the valve: short scrape, click, ringing steel
  CallPointPress    manual call point: plastic press, latch snap, low thump
  SprinklerSpray    pendent sprinkler indoors: dense water hiss with droplet patter (seamless loop)
  PowerCut          breaker trips: relay clunk, then mains hum dying away
  Crawl0..2         knees and palms shuffling on a hard floor (cloth rustle)
Pure Python (no numpy) so it runs on any machine with Python 3. Fixed seeds: output is the same every run.
"""
import math
import random
import struct
import wave
from pathlib import Path

RATE = 22050
OUT = Path(__file__).resolve().parents[2] / "Assets" / "_Fire3D" / "Art" / "Audio"


def write(name, samples, peak=0.9):
    top = max(1e-9, max(abs(s) for s in samples))
    scale = peak / top
    OUT.mkdir(parents=True, exist_ok=True)
    with wave.open(str(OUT / f"{name}.wav"), "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(b"".join(struct.pack("<h", int(max(-1.0, min(1.0, s * scale)) * 32767)) for s in samples))
    print(f"wrote {name}.wav ({len(samples) / RATE:.2f} s)")


def n(seconds):
    return int(seconds * RATE)


def silence(seconds):
    return [0.0] * n(seconds)


def mix(dst, src, at=0.0, gain=1.0):
    o = n(at)
    for i, s in enumerate(src):
        if o + i < len(dst):
            dst[o + i] += s * gain
    return dst


def lowpass(x, cutoff):
    a = 1.0 - math.exp(-2.0 * math.pi * cutoff / RATE)
    y, out = 0.0, []
    for s in x:
        y += a * (s - y)
        out.append(y)
    return out


def highpass(x, cutoff):
    low = lowpass(x, cutoff)
    return [s - l for s, l in zip(x, low)]


def bandpass(x, lo, hi):
    return lowpass(highpass(x, lo), hi)


def noise(rng, seconds):
    return [rng.uniform(-1.0, 1.0) for _ in range(n(seconds))]


def envelope(x, attack, decay, hold=0.0):
    out = []
    for i, s in enumerate(x):
        t = i / RATE
        if t < attack:
            g = t / max(attack, 1e-6)
        elif t < attack + hold:
            g = 1.0
        else:
            g = math.exp(-(t - attack - hold) / max(decay, 1e-6))
        out.append(s * g)
    return out


def tone(freq, seconds, decay, phase=0.0):
    return [math.sin(2 * math.pi * freq * i / RATE + phase) * math.exp(-i / RATE / decay) for i in range(n(seconds))]


def click(rng, seconds=0.006, lo=1500, hi=8000):
    return envelope(bandpass(noise(rng, seconds * 4), lo, hi), 0.0005, seconds)


def extinguisher_pin():
    rng = random.Random(11)
    out = silence(0.55)
    # The pin scraping through the handle eye, rising in pitch as it slides out.
    scrape = bandpass(noise(rng, 0.14), 2500, 7000)
    scrape = [s * (0.3 + 0.7 * math.sin(math.pi * i / len(scrape))) * (1 + 0.5 * math.sin(i * 0.09)) for i, s in enumerate(scrape)]
    mix(out, scrape, 0.0, 0.35)
    mix(out, click(rng, 0.004, 2000, 9000), 0.14, 1.0)
    # Small steel parts ringing (inharmonic partials).
    for f, g, d in ((2310, 0.5, 0.09), (3480, 0.35, 0.07), (5170, 0.25, 0.05), (1180, 0.3, 0.12)):
        mix(out, tone(f, 0.4, d, rng.random() * 6), 0.142, g)
    return out


def call_point_press():
    rng = random.Random(23)
    out = silence(0.35)
    mix(out, click(rng, 0.008, 800, 5000), 0.0, 0.7)                  # finger on the plastic element
    mix(out, tone(180, 0.12, 0.03), 0.0, 0.5)                         # body thump through the wall box
    mix(out, click(rng, 0.004, 1500, 9000), 0.055, 1.0)               # latch snaps into the alarm position
    mix(out, tone(1250, 0.1, 0.02), 0.055, 0.25)
    return out


def sprinkler_spray(seconds=4.0):
    rng = random.Random(37)
    length = n(seconds)
    fade = n(0.5)
    base = noise(rng, seconds + 0.5)
    hiss = bandpass(base, 900, 7500)
    body = lowpass(highpass(noise(rng, seconds + 0.5), 200), 1200)
    x = [h * 0.8 + b * 0.5 for h, b in zip(hiss, body)]
    # Slow pressure wobble and droplets landing on the floor and furniture.
    x = [s * (0.85 + 0.15 * math.sin(2 * math.pi * 0.6 * i / RATE)) for i, s in enumerate(x)]
    for _ in range(int(seconds * 90)):
        drop = envelope(bandpass(noise(rng, 0.02), 1200, 6000), 0.0005, rng.uniform(0.004, 0.012))
        mix(x, drop, rng.uniform(0, seconds + 0.4), rng.uniform(0.2, 0.6))
    # Seamless loop: cross-fade the tail into the head.
    out = x[:length]
    for i in range(fade):
        w = i / fade
        out[i] = out[i] * w + x[length + i] * (1 - w)
    return out


def power_cut():
    rng = random.Random(41)
    out = silence(1.4)
    mix(out, tone(70, 0.3, 0.06), 0.0, 0.9)                            # breaker clunk
    mix(out, envelope(bandpass(noise(rng, 0.05), 300, 3000), 0.001, 0.02), 0.0, 0.8)
    mix(out, click(rng, 0.005, 1500, 8000), 0.012, 0.6)
    # Mains hum (50 Hz + harmonics) dying with a falling pitch as transformers spin down.
    hum = []
    for i in range(n(1.3)):
        t = i / RATE
        f = 50.0 * (1.0 - 0.25 * t)
        g = math.exp(-t / 0.35)
        hum.append(g * sum(a * math.sin(2 * math.pi * f * k * t) for k, a in ((1, 0.5), (2, 0.8), (3, 0.35), (4, 0.2))))
    mix(out, hum, 0.03, 0.45)
    return out


def crawl(seed):
    rng = random.Random(seed)
    out = silence(0.45)
    # Two contacts per shuffle: a palm, then a knee dragging the trouser fabric.
    for at, length, gain in ((0.0, 0.12, 0.7), (0.17, 0.22, 1.0)):
        rustle = bandpass(noise(rng, length), 700, 4200)
        rustle = [s * math.sin(math.pi * i / len(rustle)) ** 2 for i, s in enumerate(rustle)]
        mix(out, rustle, at + rng.uniform(0, 0.02), gain)
        mix(out, lowpass(envelope(noise(rng, 0.04), 0.002, 0.012), 400), at, 0.6 * gain)
    return out


if __name__ == "__main__":
    write("ExtinguisherPin", extinguisher_pin())
    write("CallPointPress", call_point_press())
    write("SprinklerSpray", sprinkler_spray(), peak=0.7)
    write("PowerCut", power_cut())
    for i in range(3):
        write(f"Crawl{i}", crawl(100 + i), peak=0.6)
