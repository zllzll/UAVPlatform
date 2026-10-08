import pymupdf
for src in [r"E:\Projects\文档\无人机+RTK\NSR雷达系统通信协议V1.2.9.pdf",
            r"E:\Projects\文档\UCM211\UCM221雷达通用协议_V1.1.0-2.pdf"]:
    d = pymupdf.open(src)
    print("="*70)
    print(src.split("\\")[-1])
    print(" pages:", len(d), "encrypted:", d.is_encrypted, "needs_pass:", d.needs_pass, "perm:", d.permissions)
    print(" metadata:", d.metadata)
    t = d[0].get_text()
    print(" page1 text repr[:400]:", repr(t[:400]))
    # fonts on page 1
    try:
        fonts = d[0].get_fonts()
        print(" page1 fonts:", [(f[3], f[4], f[5]) for f in fonts][:10])
    except Exception as e:
        print(" font err", e)
    # links / toc
    print(" toc:", d.get_toc()[:8])
