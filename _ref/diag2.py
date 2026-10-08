import pymupdf, sys, os
src = sys.argv[1]
print("argv_ok:", os.path.exists(src))
d = pymupdf.open(src)
t = d[0].get_text()
print("inmem_len:", len(t))
print("inmem_head_esc:", t[:60].encode("unicode_escape").decode("ascii"))
dst = r"D:\DeepseekHarness\UAVPlatform\_ref\t1.txt"
with open(dst, "w", encoding="utf-8", newline="") as f:
    f.write(t)
rb = open(dst, "rb").read()
print("disk_len:", len(rb))
print("disk_head_hex:", rb[:80].hex(" "))
rb2 = open(dst, "r", encoding="utf-8").read()
print("readback_equal:", rb2 == t)
print("readback_head_esc:", rb2[:60].encode("unicode_escape").decode("ascii"))
