import pymupdf, os
def find(root, needle):
    for dp, dn, fn in os.walk(root):
        for f in fn:
            if needle in f and f.lower().endswith(".pdf"):
                return os.path.join(dp, f)
    return None
src = find(r"E:\Projects", "NSR")
print("found:", src.encode("unicode_escape").decode("ascii") if src else None)
dst = r"D:\DeepseekHarness\UAVPlatform\_ref\t1.txt"
d = pymupdf.open(src)
t = d[0].get_text()
print("inmem_len:", len(t))
print("inmem_head_esc:", t[:60].encode("unicode_escape").decode("ascii"))
with open(dst, "w", encoding="utf-8", newline="") as f:
    f.write(t)
rb = open(dst, "rb").read()
print("disk_len:", len(rb))
print("disk_head_hex:", rb[:60].hex(" "))
rb2 = open(dst, "r", encoding="utf-8").read()
print("readback_equal:", rb2 == t)
print("readback_head_esc:", rb2[:60].encode("unicode_escape").decode("ascii"))
