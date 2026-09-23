import base64
from pathlib import Path
from PIL import Image

root = Path(r"C:\Users\muham\OneDrive\Desktop\Point of Sale\src\EdgeRetails.Desktop\Assets")
source_b64 = root / "edge_source.b64"
source_png = root / "edge_source.png"
source_png.write_bytes(base64.b64decode(source_b64.read_text()))

image = Image.open(source_png).convert("RGBA")
pixels = image.load()
for y in range(image.height):
    for x in range(image.width):
        r, g, b, a = pixels[x, y]
        if r > 242 and g > 242 and b > 242:
            pixels[x, y] = (255, 255, 255, 0)

bbox = image.getbbox()
if bbox is None:
    raise RuntimeError("Logo mark was not detected.")
mark = image.crop(bbox)
mark.thumbnail((176, 56), Image.Resampling.LANCZOS)
logo = Image.new("RGBA", (176, 56), (255, 255, 255, 0))
logo.alpha_composite(
    mark,
    ((176 - mark.width) // 2, (56 - mark.height) // 2))
logo.save(root / "EdgeLogo.png")

icon = Image.new("RGBA", (256, 256), (255, 255, 255, 255))
icon_mark = mark.copy()
icon_mark.thumbnail((210, 90), Image.Resampling.LANCZOS)
icon.alpha_composite(
    icon_mark,
    ((256 - icon_mark.width) // 2, (256 - icon_mark.height) // 2))
icon.save(root / "EdgeAppIcon.png")
icon.save(
    root / "EdgeRetails.ico",
    sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])

source_b64.unlink(missing_ok=True)
source_png.unlink(missing_ok=True)
print("BRAND_ASSETS_READY")
