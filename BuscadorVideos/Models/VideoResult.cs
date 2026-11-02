namespace BuscadorVideos.Models;

/// <summary>
/// Representa un video encontrado con su portada, preview al pasar el mouse y título en su idioma original.
/// </summary>
public class VideoResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Nombre/descripción exacta del video tal cual está en su idioma original.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Canal o autor del video (si está disponible).
    /// </summary>
    public string Author { get; set; } = string.Empty;

    /// <summary>
    /// Duración formateada (ej. "05:32").
    /// </summary>
    public string Duration { get; set; } = string.Empty;

    /// <summary>
    /// URL de la imagen de portada (thumbnail estático).
    /// </summary>
    public string ThumbnailUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL de vista previa animada en formato WebP o GIF (ej. movingThumbnail de YouTube o dynamicCover de TikTok).
    /// </summary>
    public string PreviewWebpUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL de clip de video MP4 silencioso para vista previa al pasar el mouse (ej. Bing Video vtUrl para TikTok y otras webs).
    /// </summary>
    public string PreviewMp4Url { get; set; } = string.Empty;

    /// <summary>
    /// URL de tira de fotogramas vertical (ej. Dailymotion filmstrip).
    /// </summary>
    public string FilmstripUrl { get; set; } = string.Empty;

    /// <summary>
    /// Lista de fotogramas de respaldo del video (inicio, mitad, final) para animación al pasar el mouse.
    /// </summary>
    public List<string> FallbackFrameUrls { get; set; } = [];

    /// <summary>
    /// Enlace directo al video en su página original.
    /// </summary>
    public string VideoPageUrl { get; set; } = string.Empty;

    /// <summary>
    /// URL optimizada para reproducir el video dentro del reproductor integrado WebView2.
    /// </summary>
    public string PlayerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Identificador de la página guardada que originó este resultado.
    /// </summary>
    public string SourceSiteId { get; set; } = string.Empty;

    /// <summary>
    /// Nombre visible de la página origen (ej. "YouTube", "tiktok.com").
    /// </summary>
    public string SourceSiteName { get; set; } = string.Empty;

    /// <summary>
    /// Dominio normalizado de la página origen.
    /// </summary>
    public string SourceDomain { get; set; } = string.Empty;

    /// <summary>
    /// Color distintivo de la plataforma en formato hexadecimal.
    /// </summary>
    public string AccentHex => SavedSite.GetAccentColorForDomain(SourceDomain);

    public string SubtitleText =>
        string.IsNullOrWhiteSpace(Author)
            ? SourceDomain
            : $"{Author}  •  {SourceDomain}";
}
