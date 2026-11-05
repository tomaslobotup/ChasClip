namespace BuscadorVideos.Models;

/// <summary>
/// Configuración persistente de la aplicación (bloqueador de anuncios, panel lateral, calidad preferida y migración de predeterminados).
/// </summary>
public class AppSettings
{
    public bool IsAdBlockerEnabled { get; set; } = true;
    public bool IsSidebarCollapsed { get; set; } = false;
    public int PreferredQualityHeight { get; set; } = 0; // 0 = Automática (Mejor disponible)
    public bool DefaultsSeededV2 { get; set; } = false;
    public int GridColumns { get; set; } = 3;
    public string Theme { get; set; } = "Dark"; // "Dark", "Light", "Custom"
    public string CustomAccentColor { get; set; } = "#7C6BFF";
    public bool IsIncognitoMode { get; set; } = false;
    public string DefaultDownloadDirectory { get; set; } = string.Empty;
    public bool DownloadPlaylistsComplete { get; set; } = true;
    public bool IsAdultContentEnabled { get; set; } = false;
    public string Language { get; set; } = "es"; // "es", "en"
}
