using System.Windows;
using System.Windows.Media;

namespace BuscadorVideos.Services;

public enum AppTheme
{
    Dark,
    Light
}

/// <summary>
/// Motor algorítmico de temas basado en una paleta de 4 colores base:
/// 1) Dark    (Fondo oscuro profundo / Texto en modo claro)
/// 2) Accent  (Acento primario de marca / Botones principales / Bordes destacados)
/// 3) Neutral (Gris/Plata secundario para bordes, subtítulos y sombras 8-bit)
/// 4) Light   (Blanco/Niebla para textos principales en modo oscuro y fondo en modo claro)
///
/// Para cambiar toda la aplicación a otra paleta de 4 colores en el futuro,
/// basta con actualizar las 4 constantes Palette*Hex (o ejecutar `python tools/apply_palette.py`).
/// </summary>
public static class AppThemeService
{
    // ==================== PALETA BASE DE 4 COLORES (CHASCLIP) ====================
    public const string PaletteDarkHex = "#0E0E0F";    // Dark Void
    public const string PaletteAccentHex = "#390007";  // Dark Maroon
    public const string PaletteNeutralHex = "#9BA0AB"; // Shining Knight
    public const string PaletteLightHex = "#F0F4F5";   // Maui Mist

    public static AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public static void ApplyTheme(AppTheme theme)
    {
        ApplyPalette(PaletteDarkHex, PaletteAccentHex, PaletteNeutralHex, PaletteLightHex, theme);
    }

