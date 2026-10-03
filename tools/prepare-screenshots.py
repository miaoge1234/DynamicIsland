"""把演示模式生成的三张截图整理成 README 用的图。

- 收起态那张画布是 560x292，胶囊只占顶部一条，需要裁掉空白
- 展开态两张本来就是满的，直接搬过去
- 顺便做一次透明像素的边界检测，把裁切框算准
"""

import shutil
from pathlib import Path

from PIL import Image

DIAG = Path.home() / "AppData" / "Roaming" / "DynamicIsland" / "diag"
OUT = Path(r"D:\deepseek\DynamicIsland\docs")
OUT.mkdir(parents=True, exist_ok=True)


def trim_alpha(img: Image.Image, padding: int = 2) -> Image.Image:
    """按非透明像素的包围盒裁掉周围的空白。"""
    bbox = img.getchannel("A").getbbox()
    if bbox is None:
        return img

    left, top, right, bottom = bbox
    left = max(0, left - padding)
    top = max(0, top - padding)
    right = min(img.width, right + padding)
    bottom = min(img.height, bottom + padding)
    return img.crop((left, top, right, bottom))


def main() -> None:
    jobs = [
        ("island-demo-collapsed.png", "screenshot-collapsed.png", True),
        ("island-demo-expanded.png", "screenshot-expanded.png", False),
        ("island-demo-todos.png", "screenshot-todos.png", False),
    ]

    for src_name, dst_name, do_trim in jobs:
        src = DIAG / src_name
        if not src.exists():
            print(f"跳过（源文件不存在）: {src}")
            continue

        dst = OUT / dst_name

        if do_trim:
            with Image.open(src) as img:
                img = img.convert("RGBA")
                print(f"{src_name}: 原始 {img.width}x{img.height}", end="")
                trimmed = trim_alpha(img, padding=3)
                print(f" -> 裁切后 {trimmed.width}x{trimmed.height}")
                trimmed.save(dst, "PNG", optimize=True)
        else:
            shutil.copyfile(src, dst)
            with Image.open(dst) as img:
                print(f"{src_name}: 直接使用 {img.width}x{img.height}")

        size_kb = dst.stat().st_size / 1024
        print(f"  -> {dst}  ({size_kb:.1f} KB)")


if __name__ == "__main__":
    main()
