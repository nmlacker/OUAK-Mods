"""Finds direct calls (E8 rel32) to the given hex VAs in GameAssembly.dll and names the calling methods.
Usage: python -I xrefs.py 180582BD0 1805833E0"""
import bisect, os, struct, sys

GAME = os.path.normpath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))  # the game folder: ModMenuSource sits inside it
b = open(os.path.join(GAME, "GameAssembly.dll"), "rb").read()
pe = struct.unpack_from("<I", b, 0x3C)[0]
nsec = struct.unpack_from("<H", b, pe + 6)[0]
osz = struct.unpack_from("<H", b, pe + 20)[0]
base = struct.unpack_from("<Q", b, pe + 24 + 24)[0]
sec = pe + 24 + osz
targets = {int(v, 16): v for v in sys.argv[1:]}

rows = []
with open(os.path.join(os.path.dirname(os.path.abspath(__file__)), "methodmap.tsv"), encoding="utf-8") as f:
    for line in f:
        p = line.rstrip("\n").split("\t")
        if len(p) >= 3 and p[0].isdigit():
            rows.append((int(p[0]), int(p[1]), p[2]))
rows.sort()
starts = [r[0] for r in rows]

def name_of(rva):
    i = bisect.bisect_right(starts, rva) - 1
    if i >= 0 and rva < rows[i][0] + rows[i][1]:
        return f"{rows[i][2]} +0x{rva - rows[i][0]:X}"
    return "?"

for s in range(nsec):
    o = sec + 40 * s
    if b[o:o + 8].rstrip(b"\0") not in (b".text", b"il2cpp"):
        continue
    vaddr, rsize, rptr = struct.unpack_from("<III", b, o + 12)[0], struct.unpack_from("<I", b, o + 16)[0], struct.unpack_from("<I", b, o + 20)[0]
    vaddr = struct.unpack_from("<I", b, o + 12)[0]
    end = rptr + rsize - 5
    i = b.find(b"\xE8", rptr, end)
    while i != -1:
        rel = struct.unpack_from("<i", b, i + 1)[0]
        rva = vaddr + (i - rptr)
        dest = base + rva + 5 + rel
        if dest in targets:
            print(f"{targets[dest]} <- {base + rva:X}  {name_of(rva)}")
        i = b.find(b"\xE8", i + 1, end)
