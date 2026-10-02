# /// script
# dependencies = [
#   "pillow>=10.0.0",
#   "numpy>=1.20.0",
# ]
# ///

"""
Brokencca Icon Generator
========================
Generates application icons for Brokencca in the visual style of Brokenithm
and based on the physical WACCA arcade cabinet (assets/cabinet.jpg).

Outputs:
  - assets/brokencca.png: 1024x1024 master icon (white background, identical style/canvas to brokenithm.png)
  - assets/brokencca_transparent.png: 1024x1024 icon with transparent background
  - assets/brokencca.ico: Multi-resolution Windows application icon (16x16 to 256x256)
  - ios/Brokencca/Assets.xcassets/AppIcon.appiconset/: Full Apple iOS AppIcon asset catalog
"""

import os
import sys
import json
import math
import argparse
import numpy as np
from PIL import Image, ImageDraw

# Master Canvas Configuration
SUPERSAMPLE = 4
CANVAS_SIZE = 1024
INTERNAL_SIZE = CANVAS_SIZE * SUPERSAMPLE
CX, CY = INTERNAL_SIZE // 2, INTERNAL_SIZE // 2

# Palette from Brokenithm & WACCA Cabinet
COLOR_WHITE_BG     = (255, 255, 255, 255)
COLOR_BORDER       = (0, 0, 0, 255)
COLOR_DARK_SCREEN  = (46, 46, 46, 255)        # Exact Brokenithm upper dark shade (#2e2e2e)
COLOR_WHITE_LINE   = (234, 234, 234, 255)     # Exact Brokenithm air sensor white line (#eaeaea)

# Cabinet Outer Rim
COLOR_BEZEL        = (244, 246, 252, 255)     # Illuminated cool-white cabinet bezel
COLOR_BEZEL_ACCENT = (220, 226, 240, 255)     # Subtle inner bezel contour

# WACCA Illuminated LED Arch (matching assets/cabient.jpg)
C_PINK             = (255, 42, 133, 255)     # Hot Pink / Magenta (#ff2a85)
C_WHITE            = (255, 255, 255, 255)     # Crisp White (#ffffff)
C_YELLOW           = (255, 214, 10, 255)      # Electric Yellow (#ffd60a)
C_CYAN             = (0, 229, 255, 255)       # Electric Cyan (#00e5ff)
C_BLUE             = (37, 99, 235, 255)       # Royal Arcade Blue (#2563eb)
C_VIOLET           = (139, 44, 215, 255)      # Arcade Violet (#8b2cd7)

# WACCA Lower Sensor Panels (illuminated midnight tones from cabient.jpg)
C_INDIGO_MID       = (55, 48, 108, 255)       # Illuminated Indigo (#37306c)
C_INDIGO_DARK      = (38, 32, 78, 255)        # Dark Indigo (#26204e)
C_DEEP_SLATE       = (25, 22, 50, 255)        # Midnight Slate (#191632)


def draw_annular_sector(draw, center, r_in, r_out, a_start_deg, a_end_deg, fill, outline=None):
    """Draw a smooth annular sector (pie slice of a ring) using polar polygon vertices."""
    cx, cy = center
    num_pts = max(16, int(abs(a_end_deg - a_start_deg) * 3))
    angles_out = np.linspace(math.radians(a_start_deg), math.radians(a_end_deg), num_pts)
    angles_in = np.linspace(math.radians(a_end_deg), math.radians(a_start_deg), num_pts)
    pts = []
    for a in angles_out:
        pts.append((cx + r_out * math.cos(a), cy + r_out * math.sin(a)))
    for a in angles_in:
        pts.append((cx + r_in * math.cos(a), cy + r_in * math.sin(a)))
    draw.polygon(pts, fill=fill, outline=outline)


