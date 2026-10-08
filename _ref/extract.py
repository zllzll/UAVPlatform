import pymupdf, os, re
out = r"D:\DeepseekHarness\UAVPlatform\_ref"
jobs = [
 (r"E:\Projects\文档\无人机+RTK\NSR雷达系统通信协议V1.2.9.pdf", "NSR_radar_V1.2.9.txt"),
 (r"E:\Projects\文档\UCM211\UCM221雷达通用协议_V1.1.0-2.pdf", "UCM221_V1.1.0-2.txt"),
]
def clean(s):
    s = s.replace("\x00", "")
    s = re.sub(r"[\x01-\x08\x0b\x0c\x0e-\x1f\x7f]", "", s)
    s = re.sub(r"[ \t]+\n", "\n", s)
    s = re.sub(r"\n{3,}", "\n\n", s)
    return s
for src, name in jobs:
    d = pymupdf.open(src)
    parts = []
    for i, p in enumerate(d):
        parts.append(f"\n===== PAGE {i+1}/{len(d)} =====\n" + p.get_text())
    txt = clean("".join(parts))
    dst = os.path.join(out, name)
    with open(dst, "w", encoding="utf-8", newline="\n") as f:
        f.write(txt)
    print(name, len(txt), "nullbytes:", txt.count("\x00"))
