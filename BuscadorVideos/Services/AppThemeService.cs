using System.Windows;
using System.Windows.Media;

namespace BuscadorVideos.Services;

public enum AppTheme
{
    Dark,
    Light,
    Custom
}

/// <summary>
/// Gestiona la aplicación dinámica de temas en tiempo real (Oscuro, Blanco y Personalizable con paleta de acentos).
/// </summary>
public static class AppThemeService
{
    public static readonly string[] CustomAccentPresets =
    [
        "#7C6BFF", // Morado eléctrico (predeterminado)
        "#06B6D4", // Cian neón
        "#10B981", // Verde esmeralda
        "#F43F5E", // Rosa neón / Rubí
        "#F59E0B", // Ámbar dorado
        "#3B82F6", // Azul zafiro
        "#EC4899"  // Magenta
    ];

    public static void ApplyTheme(AppTheme theme, string? customAccentHex = null, string? customBgHex = null)
    {
        var app = Application.Current;
        if (app == null) return;

        Color bg, surface, sidebar, cardBg, border, cardBorder, textPrimary, textSecondary, accent, accentHover;
        Color btnBg, btnFg, btnBorder, inputBg, inputFg, inputBorder, subtle;

        switch (theme)
        {
            case AppTheme.Light:
                bg = (Color)ColorConverter.ConvertFromString("#F1F5F9");
                surface = (Color)ColorConverter.ConvertFromString("#FFFFFF");
                sidebar = (Color)ColorConverter.ConvertFromString("#F8FAFC");
                cardBg = (Color)ColorConverter.ConvertFromString("#FFFFFF");
                border = (Color)ColorConverter.ConvertFromString("#E2E8F0");
                cardBorder = (Color)ColorConverter.ConvertFromString("#E2E8F0");
                textPrimary = (Color)ColorConverter.ConvertFromString("#0F172A");
                textSecondary = (Color)ColorConverter.ConvertFromString("#475569");
                accent = (Color)ColorConverter.ConvertFromString("#4F46E5");
                accentHover = (Color)ColorConverter.ConvertFromString("#6366F1");

                btnBg = (Color)ColorConverter.ConvertFromString("#F1F5F9");
                btnFg = (Color)ColorConverter.ConvertFromString("#1E293B");
                btnBorder = (Color)ColorConverter.ConvertFromString("#CBD5E1");

                inputBg = (Color)ColorConverter.ConvertFromString("#FFFFFF");
                inputFg = (Color)ColorConverter.ConvertFromString("#0F172A");
                inputBorder = (Color)ColorConverter.ConvertFromString("#CBD5E1");
                subtle = (Color)ColorConverter.ConvertFromString("#F8FAFC");
                break;

            case AppTheme.Custom:
                var hex = !string.IsNullOrWhiteSpace(customAccentHex) ? customAccentHex : "#06B6D4";
                accent = (Color)ColorConverter.ConvertFromString(hex);
                accentHover = Color.FromArgb(235, (byte)Math.Min(255, accent.R + 25), (byte)Math.Min(255, accent.G + 25), (byte)Math.Min(255, accent.B + 25));

                bg = (Color)ColorConverter.ConvertFromString("#0C101A");
                surface = (Color)ColorConverter.ConvertFromString("#131929");
                sidebar = (Color)ColorConverter.ConvertFromString("#101524");
                cardBg = (Color)ColorConverter.ConvertFromString("#151D30");
                border = (Color)ColorConverter.ConvertFromString("#222C44");
                cardBorder = (Color)ColorConverter.ConvertFromString("#293552");
                textPrimary = (Color)ColorConverter.ConvertFromString("#F4F7FC");
                textSecondary = (Color)ColorConverter.ConvertFromString("#8897BA");

                btnBg = (Color)ColorConverter.ConvertFromString("#1A2234");
                btnFg = (Color)ColorConverter.ConvertFromString("#DCE1F0");
                btnBorder = (Color)ColorConverter.ConvertFromString("#2D3852");

                inputBg = (Color)ColorConverter.ConvertFromString("#131826");
                inputFg = (Color)ColorConverter.ConvertFromString("#F3F5FB");
                inputBorder = (Color)ColorConverter.ConvertFromString("#2C364F");
                subtle = (Color)ColorConverter.ConvertFromString("#161E30");
                break;

            case AppTheme.Dark:
            default:
                bg = (Color)ColorConverter.ConvertFromString("#0B0E17");
                surface = (Color)ColorConverter.ConvertFromString("#121625");
                sidebar = (Color)ColorConverter.ConvertFromString("#111522");
                cardBg = (Color)ColorConverter.ConvertFromString("#151927");
                border = (Color)ColorConverter.ConvertFromString("#20263B");
                cardBorder = (Color)ColorConverter.ConvertFromString("#252B40");
                textPrimary = (Color)ColorConverter.ConvertFromString("#FFFFFF");
                textSecondary = (Color)ColorConverter.ConvertFromString("#8F9BB9");
                accent = (Color)ColorConverter.ConvertFromString("#6D5DFB");
                accentHover = (Color)ColorConverter.ConvertFromString("#8274FC");

                btnBg = (Color)ColorConverter.ConvertFromString("#1C2133");
                btnFg = (Color)ColorConverter.ConvertFromString("#DCE1F0");
                btnBorder = (Color)ColorConverter.ConvertFromString("#2D3550");

                inputBg = (Color)ColorConverter.ConvertFromString("#131621");
                inputFg = (Color)ColorConverter.ConvertFromString("#F3F5FB");
                inputBorder = (Color)ColorConverter.ConvertFromString("#2A3046");
                subtle = (Color)ColorConverter.ConvertFromString("#1A2035");
                break;
        }

        app.Resources["AppBackgroundBrush"] = new SolidColorBrush(bg);
        app.Resources["AppSurfaceBrush"] = new SolidColorBrush(surface);
        app.Resources["AppSidebarBrush"] = new SolidColorBrush(sidebar);
        app.Resources["AppCardBackgroundBrush"] = new SolidColorBrush(cardBg);
        app.Resources["AppBorderBrush"] = new SolidColorBrush(border);
        app.Resources["AppCardBorderBrush"] = new SolidColorBrush(cardBorder);
        app.Resources["AppTextPrimaryBrush"] = new SolidColorBrush(textPrimary);
        app.Resources["AppTextSecondaryBrush"] = new SolidColorBrush(textSecondary);
        app.Resources["AppAccentBrush"] = new SolidColorBrush(accent);
        app.Resources["AppAccentHoverBrush"] = new SolidColorBrush(accentHover);

        app.Resources["AppButtonBackgroundBrush"] = new SolidColorBrush(btnBg);
        app.Resources["AppButtonForegroundBrush"] = new SolidColorBrush(btnFg);
        app.Resources["AppButtonBorderBrush"] = new SolidColorBrush(btnBorder);
        app.Resources["AppInputBackgroundBrush"] = new SolidColorBrush(inputBg);
        app.Resources["AppInputForegroundBrush"] = new SolidColorBrush(inputFg);
        app.Resources["AppInputBorderBrush"] = new SolidColorBrush(inputBorder);
        app.Resources["AppSubtleBrush"] = new SolidColorBrush(subtle);
    }
}
