"""Regenerates every app icon asset from docs/logo.png (python build/make-icons.py; needs Pillow).

Tiles, taskbar / Start target sizes (plated and unplated), Store logo, splash, lock screen, the in-app
wordmark and splash images, and AppIcon.ico for the exe and window.
"""
import os
from PIL import Image

root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
assets = os.path.join(root, 'src', 'MoonMovie', 'Assets')
src = Image.open(os.path.join(root, 'docs', 'logo.png')).convert('RGBA')
src = src.crop(src.getbbox())


def place(w, h, fill):
    """The logo centred on a transparent w x h canvas, its larger side `fill` of the shorter canvas side."""
    canvas = Image.new('RGBA', (w, h), (0, 0, 0, 0))
    scale = int(min(w, h) * fill) / max(src.size)
    logo = src.resize((max(1, round(src.width * scale)), max(1, round(src.height * scale))), Image.LANCZOS)
    canvas.alpha_composite(logo, ((w - logo.width) // 2, (h - logo.height) // 2))
    return canvas


for old in os.listdir(assets):
    if old.endswith('.png') and old.startswith(('Square', 'Wide', 'StoreLogo', 'SplashScreen', 'LockScreenLogo')):
        os.remove(os.path.join(assets, old))

for s, f in {100: 1, 125: 1.25, 150: 1.5, 200: 2, 400: 4}.items():
    place(round(44 * f), round(44 * f), 0.86).save(os.path.join(assets, f'Square44x44Logo.scale-{s}.png'))
    place(round(150 * f), round(150 * f), 0.62).save(os.path.join(assets, f'Square150x150Logo.scale-{s}.png'))
    place(round(310 * f), round(150 * f), 0.66).save(os.path.join(assets, f'Wide310x150Logo.scale-{s}.png'))
    place(round(50 * f), round(50 * f), 0.9).save(os.path.join(assets, f'StoreLogo.scale-{s}.png'))
    place(round(620 * f), round(300 * f), 0.5).save(os.path.join(assets, f'SplashScreen.scale-{s}.png'))
    place(round(24 * f), round(24 * f), 0.9).save(os.path.join(assets, f'LockScreenLogo.scale-{s}.png'))

for t in (16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256):
    img = place(t, t, 0.94)
    for suffix in ('', '_altform-unplated', '_altform-lightunplated'):
        img.save(os.path.join(assets, f'Square44x44Logo.targetsize-{t}{suffix}.png'))

place(96, 96, 1.0).save(os.path.join(assets, 'AppLogo.png'))        # title-bar wordmark
place(320, 320, 1.0).save(os.path.join(assets, 'AppLogoLarge.png'))  # startup splash
place(256, 256, 0.94).save(os.path.join(assets, 'AppIcon.ico'),
                           sizes=[(16, 16), (20, 20), (24, 24), (32, 32), (40, 40), (48, 48), (64, 64), (256, 256)])
print('icons written to', assets)
