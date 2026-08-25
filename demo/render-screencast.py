"""Render a terminal screencast GIF from real captured jern output.

Every line of output in this animation was produced by running the command
shown, in the public demo repository, and captured through a pty so the
colors are the ones a real terminal shows.
"""
import re, pathlib, sys
from PIL import Image, ImageDraw, ImageFont

CAST = pathlib.Path(sys.argv[1] if len(sys.argv) > 1 else ".")
OUT = CAST / "screencast.gif"

# jern's terminal palette (ui/index.html + the CLI's Style module).
BG, PANEL, LINE = (0x16, 0x18, 0x1d), (0x1e, 0x21, 0x28), (0x2c, 0x30, 0x39)
INK, DIM = (0xe8, 0xe6, 0xe1), (0x9a, 0x98, 0x90)
RUST, STEEL = (0xc2, 0x64, 0x3a), (0x6f, 0xb0, 0xc4)
RED, GREEN, YELLOW = (0xe0, 0x6c, 0x5a), (0x7a, 0xc0, 0x7a), (0xd4, 0xa2, 0x4a)

W, H = 900, 600
PAD, TOP = 22, 40
FONT = ImageFont.truetype("/System/Library/Fonts/Menlo.ttc", 13)
BOLD = ImageFont.truetype("/System/Library/Fonts/Menlo.ttc", 13, index=1)
CH_W, LINE_H = 8, 18
MAX_LINES = (H - TOP - PAD) // LINE_H

SGR = re.compile(r"\x1b\[([0-9;]*)m")

def parse(raw: str):
    """ANSI text -> list of lines, each a list of (text, color, bold)."""
    lines, cur, color, bold = [], [], INK, False
    raw = raw.replace("\r\n", "\n").replace("\r", "")
    pos = 0
    for m in SGR.finditer(raw):
        chunk = raw[pos:m.start()]
        pos = m.end()
        for i, piece in enumerate(chunk.split("\n")):
            if i:
                lines.append(cur); cur = []
            if piece:
                cur.append((piece, color, bold))
        codes = [c for c in m.group(1).split(";") if c != ""] or ["0"]
        i = 0
        while i < len(codes):
            c = codes[i]
            if c == "0": color, bold = INK, False
            elif c == "1": bold = True
            elif c == "2": color = DIM
            elif c == "31": color = RED
            elif c == "32": color = GREEN
            elif c == "33": color = YELLOW
            elif c == "36": color = STEEL
            elif c == "38" and i + 2 < len(codes) and codes[i + 1] == "5":
                n = int(codes[i + 2]); i += 2
                color = RUST if n == 173 else STEEL if n == 110 else INK
            i += 1
    for i, piece in enumerate(raw[pos:].split("\n")):
        if i:
            lines.append(cur); cur = []
        if piece:
            cur.append((piece, color, bold))
    lines.append(cur)
    return lines

def frame(lines):
    img = Image.new("RGB", (W, H), BG)
    d = ImageDraw.Draw(img)
    d.rectangle([0, 0, W, TOP - 8], fill=PANEL)
    d.line([(0, TOP - 8), (W, TOP - 8)], fill=LINE)
    for i, c in enumerate([(0xe0, 0x6c, 0x5a), (0xd4, 0xa2, 0x4a), (0x7a, 0xc0, 0x7a)]):
        d.ellipse([PAD + i * 18, 12, PAD + i * 18 + 9, 21], fill=c)
    d.text((PAD + 70, 11), "jern-demo — an agent under repository rules", font=FONT, fill=DIM)
    y = TOP
    for line in lines[-MAX_LINES:]:
        x = PAD
        for text, color, bold in line:
            d.text((x, y), text, font=BOLD if bold else FONT, fill=color)
            x += len(text) * CH_W
        y += LINE_H
    return img

frames, delays = [], []
screen = []          # committed lines
def push(img_lines, ms):
    frames.append(frame(img_lines)); delays.append(ms)

def prompt(cmd_shown):
    return [("$ ", RUST, True), (cmd_shown, INK, False)]

def run(cmd, raw_file, hold=2600, reveal=3):
    """Type the command, then reveal its captured output."""
    global screen
    for n in range(0, len(cmd) + 1, 4):
        push(screen + [prompt(cmd[:n])], 55)
    push(screen + [prompt(cmd)], 380)
    screen = screen + [prompt(cmd)]
    out = parse(pathlib.Path(raw_file).read_text(errors="replace"))
    out = [l for l in out]
    while out and not out[-1]:
        out.pop()
    for i in range(0, len(out), reveal):
        screen_now = screen + out[: i + reveal]
        push(screen_now, 120)
    screen = screen + out
    push(screen, hold)
    screen = screen + [[]]

push([], 900)
run("jern policy", CAST / "policy.raw", hold=4200, reveal=4)
run("jern golden check", CAST / "check-ok.raw", hold=2600)
run("git diff jern.json", CAST / "diff.raw", hold=3000, reveal=4)
run("jern golden check", CAST / "check-fail.raw", hold=4600)
run("jern receipt .jern/golden/fix-the-failing-test.jsonl", CAST / "receipt.raw", hold=4800, reveal=4)
push(screen, 1400)

frames[0].save(OUT, save_all=True, append_images=frames[1:], duration=delays,
               loop=0, optimize=True, disposal=2)
poster = next(i for i in range(len(frames) - 1, -1, -1) if delays[i] > 4000)
frames[poster].save(CAST / "poster.png")
print("frames:", len(frames), "| seconds:", round(sum(delays) / 1000, 1),
      "| size:", round(OUT.stat().st_size / 1e6, 2), "MB")
