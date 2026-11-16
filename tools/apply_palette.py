#!/usr/bin/env python3
"""
Algoritmo generador de paleta de 4 colores y recursos 8-bit para ChasClip.

Uso para cambiar toda la interfaz, el ícono (.ico) y el texto 8-bit (logo.png) a una nueva paleta:
    python tools/apply_palette.py --dark "#0E0E0F" --accent "#390007" --neutral "#9BA0AB" --light "#F0F4F5"
"""

import argparse
import os
import re
import numpy as np
from PIL import Image


def hex_to_rgba(hex_str: str, alpha: int = 255) -> tuple[int, int, int, int]:
    h = hex_str.strip().lstrip("#")
    if len(h) == 3:
        h = "".join(c * 2 for c in h)
    return (int(h[0:2], 16), int(h[2:4], 16), int(h[4:6], 16), alpha)


def render_indexed_map(
    idx_map: np.ndarray,
    c_dark: tuple[int, int, int, int],
    c_accent: tuple[int, int, int, int],
    c_neutral: tuple[int, int, int, int],
    c_light: tuple[int, int, int, int],
) -> Image.Image:
    h, w = idx_map.shape
    out = np.zeros((h, w, 4), dtype=np.uint8)
    out[idx_map == 0] = c_dark
    out[idx_map == 1] = c_accent
    out[idx_map == 2] = c_neutral
    out[idx_map == 3] = c_light
    out[idx_map == 255] = (0, 0, 0, 0)
    return Image.fromarray(out, mode="RGBA")


