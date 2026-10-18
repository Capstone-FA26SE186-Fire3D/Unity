"""Generates the green emergency-exit sign textures (512x256, Vietnamese + English text, running-man pictogram).

Run:  python Tools/SignTextures/generate.py
Writes Assets/_Fire3D/Art/Signs/*.png. Signs, one per situation so they cannot be mistaken for each other:
  ExitFinal      LỐI RA / EXIT          running man towards an open door   (the way out of the building)
  ExitStairDown  THOÁT HIỂM / EXIT      running man down stairs + arrow ↓  (escape stair, going down)
  ExitStairUp    THOÁT HIỂM / EXIT      running man up stairs + arrow ↑
  ExitLeft/Right THOÁT HIỂM / EXIT      running man + arrow ←/→            (follow the corridor)
The pictogram follows the ISO 7010 idea (white on green, figure running to the exit); it is drawn here, not copied.
Font: Be Vietnam Pro (OFL), the same family as the UI.
"""
import math
import os
from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
UNITY = os.path.normpath(os.path.join(HERE, "..", ".."))
OUT = os.path.join(UNITY, "Assets", "_Fire3D", "Art", "Signs")
FONT = os.path.join(UNITY, "Assets", "_Fire3D", "Resources", "UI", "Fonts", "BeVietnamPro_800ExtraBold.ttf")
S = 2                      # supersampling
W, H = 512 * S, 256 * S
GREEN = (0, 132, 66)
WHITE = (255, 255, 255)


def line(d, pts, width):
    w = int(width * S)
    d.line([(x * S, y * S) for x, y in pts], fill=WHITE, width=w, joint="curve")
    for x, y in (pts[0], pts[-1]):
        r = w / 2
        d.ellipse([x * S - r, y * S - r, x * S + r, y * S + r], fill=WHITE)


def running_man(d, ox, oy, scale=1.0):
    """Head, torso, swinging arms and striding legs; origin = top-left of a 150x190 box."""
    def p(x, y):
        return (ox + x * scale, oy + y * scale)
    hx, hy = p(95, 20)
    r = 17 * scale * S
    d.ellipse([hx * S - r, hy * S - r, hx * S + r, hy * S + r], fill=WHITE)
    line(d, [p(82, 52), p(60, 108)], 15 * scale)                         # torso leaning forward
    line(d, [p(80, 60), p(112, 80), p(132, 62)], 11 * scale)             # front arm
    line(d, [p(72, 66), p(44, 82), p(30, 104)], 11 * scale)              # back arm
    line(d, [p(60, 108), p(96, 130), p(88, 170)], 13 * scale)            # front leg
    line(d, [p(60, 108), p(34, 140), p(10, 160)], 13 * scale)            # back leg


def door(d, x, y, w, h, thick=8):
    d.rectangle([x * S, y * S, (x + w) * S, (y + h) * S], outline=WHITE, width=thick * S)
    # open leaf swung towards the viewer
    d.polygon([((x + 6) * S, (y + 6) * S), ((x + w * 0.55) * S, (y + 22) * S), ((x + w * 0.55) * S, (y + h - 4) * S), ((x + 6) * S, (y + h - 6) * S)], fill=WHITE)


def stairs(d, x, y, w, h, descending):
    n = 4
    pts = []
    for i in range(n + 1):
        px = x + w * i / n
        py = (y + h * i / n) if descending else (y + h - h * i / n)
        pts += [(px, py), (px + w / n, py)]
    pts = pts[:-1]
    line(d, pts, 9)


def arrow(d, cx, cy, size, direction):
    s = size * S
    ang = {"right": 0, "down": 90, "left": 180, "up": 270}[direction]
    pts = [(-0.5, -0.18), (0.1, -0.18), (0.1, -0.5), (0.7, 0), (0.1, 0.5), (0.1, 0.18), (-0.5, 0.18)]
    c, sn = math.cos(math.radians(ang)), math.sin(math.radians(ang))
    d.polygon([(cx * S + (x * c - y * sn) * s, cy * S + (x * sn + y * c) * s) for x, y in pts], fill=WHITE)


def text(d, xy, string, size, anchor="lm", max_width=None):
    px = int(size * S)
    font = ImageFont.truetype(FONT, px)
    if max_width is not None:
        # Shrink until the text fits the room beside the pictogram.
        while font.getlength(string) > max_width * S and px > 10:
            px -= 2
            font = ImageFont.truetype(FONT, px)
    d.text((xy[0] * S, xy[1] * S), string, font=font, fill=WHITE, anchor=anchor)


def sign(name, title, pictogram):
    img = Image.new("RGB", (W, H), GREEN)
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([10 * S, 10 * S, (512 - 10) * S, (256 - 10) * S], radius=18 * S, outline=WHITE, width=5 * S)
    pictogram(d)
    text(d, (238, 108), title, 70, max_width=250)
    text(d, (240, 180), "EXIT", 60, max_width=250)
    img.resize((512, 256), Image.LANCZOS).save(os.path.join(OUT, name + ".png"))


def main():
    os.makedirs(OUT, exist_ok=True)

    def final(d):
        running_man(d, 30, 36, 1.0)
        door(d, 172, 46, 44, 150)
        arrow(d, 100, 222, 30, "right")

    def down(d):
        running_man(d, 30, 36, 1.0)
        stairs(d, 140, 120, 70, 70, descending=True)
        arrow(d, 196, 222, 30, "down")

    def up(d):
        running_man(d, 30, 36, 1.0)
        stairs(d, 140, 120, 70, 70, descending=False)
        arrow(d, 196, 222, 30, "up")

    def left(d):
        running_man(d, 70, 40, 1.0)
        arrow(d, 44, 128, 40, "left")

    def right(d):
        running_man(d, 40, 40, 1.0)
        arrow(d, 206, 128, 40, "right")

    sign("ExitFinal", "LỐI RA", final)
    sign("ExitStairDown", "THOÁT HIỂM", down)
    sign("ExitStairUp", "THOÁT HIỂM", up)
    sign("ExitLeft", "THOÁT HIỂM", left)
    sign("ExitRight", "THOÁT HIỂM", right)
    print("wrote", OUT)


if __name__ == "__main__":
    main()
