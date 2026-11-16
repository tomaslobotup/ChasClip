namespace BuscadorVideos.Models;

/// <summary>
/// Representa un atajo / acceso rápido personalizable en el buscador web.
/// </summary>
public class BrowserShortcut
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string IconEmoji { get; set; } = "🌐";
    public string BadgeHex { get; set; } = "#390007";
    public bool IsCustom { get; set; } = false;
}