def generate_assets(dark_hex: str, accent_hex: str, neutral_hex: str, light_hex: str, root_dir: str) -> None:
    templates_path = os.path.join(root_dir, "tools", "chasclip_templates.npz")
    assets_dir = os.path.join(root_dir, "BuscadorVideos", "Assets")
    os.makedirs(assets_dir, exist_ok=True)

    data = np.load(templates_path)
    icon_map = data["icon"]
    word_map = data["wordmark"]

    c_dark = hex_to_rgba(dark_hex, 255)
    c_accent = hex_to_rgba(accent_hex, 255)
    c_neutral = hex_to_rgba(neutral_hex, 255)
    c_light = hex_to_rgba(light_hex, 255)

    # 1) Renderizar ícono 8-bit cuadrado (centrado con margen limpio en grilla lógica)
    raw_icon = render_indexed_map(icon_map, c_dark, c_accent, c_neutral, c_light)
    ih, iw = icon_map.shape
    side = max(ih, iw) + 2
    square_icon = Image.new("RGBA", (side, side), (0, 0, 0, 0))
    square_icon.paste(raw_icon, ((side - iw) // 2, (side - ih) // 2), raw_icon)

    icon_256 = square_icon.resize((256, 256), Image.Resampling.NEAREST)
    icon_png_path = os.path.join(assets_dir, "icon.png")
    icon_256.save(icon_png_path, format="PNG")

    # Guardar multi-resolución .ico (16, 32, 48, 64, 128, 256) con escalado NEAREST (8-bit puro)
    ico_path = os.path.join(assets_dir, "logo.ico")
    ico_sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    icon_256.save(ico_path, format="ICO", sizes=ico_sizes)

    # 2) Renderizar texto 8-bit "ChasClip" (wordmark) en Assets/logo.png con factor entero 8x
    raw_word = render_indexed_map(word_map, c_dark, c_accent, c_neutral, c_light)
    wh, ww = word_map.shape
    pad_x, pad_y = 2, 1
    word_canvas = Image.new("RGBA", (ww + pad_x * 2, wh + pad_y * 2), (0, 0, 0, 0))
    word_canvas.paste(raw_word, (pad_x, pad_y), raw_word)
    word_hi = word_canvas.resize((word_canvas.width * 8, word_canvas.height * 8), Image.Resampling.NEAREST)

    logo_png_path = os.path.join(assets_dir, "logo.png")
    word_hi.save(logo_png_path, format="PNG")

    print(f"[OK] Icono 8-bit guardado en: {ico_path} y {icon_png_path}")
    print(f"[OK] Texto 8-bit 'ChasClip' guardado en: {logo_png_path}")


def lerp_rgb(c1: tuple[int, int, int, int], c2: tuple[int, int, int, int], t: float) -> str:
    t = max(0.0, min(1.0, t))
    r = int(round(c1[0] + (c2[0] - c1[0]) * t))
    g = int(round(c1[1] + (c2[1] - c1[1]) * t))
    b = int(round(c1[2] + (c2[2] - c1[2]) * t))
    return f"#{r:02X}{g:02X}{b:02X}"


def rel_lum(c: tuple[int, int, int, int]) -> float:
    return (0.2126 * c[0] + 0.7152 * c[1] + 0.0722 * c[2]) / 255.0


def update_csharp_palette(dark_hex: str, accent_hex: str, neutral_hex: str, light_hex: str, root_dir: str) -> None:
    theme_service_path = os.path.join(root_dir, "BuscadorVideos", "Services", "AppThemeService.cs")
    if os.path.exists(theme_service_path):
        with open(theme_service_path, "r", encoding="utf-8") as f:
            content = f.read()

        content = re.sub(
            r'(public const string PaletteDarkHex\s*=\s*")#[0-9A-Fa-f]{6}(";)',
            rf'\g<1>{dark_hex.upper()}\g<2>',
            content,
        )
        content = re.sub(
            r'(public const string PaletteAccentHex\s*=\s*")#[0-9A-Fa-f]{6}(";)',
            rf'\g<1>{accent_hex.upper()}\g<2>',
            content,
        )
        content = re.sub(
            r'(public const string PaletteNeutralHex\s*=\s*")#[0-9A-Fa-f]{6}(";)',
            rf'\g<1>{neutral_hex.upper()}\g<2>',
            content,
        )
        content = re.sub(
            r'(public const string PaletteLightHex\s*=\s*")#[0-9A-Fa-f]{6}(";)',
            rf'\g<1>{light_hex.upper()}\g<2>',
            content,
        )

        with open(theme_service_path, "w", encoding="utf-8") as f:
            f.write(content)

        print(f"[OK] Paleta base actualizada en: {theme_service_path}")

    # Actualizar también los valores iniciales de App.xaml (Modo Oscuro predeterminado)
    app_xaml_path = os.path.join(root_dir, "BuscadorVideos", "App.xaml")
    if os.path.exists(app_xaml_path):
        c_dark = hex_to_rgba(dark_hex)
        c_accent = hex_to_rgba(accent_hex)
        c_neutral = hex_to_rgba(neutral_hex)
        c_light = hex_to_rgba(light_hex)
        accent_is_dark = rel_lum(c_accent) < 0.18

        brushes = {
            "PaletteDarkBrush": dark_hex.upper(),
            "PaletteAccentBrush": accent_hex.upper(),
            "PaletteNeutralBrush": neutral_hex.upper(),
            "PaletteLightBrush": light_hex.upper(),
            "AppBackgroundBrush": dark_hex.upper(),
            "AppHeaderBackgroundBrush": lerp_rgb(c_dark, c_accent, 0.42),
            "AppSidebarBrush": lerp_rgb(c_dark, c_neutral, 0.06),
            "AppSurfaceBrush": lerp_rgb(c_dark, c_neutral, 0.09),
            "AppCardBackgroundBrush": lerp_rgb(c_dark, c_neutral, 0.12),
            "AppSubtleBrush": lerp_rgb(c_dark, c_accent, 0.32),
            "AppBorderBrush": lerp_rgb(c_dark, c_neutral, 0.28),
            "AppCardBorderBrush": lerp_rgb(c_dark, c_neutral, 0.36),
            "AppTextPrimaryBrush": light_hex.upper(),
            "AppTextSecondaryBrush": neutral_hex.upper(),
            "AppTextMutedBrush": lerp_rgb(c_dark, c_neutral, 0.78),
            "AppAccentBrush": accent_hex.upper(),
            "AppAccentHoverBrush": lerp_rgb(c_accent, c_neutral, 0.24) if accent_is_dark else lerp_rgb(c_accent, c_light, 0.18),
            "AppAccentPressedBrush": lerp_rgb(c_accent, c_dark, 0.30),
            "AppAccentForegroundBrush": light_hex.upper() if rel_lum(c_accent) < 0.45 else dark_hex.upper(),
            "AppAccentBorderBrush": lerp_rgb(c_accent, c_neutral, 0.55) if accent_is_dark else lerp_rgb(c_accent, c_light, 0.25),
            "AppAccentTextBrush": lerp_rgb(c_accent, c_light, 0.65) if accent_is_dark else accent_hex.upper(),
            "AppButtonBackgroundBrush": lerp_rgb(c_dark, c_neutral, 0.14),
            "AppButtonHoverBackgroundBrush": lerp_rgb(c_dark, c_accent, 0.78),
            "AppButtonForegroundBrush": light_hex.upper(),
            "AppButtonBorderBrush": lerp_rgb(c_dark, c_neutral, 0.34),
            "AppInputBackgroundBrush": lerp_rgb(c_dark, c_neutral, 0.05),
            "AppInputForegroundBrush": light_hex.upper(),
            "AppInputBorderBrush": lerp_rgb(c_dark, c_neutral, 0.32),
            "AppOverlayBackgroundBrush": f"#E0{dark_hex.upper().lstrip('#')}",
            "AppSplashBackgroundBrush": dark_hex.upper(),
            "AppScrollThumbBrush": lerp_rgb(c_dark, c_neutral, 0.36),
            "AppScrollThumbHoverBrush": neutral_hex.upper(),
        }

        with open(app_xaml_path, "r", encoding="utf-8") as f:
            ax = f.read()
        for key, val in brushes.items():
            ax = re.sub(
                rf'(<SolidColorBrush\s+x:Key="{key}"\s+Color=")#[0-9A-Fa-f]{{6,8}}("/>)',
                rf'\g<1>{val}\g<2>',
                ax,
            )
        with open(app_xaml_path, "w", encoding="utf-8") as f:
            f.write(ax)
        print(f"[OK] Recursos XAML predeterminados actualizados en: {app_xaml_path}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Aplica una paleta de 4 colores a ChasClip (UI + Icono + Texto 8-bit).")
    parser.add_argument("--dark", default="#0E0E0F", help="Color base oscuro (ej. Dark Void #0E0E0F)")
    parser.add_argument("--accent", default="#390007", help="Color acento primario (ej. Dark Maroon #390007)")
    parser.add_argument("--neutral", default="#9BA0AB", help="Color secundario neutro (ej. Shining Knight #9BA0AB)")
    parser.add_argument("--light", default="#F0F4F5", help="Color claro principal (ej. Maui Mist #F0F4F5)")
    args = parser.parse_args()

    root_dir = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    generate_assets(args.dark, args.accent, args.neutral, args.light, root_dir)
    update_csharp_palette(args.dark, args.accent, args.neutral, args.light, root_dir)


if __name__ == "__main__":
    main()
