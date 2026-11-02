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
}
