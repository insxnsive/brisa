"""Regenerate Brisa's original icon. Requires Pillow; not needed to build the app."""
from pathlib import Path
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'src/Brisa/Assets'
OUT.mkdir(parents=True, exist_ok=True)
SCALE = 4
image = Image.new('RGBA', (256 * SCALE, 256 * SCALE))
draw = ImageDraw.Draw(image)
def box(values):
    return tuple(int(v * SCALE) for v in values)
draw.rounded_rectangle(box((8, 8, 248, 248)), radius=54*SCALE, fill='#125B50')
# Two open, flowing strokes, kept legible at notification-area sizes.
for y, end in [(87, 190), (155, 166)]:
    draw.line([box((65, y+18)), box((103, y+18)), box((130, y-9)), box((end, y-9))], fill='#F4FBF6', width=19*SCALE, joint='curve')
    for x, yy in [(65, y+18), (end, y-9)]:
        draw.ellipse(box((x-9.5, yy-9.5, x+9.5, yy+9.5)), fill='#F4FBF6')
image = image.resize((256, 256), Image.Resampling.LANCZOS)
image.save(OUT/'Brisa.png')
image.save(OUT/'Brisa.ico', sizes=[(16,16),(24,24),(32,32),(48,48),(64,64),(128,128),(256,256)])
print(OUT/'Brisa.ico')
