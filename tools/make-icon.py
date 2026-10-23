"""生成 MovieWall / LUMEN 应用图标（多尺寸 ICO）。

设计语言跟界面一致：纯黑底、只用明度层次，不引入彩色。
符号是「镜头光圈」——一个白环加中心小圆，16px 下也能认出是镜头。

用法： python tools/make-icon.py
输出： src/MovieWall/Assets/app.ico  （16/24/32/48/64/128/256 共 7 档）
"""

import os
import struct
from PIL import Image, ImageDraw

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(HERE)
OUT_DIR = os.path.join(ROOT, "src", "MovieWall", "Assets")
OUT_ICO = os.path.join(OUT_DIR, "app.ico")
OUT_PREVIEW = os.path.join(ROOT, "_shots", "icon-preview.png")

SIZES = [16, 24, 32, 48, 64, 128, 256]

BG = (10, 10, 12, 255)          # #0A0A0C 画布
EDGE = (36, 36, 40, 255)        # #242428 极细描边
INK = (243, 243, 244, 255)      # #F3F3F4 主色


def render(size: int) -> Image.Image:
    """按目标尺寸单独绘制，避免 256→16 缩小后线条糊成一团。"""
    ss = 8                                    # 超采样倍率
    s = size * ss
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)

    # 圆角底板：Windows 图标习惯留一点边，圆角约 22%
    pad = round(s * 0.04)
    radius = round(s * 0.22)
    d.rounded_rectangle([pad, pad, s - pad - 1, s - pad - 1],
                        radius=radius, fill=BG,
                        outline=EDGE, width=max(1, round(s * 0.008)))

    cx = cy = s / 2

    # 光圈环：小尺寸时按最小 2px 反推环宽，否则 16px 下只剩一条灰线
    ring_outer = s * 0.30
    ring_w = max(ring_outer * 0.27, 2.0 * ss)
    d.ellipse([cx - ring_outer, cy - ring_outer, cx + ring_outer, cy + ring_outer], fill=INK)
    inner = ring_outer - ring_w
    d.ellipse([cx - inner, cy - inner, cx + inner, cy + inner], fill=BG)

    # 中心瞳孔：小尺寸也要保证 2px 起步
    dot = max(s * 0.075, 2.0 * ss)
    d.ellipse([cx - dot, cy - dot, cx + dot, cy + dot], fill=INK)

    return img.resize((size, size), Image.LANCZOS)


def write_ico(images, path):
    """手写 ICO 容器：每档用 PNG 压缩存（Vista 之后都支持），省得依赖 Pillow 的尺寸推断。"""
    blobs = []
    for im in images:
        tmp = os.path.join(os.environ.get("TEMP", "/tmp"), f"_ico_{im.width}.png")
        im.save(tmp, format="PNG")
        with open(tmp, "rb") as f:
            blobs.append((im.width, im.height, f.read()))
        os.remove(tmp)

    n = len(blobs)
    header = struct.pack("<HHH", 0, 1, n)
    offset = 6 + 16 * n
    entries, data = b"", b""
    for w, h, blob in blobs:
        entries += struct.pack("<BBBBHHII",
                               w if w < 256 else 0,
                               h if h < 256 else 0,
                               0, 0, 1, 32, len(blob), offset)
        data += blob
        offset += len(blob)

    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "wb") as f:
        f.write(header + entries + data)


def main():
    os.makedirs(OUT_DIR, exist_ok=True)
    images = [render(s) for s in SIZES]
    write_ico(images, OUT_ICO)

    print(f"ICO  -> {OUT_ICO}  ({os.path.getsize(OUT_ICO)} 字节, {len(SIZES)} 档)")

    # 预览图：各尺寸并排放在深色底上，方便一眼看小尺寸是否还认得出
    os.makedirs(os.path.dirname(OUT_PREVIEW), exist_ok=True)
    gap = 24
    total_w = sum(s for s in SIZES) + gap * (len(SIZES) - 1) + gap * 2
    total_h = 256 + gap * 2
    sheet = Image.new("RGBA", (total_w, total_h), (6, 6, 7, 255))
    x = gap
    for im in images:
        sheet.alpha_composite(im, (x, (total_h - im.height) // 2))
        x += im.width + gap
    sheet.save(OUT_PREVIEW)
    print(f"预览 -> {OUT_PREVIEW}")


if __name__ == "__main__":
    main()
