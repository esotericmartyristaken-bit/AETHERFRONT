"""Convert the supplied APF artwork into client textures and a multi-size ICO."""
import hashlib
import json
from pathlib import Path
import sys
from PIL import Image

source, output = map(Path, sys.argv[1:])
original = source.read_bytes()
image = Image.open(source).convert('RGBA')
background = image.getpixel((0, 0))

def canvas(size, padding=0):
    result = Image.new('RGBA', size, background)
    copy = image.copy()
    copy.thumbnail((size[0]-2*padding, size[1]-2*padding), Image.Resampling.LANCZOS)
    # Allow scaling up while preserving the source aspect ratio.
    ratio = min((size[0]-2*padding)/image.width, (size[1]-2*padding)/image.height)
    copy = image.resize((max(1, round(image.width*ratio)), max(1, round(image.height*ratio))), Image.Resampling.LANCZOS)
    result.alpha_composite(copy, ((size[0]-copy.width)//2, (size[1]-copy.height)//2))
    return result

files = {
    'GameData/LunaMultiplayer/Button/LMPButton.png': canvas((64, 64), 2),
    # Keep the legacy flag path so existing craft/save flag references still work.
    'GameData/LunaMultiplayer/Flags/lmp.png': canvas((256, 160), 8).convert('RGB'),
}
for relative, artwork in files.items():
    path = output/relative
    path.parent.mkdir(parents=True, exist_ok=True)
    artwork.save(path)
folder = output/'GameData/LunaMultiplayer/Presentation'
folder.mkdir(parents=True, exist_ok=True)
(folder/'APF.png').write_bytes(original)
sizes = [(s, s) for s in (16, 24, 32, 48, 64, 128, 256)]
canvas((256, 256)).save(folder/'APF.ico', format='ICO', sizes=sizes)
with Image.open(folder/'APF.ico') as icon:
    assert icon.ico.sizes() == set(sizes)
print(json.dumps({'source_sha256': hashlib.sha256(original).hexdigest(), 'source_dimensions': image.size,
                  'icon_sizes': sizes, 'redrawn_or_generated_artwork': False}, indent=2))