    /// <summary>
    /// Algoritmo central que deriva todos los pinceles de la interfaz (modo Oscuro y modo Blanco)
    /// a partir de únicamente 4 colores base garantizando contraste y legibilidad total.
    /// </summary>
    public static void ApplyPalette(
        string darkHex,
        string accentHex,
        string neutralHex,
        string lightHex,
        AppTheme theme)
    {
        var app = Application.Current;
        if (app == null) return;

        CurrentTheme = theme;

        Color cDark = ParseColor(darkHex, Color.FromRgb(14, 14, 15));
        Color cAccent = ParseColor(accentHex, Color.FromRgb(57, 0, 7));
        Color cNeutral = ParseColor(neutralHex, Color.FromRgb(155, 160, 171));
        Color cLight = ParseColor(lightHex, Color.FromRgb(240, 244, 245));

        Color bg, headerBg, sidebar, surface, cardBg, subtle;
        Color border, cardBorder;
        Color textPrimary, textSecondary, textMuted;
        Color accent, accentHover, accentPressed, accentFg, accentBorder, accentText;
        Color btnBg, btnHoverBg, btnFg, btnBorder;
        Color inputBg, inputFg, inputBorder;
        Color overlayBg, splashBg, scrollThumb, scrollThumbHover;

        bool accentIsDark = RelativeLuminance(cAccent) < 0.28;
        accentFg = accentIsDark ? cLight : cDark;

        if (theme == AppTheme.Light)
        {
            // ==================== DERIVACIÓN ALGORÍTMICA: MODO BLANCO ====================
            bg = cLight;
            headerBg = Lerp(cLight, cNeutral, 0.20);
            sidebar = Lerp(cLight, cNeutral, 0.14);
            surface = Lerp(cLight, Colors.White, 0.70);
            cardBg = Colors.White;
            subtle = Lerp(cLight, cNeutral, 0.22);

            border = Lerp(cLight, cNeutral, 0.58);
            cardBorder = Lerp(cLight, cNeutral, 0.72);

            textPrimary = cDark;
            textSecondary = Lerp(cDark, cNeutral, 0.34);
            textMuted = Lerp(cDark, cNeutral, 0.50);

            accent = cAccent;
            accentHover = Lerp(cAccent, cLight, 0.18);
            accentPressed = Lerp(cAccent, cDark, 0.28);
            accentBorder = Lerp(cAccent, cDark, 0.15);
            accentText = accentIsDark ? cAccent : Lerp(cAccent, cDark, 0.45);

            btnBg = Lerp(cLight, cNeutral, 0.25);
            btnHoverBg = Lerp(cLight, cNeutral, 0.42);
            btnFg = cDark;
            btnBorder = Lerp(cLight, cNeutral, 0.68);

            inputBg = Colors.White;
            inputFg = cDark;
            inputBorder = Lerp(cLight, cNeutral, 0.65);

            overlayBg = WithAlpha(cDark, 175);
            splashBg = cLight;
            scrollThumb = Lerp(cLight, cNeutral, 0.75);
            scrollThumbHover = Lerp(cDark, cNeutral, 0.45);
        }
        else
        {
            // ==================== DERIVACIÓN ALGORÍTMICA: MODO OSCURO (PREDETERMINADO) ====================
            bg = cDark;
            headerBg = Lerp(cDark, cAccent, 0.42);
            sidebar = Lerp(cDark, cNeutral, 0.06);
            surface = Lerp(cDark, cNeutral, 0.09);
            cardBg = Lerp(cDark, cNeutral, 0.12);
            subtle = Lerp(cDark, cAccent, 0.32);

            border = Lerp(cDark, cNeutral, 0.28);
            cardBorder = Lerp(cDark, cNeutral, 0.38);

            textPrimary = cLight;
            textSecondary = cNeutral;
            textMuted = Lerp(cNeutral, cDark, 0.22);

            accent = cAccent;
            accentHover = Lerp(cAccent, cLight, 0.16);
            accentPressed = Lerp(cAccent, cDark, 0.30);

            // Si el acento es oscuro (como Dark Maroon #390007), iluminar bordes y textos de acento
            // mezclándolos con Maui Mist / Shining Knight para que ningún borde ni texto se pierda jamás
            accentBorder = accentIsDark ? Lerp(cAccent, cNeutral, 0.55) : cAccent;
            accentText = accentIsDark ? Lerp(cAccent, cLight, 0.65) : cAccent;

            btnBg = Lerp(cDark, cNeutral, 0.14);
            btnHoverBg = Lerp(cAccent, cDark, 0.22);
            btnFg = cLight;
            btnBorder = Lerp(cDark, cNeutral, 0.34);

            inputBg = Lerp(cDark, cNeutral, 0.05);
            inputFg = cLight;
            inputBorder = Lerp(cDark, cNeutral, 0.32);

            overlayBg = WithAlpha(cDark, 224);
            splashBg = cDark;
            scrollThumb = Lerp(cDark, cNeutral, 0.36);
            scrollThumbHover = cNeutral;
        }

        SetBrush(app, "AppBackgroundBrush", bg);
        SetBrush(app, "AppHeaderBackgroundBrush", headerBg);
        SetBrush(app, "AppSidebarBrush", sidebar);
        SetBrush(app, "AppSurfaceBrush", surface);
        SetBrush(app, "AppCardBackgroundBrush", cardBg);
        SetBrush(app, "AppSubtleBrush", subtle);

        SetBrush(app, "AppBorderBrush", border);
        SetBrush(app, "AppCardBorderBrush", cardBorder);

        SetBrush(app, "AppTextPrimaryBrush", textPrimary);
        SetBrush(app, "AppTextSecondaryBrush", textSecondary);
        SetBrush(app, "AppTextMutedBrush", textMuted);

        SetBrush(app, "AppAccentBrush", accent);
        SetBrush(app, "AppAccentHoverBrush", accentHover);
        SetBrush(app, "AppAccentPressedBrush", accentPressed);
        SetBrush(app, "AppAccentForegroundBrush", accentFg);
        SetBrush(app, "AppAccentBorderBrush", accentBorder);
        SetBrush(app, "AppAccentTextBrush", accentText);
        app.Resources["AppGlowColor"] = accentIsDark ? Color.FromRgb(163, 0, 24) : Color.FromRgb(220, 38, 38);

        SetBrush(app, "AppButtonBackgroundBrush", btnBg);
        SetBrush(app, "AppButtonHoverBackgroundBrush", btnHoverBg);
        SetBrush(app, "AppButtonForegroundBrush", btnFg);
        SetBrush(app, "AppButtonBorderBrush", btnBorder);

        SetBrush(app, "AppInputBackgroundBrush", inputBg);
        SetBrush(app, "AppInputForegroundBrush", inputFg);
        SetBrush(app, "AppInputBorderBrush", inputBorder);

        SetBrush(app, "AppOverlayBackgroundBrush", overlayBg);
        SetBrush(app, "AppSplashBackgroundBrush", splashBg);
        SetBrush(app, "AppScrollThumbBrush", scrollThumb);
        SetBrush(app, "AppScrollThumbHoverBrush", scrollThumbHover);

        // Colores base puros expuestos como recursos por si algún componente los consulta
        SetBrush(app, "PaletteDarkBrush", cDark);
        SetBrush(app, "PaletteAccentBrush", cAccent);
        SetBrush(app, "PaletteNeutralBrush", cNeutral);
        SetBrush(app, "PaletteLightBrush", cLight);
    }

    private static void SetBrush(Application app, string key, Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        app.Resources[key] = brush;
    }

    public static Color ParseColor(string hex, Color fallback)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(hex) && ColorConverter.ConvertFromString(hex) is Color c)
                return c;
        }
        catch { }
        return fallback;
    }

    public static Color Lerp(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        byte r = (byte)Math.Round(a.R + (b.R - a.R) * t);
        byte g = (byte)Math.Round(a.G + (b.G - a.G) * t);
        byte bl = (byte)Math.Round(a.B + (b.B - a.B) * t);
        byte al = (byte)Math.Round(a.A + (b.A - a.A) * t);
        return Color.FromArgb(al, r, g, bl);
    }

    public static Color WithAlpha(Color c, byte alpha)
        => Color.FromArgb(alpha, c.R, c.G, c.B);

    public static double RelativeLuminance(Color c)
    {
        static double Channel(byte v)
        {
            double s = v / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }
}
