"""Renders the fire-safety signs (512x256 PNG): ISO 7010 pictogram + Vietnamese text.

Run:  python Tools/SignTextures/safety_signs.py <folder with the ISO 7010 SVGs>
Writes Assets/_Fire3D/Art/Signs/{NoLift,Extinguisher,CallPoint}.png.
Pictograms: ISO 7010 P020/F001/F005 from Wikimedia Commons (public domain, author MaxxL).
Text: Be Vietnam Pro (OFL). Rendering uses headless Edge/Chrome so no Python imaging package is needed.
"""
import os
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

HERE = Path(__file__).resolve().parent
UNITY = HERE.parent.parent
OUT = UNITY / "Assets" / "_Fire3D" / "Art" / "Signs"
FONT = UNITY / "Assets" / "_Fire3D" / "Resources" / "UI" / "Fonts" / "BeVietnamPro_800ExtraBold.ttf"
BROWSERS = [
    r"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe",
    r"C:\Program Files\Google\Chrome\Application\chrome.exe",
]

# name: (pictogram svg, background, text colour, lines)
SIGNS = {
    "NoLift": ("ISO_7010_P020.svg", "#ffffff", "#1a1a1a", ["KHÔNG DÙNG", "THANG MÁY", "KHI CÓ CHÁY"]),
    "Extinguisher": ("ISO_7010_F001.svg", "#c8102e", "#ffffff", ["BÌNH", "CHỮA CHÁY"]),
    "CallPoint": ("ISO_7010_F005.svg", "#c8102e", "#ffffff", ["NÚT ẤN", "BÁO CHÁY"]),
}

PAGE = """<!doctype html><html><head><meta charset="utf-8"><style>
@font-face {{ font-family: Sign; src: url("{font}"); }}
html, body {{ margin: 0; width: 512px; height: 256px; overflow: hidden; background: {bg}; }}
.sign {{ box-sizing: border-box; width: 512px; height: 256px; display: flex; align-items: center; gap: 22px;
  padding: 0 26px; border: 8px solid {border}; border-radius: 14px; background: {bg}; }}
img {{ width: 170px; height: 170px; flex: none; }}
.t {{ font: 800 {size}px/1.08 Sign; color: {fg}; letter-spacing: 0.01em; white-space: nowrap; }}
</style></head><body><div class="sign"><img src="{svg}"><div class="t">{text}</div></div></body></html>"""


def main(svg_dir):
    browser = next((b for b in BROWSERS if os.path.exists(b)), None)
    if browser is None:
        sys.exit("Edge or Chrome is required")
    OUT.mkdir(parents=True, exist_ok=True)
    work = Path(tempfile.mkdtemp(prefix="signs-", dir=str(UNITY / "Temp") if (UNITY / "Temp").exists() else None))
    try:
        for name, (svg, bg, fg, lines) in SIGNS.items():
            size = 35 if len(lines) == 3 else 44
            border = "#c8102e" if bg == "#ffffff" else "#ffffff"
            html = work / f"{name}.html"
            html.write_text(PAGE.format(font=FONT.as_uri(), bg=bg, border=border, fg=fg, size=size,
                                        svg=(Path(svg_dir) / svg).resolve().as_uri(), text="<br>".join(lines)), encoding="utf-8")
            png = OUT / f"{name}.png"
            subprocess.run([browser, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
                            f"--user-data-dir={work / 'profile'}", "--window-size=512,256", f"--screenshot={png}", html.as_uri()],
                           check=True, capture_output=True, timeout=60)
            print("wrote", png)
    finally:
        shutil.rmtree(work, ignore_errors=True)


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else ".")
