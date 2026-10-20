r"""배포 묶음 안의 문서 스크린샷(PNG)을 256색 팔레트로 줄인다. 원본(docs/images)은 건드리지 않는다.
    python -I scripts\optimize-docs-images.py <묶음>\docs\images
"""
import sys
from pathlib import Path
from PIL import Image

folder = Path(sys.argv[1])
before = after = 0
for path in sorted(folder.glob("*.png")):
    size = path.stat().st_size
    with Image.open(path) as im:
        mode = "RGBA" if im.mode in ("RGBA", "LA", "P") and "A" in im.convert("RGBA").getbands() and im.convert("RGBA").getextrema()[3][0] < 255 else "RGB"
        small = im.convert(mode).quantize(256, method=Image.Quantize.FASTOCTREE, dither=Image.Dither.FLOYDSTEINBERG)
    tmp = path.with_suffix(".tmp")
    small.save(tmp, format="PNG", optimize=True)
    if tmp.stat().st_size < size:
        tmp.replace(path)
    else:
        tmp.unlink()
    before += size
    after += path.stat().st_size
print(f"문서 이미지 {before / 1048576:.1f} MB -> {after / 1048576:.1f} MB")
