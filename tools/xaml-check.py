#!/usr/bin/env python3
"""Comprueba los XAML de PowerTools sin abrir la app.

Detecta fallos que solo se ven al cargar una ventana:
  - claves de recurso referenciadas y no definidas (XamlParseException)
  - Storyboard.TargetName sin x:Name en el mismo XAML
  - x:Class sin code-behind o con una partial class distinta
  - handlers de evento sin método en el code-behind

Uso:  python3 tools/xaml-check.py [ruta-al-proyecto]
Sale con código 1 si encuentra algo.
"""
import re
import sys
from collections import defaultdict
from pathlib import Path

ROOT = Path(sys.argv[1] if len(sys.argv) > 1 else
            Path(__file__).resolve().parent.parent / "src" / "PowerTools")

xamls = sorted(ROOT.rglob("*.xaml"))
if not xamls:
    print(f"no hay XAML en {ROOT}")
    sys.exit(2)

KEY_RE = re.compile(r'x:Key\s*=\s*"([^"]+)"')
REFS = re.compile(r'\{(StaticResource|DynamicResource)\s+(?:ResourceKey\s*=\s*)?([^}\s]+)\s*\}')

defined = defaultdict(set)
all_keys = set()
for f in xamls:
    txt = f.read_text(encoding="utf-8", errors="replace")
    for k in KEY_RE.findall(txt):
        defined[f].add(k)
        all_keys.add(k)

SYSTEM = {"SystemColors", "SystemFonts", "SystemParameters"}

missing = []
for f in xamls:
    txt = f.read_text(encoding="utf-8", errors="replace")
    for m in REFS.finditer(txt):
        kind, key = m.group(1), m.group(2).strip().strip('"').strip("'")
        if not key or key.startswith("{") or key.startswith("x:") or key in all_keys or key in SYSTEM:
            continue
        missing.append((f.relative_to(ROOT), txt[:m.start()].count("\n") + 1, kind, key))

# Storyboard.TargetName
tn_missing = []
for f in xamls:
    txt = f.read_text(encoding="utf-8", errors="replace")
    names = set(re.findall(r'x:Name\s*=\s*"([^"]+)"', txt))
    for m in re.finditer(r'(?:Storyboard\.)?TargetName\s*=\s*"([^"]+)"', txt):
        if m.group(1) and m.group(1) not in names:
            tn_missing.append((f.relative_to(ROOT), txt[:m.start()].count("\n") + 1, m.group(1)))

# x:Class ↔ code-behind
bad_class = []
for f in xamls:
    txt = f.read_text(encoding="utf-8", errors="replace")
    m = re.search(r'x:Class\s*=\s*"([^"]+)"', txt)
    if not m:
        continue
    short = m.group(1).split(".")[-1]
    cs = f.with_suffix(".xaml.cs")
    if not cs.exists():
        bad_class.append((f.relative_to(ROOT), m.group(1), "sin .cs"))
        continue
    body = cs.read_text(encoding="utf-8", errors="replace")
    if re.search(r'\bpartial\s+class\s+' + re.escape(short) + r'\b', body) is None:
        bad_class.append((f.relative_to(ROOT), m.group(1), "el .cs no declara la partial class"))

# Handlers
EVENTS = ("Click|Checked|Unchecked|SelectionChanged|TextChanged|MouseDown|MouseUp|KeyDown|KeyUp|"
          "Loaded|Closing|Closed|Drop|DragOver|DragEnter|DragLeave|SizeChanged|ValueChanged|"
          "Expanded|Collapsed|Opened|ScrollChanged|PreviewMouseDown|PreviewKeyDown|LostFocus|"
          "GotFocus|IsVisibleChanged")
missing_h = []
for f in xamls:
    txt = f.read_text(encoding="utf-8", errors="replace")
    cs = f.with_suffix(".xaml.cs")
    body = cs.read_text(encoding="utf-8", errors="replace") if cs.exists() else ""
    for m in re.finditer(r'\b(?:' + EVENTS + r')\s*=\s*"([A-Za-z_][A-Za-z0-9_]*)"', txt):
        name = m.group(1)
        if re.search(r'\b' + re.escape(name) + r'\s*\(', body) is None:
            missing_h.append((f.relative_to(ROOT), txt[:m.start()].count("\n") + 1, name))


def report(title, rows, fmt):
    print(f"=== {title} ===")
    print("ninguno" if not rows else "\n".join(fmt(r) for r in rows))


report("Referencias a recursos sin definición", missing,
       lambda r: f"{r[0]}:{r[1]}  {r[2]} -> {r[3]}")
report("Storyboard.TargetName sin x:Name", tn_missing, lambda r: f"{r[0]}:{r[1]}  target={r[2]}")
report("x:Class sin code-behind", bad_class, lambda r: f"{r[0]}  {r[1]}  ({r[2]})")
report("Handlers de evento sin método", missing_h, lambda r: f"{r[0]}:{r[1]}  handler={r[2]}")

total = len(missing) + len(tn_missing) + len(bad_class) + len(missing_h)
print(f"\nXAML: {len(xamls)} · problemas: {total}")
sys.exit(1 if total else 0)
