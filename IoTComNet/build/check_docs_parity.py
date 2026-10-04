#!/usr/bin/env python3
"""Documentation gate (runs in CI).

1. Parity: every docs/en/**/*.md has a docs/id twin (and vice versa), and both declare
   `translation-status` in front-matter. An `outdated` status is reported as a warning.
2. Links: every relative Markdown link and image in docs/, README*.md and samples/ resolves to a file.

Exit code 1 on any error. Run: python build/check_docs_parity.py
"""
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
EN, ID = os.path.join(ROOT, "docs", "en"), os.path.join(ROOT, "docs", "id")
errors, warnings = [], []


def pages(base):
    for d, _, files in os.walk(base):
        for f in files:
            if f.endswith(".md"):
                yield os.path.relpath(os.path.join(d, f), base).replace("\\", "/")


en, id_ = set(pages(EN)), set(pages(ID))
for p in sorted(en - id_):
    errors.append(f"missing Indonesian translation: docs/id/{p}")
for p in sorted(id_ - en):
    errors.append(f"Indonesian page without English source: docs/id/{p}")
for p in sorted(en | id_):
    for base, lang in ((EN, "en"), (ID, "id")):
        path = os.path.join(base, p)
        if not os.path.exists(path):
            continue
        head = open(path, encoding="utf-8").read(400)
        m = re.search(r"translation-status:\s*(\w+)", head)
        if not m:
            errors.append(f"docs/{lang}/{p}: missing translation-status front-matter")
        elif m.group(1) == "outdated":
            warnings.append(f"docs/{lang}/{p}: translation marked outdated")

LINK = re.compile(r"!?\[[^\]]*\]\(([^)\s]+)\)")
md_files = [os.path.join(d, f) for top in ("docs", "samples") for d, _, fs in os.walk(os.path.join(ROOT, top)) for f in fs if f.endswith(".md")]
md_files += [os.path.join(ROOT, f) for f in os.listdir(ROOT) if f.endswith(".md")]
for path in md_files:
    text = open(path, encoding="utf-8").read()
    text = re.sub(r"```.*?```", "", text, flags=re.S)  # ignore code blocks
    for target in LINK.findall(text):
        if re.match(r"^[a-z]+:", target) or target.startswith("#"):
            continue
        file_part = target.split("#")[0]
        if not file_part:
            continue
        resolved = os.path.normpath(os.path.join(os.path.dirname(path), file_part))
        if not os.path.exists(resolved):
            errors.append(f"{os.path.relpath(path, ROOT)}: broken link -> {target}")

for w in warnings:
    print("warning:", w)
for e in errors:
    print("error:", e)
print(f"docs: {len(en)} EN pages, {len(id_)} ID pages, {len(md_files)} markdown files checked, {len(errors)} errors")
sys.exit(1 if errors else 0)