def render_brokencca_icon(transparent=False, with_chevron=False):
    """Renders the master 1024x1024 Brokencca icon at 4x supersampling and downsamples with Lanczos."""
    bg_color = (0, 0, 0, 0) if transparent else COLOR_WHITE_BG
    img = Image.new("RGBA", (INTERNAL_SIZE, INTERNAL_SIZE), bg_color)
    draw = ImageDraw.Draw(img)

    # Dimensional Hierarchy (scaled to 4096px canvas)
    r_outer     = int(395 * SUPERSAMPLE)
    r_bezel_in  = int(352 * SUPERSAMPLE)
    r_touch_out = int(342 * SUPERSAMPLE)
    r_touch_mid = int(268 * SUPERSAMPLE)
    r_touch_in  = int(195 * SUPERSAMPLE)
    r_screen    = int(184 * SUPERSAMPLE)
    border_w    = int(26 * SUPERSAMPLE)

    # 1. Outer Cabinet Bezel (illuminated circular rim)
    draw.ellipse([CX - r_outer, CY - r_outer, CX + r_outer, CY + r_outer], fill=COLOR_BEZEL)
    r_bezel_mid = int(374 * SUPERSAMPLE)
    draw.ellipse([CX - r_bezel_mid, CY - r_bezel_mid, CX + r_bezel_mid, CY + r_bezel_mid],
                 outline=COLOR_BEZEL_ACCENT, width=int(5 * SUPERSAMPLE))
    draw.ellipse([CX - r_outer, CY - r_outer, CX + r_outer, CY + r_outer],
                 outline=COLOR_BORDER, width=border_w)
    draw.ellipse([CX - r_bezel_in, CY - r_bezel_in, CX + r_bezel_in, CY + r_bezel_in],
                 outline=COLOR_BORDER, width=int(8 * SUPERSAMPLE))

    # 2. Touch Panels (16 sectors x 2 tracks = 32 segments, matching WACCA's annular touch field)
    n_sec = 16
    d_sec = 360.0 / n_sec
    start_offset = 270.0 - (d_sec / 2.0)  # Perfectly aligned with apex at 12 o'clock

    # Outer track palette by distance from top (0 = apex, 8 = nadir)
    palette_outer = [
        C_PINK,         # 0: Top apex (Pink)
        C_PINK,         # 1: Pink
        C_CYAN,         # 2: Cyan
        C_CYAN,         # 3: Cyan
        C_BLUE,         # 4: Blue
        C_VIOLET,       # 5: Violet
        C_INDIGO_MID,   # 6: Indigo
        C_INDIGO_DARK,  # 7: Dark indigo
        C_DEEP_SLATE,   # 8: Bottom center
    ]

    # Inner track palette (creates the stepped equalizer arch from cabient.jpg)
    palette_inner = [
        C_WHITE,        # 0: Top apex inner (White highlight)
        C_YELLOW,       # 1: Electric Yellow
        C_PINK,         # 2: Pink
        C_CYAN,         # 3: Cyan
        C_VIOLET,       # 4: Violet
        C_INDIGO_MID,   # 5: Indigo
        C_INDIGO_DARK,  # 6: Dark indigo
        C_DEEP_SLATE,   # 7: Slate
        C_DEEP_SLATE,   # 8: Slate
    ]

    # Draw all sectors
    for s in range(n_sec):
        dist = s if s <= 8 else 16 - s
        col_out = palette_outer[dist]
        col_in = palette_inner[dist]

        a_s = start_offset + s * d_sec
        a_e = a_s + d_sec
        draw_annular_sector(draw, (CX, CY), r_touch_in, r_touch_mid, a_s, a_e, fill=col_in)
        draw_annular_sector(draw, (CX, CY), r_touch_mid, r_touch_out, a_s, a_e, fill=col_out)

    # Sector divider lines (crisp, solid black lines)
    sep_w = int(4 * SUPERSAMPLE)
    for s in range(n_sec):
        angle = math.radians(start_offset + s * d_sec)
        x1 = CX + r_touch_in * math.cos(angle)
        y1 = CY + r_touch_in * math.sin(angle)
        x2 = CX + r_touch_out * math.cos(angle)
        y2 = CY + r_touch_out * math.sin(angle)
        draw.line([(x1, y1), (x2, y2)], fill=COLOR_BORDER, width=sep_w)

    # Track boundary circle
    draw.ellipse([CX - r_touch_mid, CY - r_touch_mid, CX + r_touch_mid, CY + r_touch_mid],
                 outline=COLOR_BORDER, width=int(5 * SUPERSAMPLE))

    # Touch ring inner boundary
    draw.ellipse([CX - r_touch_in, CY - r_touch_in, CX + r_touch_in, CY + r_touch_in],
                 outline=COLOR_BORDER, width=int(9 * SUPERSAMPLE))

    # 3. Center Screen: Deep Dark Carbon (#2e2e2e)
    draw.ellipse([CX - r_screen, CY - r_screen, CX + r_screen, CY + r_screen], fill=COLOR_DARK_SCREEN)

    if with_chevron:
        # Perspective tunnel rings & WACCA Snap Arrow
        for r in [int(r_screen * 0.42), int(r_screen * 0.68), int(r_screen * 0.90)]:
            draw.ellipse([CX - r, CY - r, CX + r, CY + r], outline=COLOR_WHITE_LINE, width=int(10 * SUPERSAMPLE))
        for ang in [45, 135, 225, 315]:
            rad_a = math.radians(ang)
            x_in = CX + int(r_screen * 0.22) * math.cos(rad_a)
            y_in = CY + int(r_screen * 0.22) * math.sin(rad_a)
            x_out = CX + int(r_screen * 0.90) * math.cos(rad_a)
            y_out = CY + int(r_screen * 0.90) * math.sin(rad_a)
            draw.line([(x_in, y_in), (x_out, y_out)], fill=COLOR_WHITE_LINE, width=int(6 * SUPERSAMPLE))
        chev_pts = [
            (CX, CY - int(45 * SUPERSAMPLE)),
            (CX + int(38 * SUPERSAMPLE), CY + int(25 * SUPERSAMPLE)),
            (CX, CY + int(8 * SUPERSAMPLE)),
            (CX - int(38 * SUPERSAMPLE), CY + int(25 * SUPERSAMPLE)),
        ]
        draw.polygon(chev_pts, fill=C_PINK, outline=COLOR_BORDER)
        chev_in = [
            (CX, CY - int(32 * SUPERSAMPLE)),
            (CX + int(24 * SUPERSAMPLE), CY + int(18 * SUPERSAMPLE)),
            (CX, CY + int(7 * SUPERSAMPLE)),
            (CX - int(24 * SUPERSAMPLE), CY + int(18 * SUPERSAMPLE)),
        ]
        draw.polygon(chev_in, fill=C_WHITE)
    else:
        # Polar Concentric Rings: Direct counterpart to Brokenithm's Cartesian Air sensor lines
        ring_radii = [
            int(r_screen * 0.28),
            int(r_screen * 0.50),
            int(r_screen * 0.72),
            int(r_screen * 0.92),
        ]
        for r in ring_radii:
            draw.ellipse([CX - r, CY - r, CX + r, CY + r], outline=COLOR_WHITE_LINE, width=int(11 * SUPERSAMPLE))
        r_core = int(24 * SUPERSAMPLE)
        draw.ellipse([CX - r_core, CY - r_core, CX + r_core, CY + r_core], fill=COLOR_WHITE_LINE)
        draw.ellipse([CX - r_core, CY - r_core, CX + r_core, CY + r_core], outline=COLOR_BORDER, width=int(5 * SUPERSAMPLE))

    # Inner screen border
    draw.ellipse([CX - r_screen, CY - r_screen, CX + r_screen, CY + r_screen], outline=COLOR_BORDER, width=int(14 * SUPERSAMPLE))

    # High-quality Lanczos downsampling to 1024x1024
    return img.resize((CANVAS_SIZE, CANVAS_SIZE), Image.Resampling.LANCZOS)


