import pymupdf, sys
src = sys.argv[1]
d = pymupdf.open(src)
out = []
for i, p in enumerate(d):
    out.append("\n===== PAGE %d/%d =====\n" % (i + 1, len(d)))
    out.append(p.get_text())
sys.stdout.reconfigure(encoding="utf-8")
sys.stdout.write("".join(out))
