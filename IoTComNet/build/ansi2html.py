#!/usr/bin/env python3
"""Converts ANSI (24-bit SGR) terminal output to a self-contained HTML page for screenshots.
Usage: python build/ansi2html.py input.ansi output.html "Title" """
import html, re, sys

src, dst, title = sys.argv[1], sys.argv[2], sys.argv[3] if len(sys.argv) > 3 else "iotcom"
text = open(src, encoding="utf-8", errors="replace").read()
out, fg, bg, bold = [], None, None, False
for part in re.split(r"(\x1b\[[0-9;]*m)", text):
    m = re.fullmatch(r"\x1b\[([0-9;]*)m", part)
    if not m:
        if part:
            style = []
            if fg: style.append(f"color:{fg}")
            if bg: style.append(f"background:{bg}")
            if bold: style.append("font-weight:700")
            out.append(f'<span style="{";".join(style)}">{html.escape(part)}</span>' if style else html.escape(part))
        continue
    codes = [int(c) for c in m.group(1).split(";") if c] or [0]
    i = 0
    while i < len(codes):
        c = codes[i]
        if c == 0: fg, bg, bold = None, None, False
        elif c == 1: bold = True
        elif c == 22: bold = False
        elif c == 39: fg = None
        elif c == 49: bg = None
        elif c in (38, 48) and i + 2 < len(codes) and codes[i + 1] == 5:
            n = codes[i + 2]
            base = ["#000000", "#d23b2f", "#2e9e5b", "#f2a900", "#2f6fd6", "#7a5bb5", "#2aa1b3", "#c0c0c0",
                    "#7c848c", "#e2554a", "#43b270", "#ffc533", "#4f8ce6", "#9b7fd1", "#4fc3d4", "#ffffff"]
            col = base[n] if n < 16 else "#%02x%02x%02x" % ((n - 16) // 36 * 51, (n - 16) // 6 % 6 * 51, (n - 16) % 6 * 51) if n < 232 else "#%02x%02x%02x" % ((8 + (n - 232) * 10),) * 3
            if c == 38: fg = col
            else: bg = col
            i += 2
        elif c in (38, 48) and i + 4 < len(codes) and codes[i + 1] == 2:
            col = "#%02x%02x%02x" % tuple(codes[i + 2:i + 5])
            if c == 38: fg = col
            else: bg = col
            i += 4
        elif 30 <= c <= 37: fg = ["#1e2226", "#d23b2f", "#2e9e5b", "#f2a900", "#2f6fd6", "#7a5bb5", "#2aa1b3", "#e4e5e0"][c - 30]
        elif 90 <= c <= 97: fg = ["#7c848c", "#e2554a", "#43b270", "#ffc533", "#4f8ce6", "#9b7fd1", "#4fc3d4", "#ffffff"][c - 90]
        i += 1
page = f"""<!doctype html><html><head><meta charset="utf-8"><title>{html.escape(title)}</title>
<style>body{{margin:0;background:#1e2226;font:15px/1.4 'Cascadia Mono',Consolas,monospace;color:#e4e5e0}}
.win{{margin:22px;border-radius:12px;background:#15181b;box-shadow:0 10px 30px rgba(0,0,0,.4);overflow:hidden;border:1px solid #2b3036}}
.bar{{background:#2b3036;padding:10px 14px;display:flex;gap:8px;align-items:center;color:#9aa1a8;font-size:12px}}
.bar i{{width:12px;height:12px;border-radius:50%;display:inline-block}}
pre{{margin:0;padding:18px 22px;white-space:pre;font:inherit}}</style></head>
<body><div class="win"><div class="bar"><i style="background:#d23b2f"></i><i style="background:#f2a900"></i><i style="background:#2e9e5b"></i>&nbsp; {html.escape(title)}</div><pre>{''.join(out)}</pre></div></body></html>"""
open(dst, "w", encoding="utf-8").write(page)
print("wrote", dst)