def generate_all_icons(repo_root=None, style='tunnel'):
    """Generates all icon files for assets, iOS, and Windows."""
    if repo_root is None:
        repo_root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

    assets_dir = os.path.join(repo_root, "assets")
    ios_asset_catalog = os.path.join(repo_root, "ios", "Brokencca", "Assets.xcassets", "AppIcon.appiconset")
    os.makedirs(assets_dir, exist_ok=True)
    os.makedirs(ios_asset_catalog, exist_ok=True)

    with_chevron = (style == 'chevron')

    print(f"Generating Brokencca icons (style: {style})...")

    # 1. Master PNG (White background, matching brokenithm.png)
    img_white = render_brokencca_icon(transparent=False, with_chevron=with_chevron)
    path_white = os.path.join(assets_dir, "brokencca.png")
    img_white.save(path_white, format="PNG")
    print(f"  -> Saved master PNG: {path_white}")

    # 2. Transparent Master PNG
    img_trans = render_brokencca_icon(transparent=True, with_chevron=with_chevron)
    path_trans = os.path.join(assets_dir, "brokencca_transparent.png")
    img_trans.save(path_trans, format="PNG")
    print(f"  -> Saved transparent PNG: {path_trans}")

    # 3. Windows Multi-Resolution ICO
    # Uses transparent background so Windows desktop / taskbar renders the circular controller cleanly
    path_ico = os.path.join(assets_dir, "brokencca.ico")
    ico_sizes = [(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (24, 24), (16, 16)]
    img_trans.save(path_ico, format="ICO", sizes=ico_sizes)
    print(f"  -> Saved Windows ICO: {path_ico} (sizes: {', '.join(f'{s[0]}x{s[1]}' for s in ico_sizes)})")

    # 4. iOS AppIcon Asset Catalog
    # Standard iOS AppIcon specifications
    ios_icons = [
        {"size": 1024, "filename": "AppIcon-1024.png", "idiom": "universal", "platform": "ios"},
        {"size": 180,  "filename": "AppIcon-180.png",  "idiom": "iphone", "scale": "3x"},
        {"size": 120,  "filename": "AppIcon-120.png",  "idiom": "iphone", "scale": "2x"},
        {"size": 167,  "filename": "AppIcon-167.png",  "idiom": "ipad",   "scale": "2x"},
        {"size": 152,  "filename": "AppIcon-152.png",  "idiom": "ipad",   "scale": "2x"},
        {"size": 76,   "filename": "AppIcon-76.png",   "idiom": "ipad",   "scale": "1x"},
    ]

    contents = {
        "images": [],
        "info": {
            "author": "xcode",
            "version": 1
        }
    }

    for item in ios_icons:
        size = item["size"]
        fn = item["filename"]
        out_path = os.path.join(ios_asset_catalog, fn)
        # Resize from master white background image
        resized = img_white.resize((size, size), Image.Resampling.LANCZOS)
        # Ensure RGB mode (no alpha) for standard iOS AppIcon compatibility
        resized.convert("RGB").save(out_path, format="PNG")
        print(f"  -> Saved iOS AppIcon ({size}x{size}): {out_path}")

        entry = {
            "filename": fn,
            "idiom": item["idiom"],
            "size": f"{size}x{size}"
        }
        if "scale" in item:
            entry["scale"] = item["scale"]
        if "platform" in item:
            entry["platform"] = item["platform"]
        contents["images"].append(entry)

    contents_path = os.path.join(ios_asset_catalog, "Contents.json")
    with open(contents_path, "w", encoding="utf-8") as f:
        json.dump(contents, f, indent=2)
    print(f"  -> Saved iOS Contents.json: {contents_path}")

    print("\nIcon generation complete!")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Brokencca Icon Generator")
    parser.add_argument("--style", choices=["tunnel", "chevron"], default="tunnel",
                        help="Visual center style: 'tunnel' (Brokenithm polar concentric rings) or 'chevron' (WACCA snap target)")
    parser.add_argument("--repo-root", default=None, help="Root directory of Brokencca repository")
    args = parser.parse_args()

    generate_all_icons(repo_root=args.repo_root, style=args.style)
