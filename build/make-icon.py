"""生成 src/GlideShell/Assets/GlideShell.ico（多尺寸）与预览 PNG。需要 Pillow：pip install pillow

图形：蓝紫渐变圆角方块上三道向上滑动的白色指迹（三指上滑 = 总览），指迹自下而上由淡到实。
每个尺寸都从 1024 像素的母版缩小（LANCZOS），16 像素时仍能看出三道竖条。
"""
import os
from PIL import Image, ImageDraw

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT = os.path.join(ROOT, "src", "GlideShell", "Assets")
SIZES = [16, 20, 24, 32, 40, 48, 64, 128, 256]
M = 1024  # 母版尺寸


def master() -> Image.Image:
    img = Image.new("RGBA", (M, M), (0, 0, 0, 0))

    # 背景：左上 #3B82F6 → 右下 #7C3AED 的对角渐变，圆角方块
    grad = Image.new("RGBA", (M, M))
    px = grad.load()
    a, b = (59, 130, 246), (124, 58, 237)
    for y in range(M):
        for x in range(M):
            t = (x + y) / (2 * (M - 1))
            px[x, y] = tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3)) + (255,)
    mask = Image.new("L", (M, M), 0)
    pad = M * 0.06
    ImageDraw.Draw(mask).rounded_rectangle([pad, pad, M - pad, M - pad], radius=M * 0.22, fill=255)
    img.paste(grad, (0, 0), mask)

    # 三道指迹：圆头竖条，下段半透明（拖尾），上端实心圆点（指尖）
    draw = ImageDraw.Draw(img)
    w = M * 0.13
    gap = M * 0.20
    top, bottom = M * 0.24, M * 0.74
    for i in (-1, 0, 1):
        cx = M / 2 + i * gap
        y0 = top + (M * 0.05 if i != 0 else 0)  # 中指略高
        trail = Image.new("RGBA", (M, M), (0, 0, 0, 0))
        ImageDraw.Draw(trail).rounded_rectangle([cx - w / 2, y0, cx + w / 2, bottom], radius=w / 2, fill=(255, 255, 255, 110))
        img.alpha_composite(trail)
        draw.ellipse([cx - w / 2, y0, cx + w / 2, y0 + w], fill=(255, 255, 255, 255))
    return img


def main() -> None:
    os.makedirs(OUT, exist_ok=True)
    big = master()
    images = [big.resize((s, s), Image.LANCZOS) for s in SIZES]
    ico = os.path.join(OUT, "GlideShell.ico")
    images[-1].save(ico, format="ICO", sizes=[(s, s) for s in SIZES], append_images=images[:-1])
    preview = Image.new("RGBA", (sum(SIZES) + 10 * len(SIZES), 256), (32, 32, 32, 255))
    x = 0
    for im in images:
        preview.alpha_composite(im, (x, 256 - im.height))
        x += im.width + 10
    preview.save(os.path.join(ROOT, "artifacts", "icon-preview.png"))
    print(ico)


if __name__ == "__main__":
    main()
