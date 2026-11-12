"""Adds method names to the call targets in a disasm.ps1 listing.
Usage: python -I annotate.py listing.txt [out.txt]"""
import bisect, os, re, sys

rows = []
with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "methodmap.tsv"), encoding="utf-8") as f:
    for line in f:
        p = line.rstrip("\n").split("\t")
        if len(p) >= 3 and p[0].isdigit():
            rows.append((int(p[0]), int(p[1]), p[2]))
rows.sort()
starts = [r[0] for r in rows]
exact = {}
for r in rows:
    exact.setdefault(r[0], r[2])

def name_of(va):
    rva = va - 0x180000000
    if rva in exact:
        return exact[rva]
    i = bisect.bisect_right(starts, rva) - 1
    if i >= 0 and rva < rows[i][0] + max(rows[i][1], 1):
        return f"{rows[i][2]}+0x{rva - rows[i][0]:X}"
    return ""

out = []
for line in open(sys.argv[1], encoding="utf-8", errors="replace"):
    line = line.rstrip("\n")
    m = re.search(r"call 0*([0-9A-F]+)h", line)
    if m:
        n = name_of(int(m.group(1), 16))
        if n:
            line += "    ; " + n
    out.append(line)
text = "\n".join(out) + "\n"
if len(sys.argv) > 2:
    open(sys.argv[2], "w", encoding="utf-8").write(text)
else:
    sys.stdout.write(text)
