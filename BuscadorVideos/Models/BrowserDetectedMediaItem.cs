namespace BuscadorVideos.Models;

/// <summary>
/// Representa un flujo de video o audio detectado en tiempo real por el sniffer estilo Video DownloadHelper
/// dentro del Buscador Web (incluyendo iframes cross-origin, manifiestos HLS .m3u8 / /m3u8/, DASH y archivos MP4/WebM).
/// </summary>
public class BrowserDetectedMediaItem
{
    public string StreamUrl { get; set; } = string.Empty;
    public string PageUrl { get; set; } = string.Empty;
    public string Title { get; set; } = "Video Detectado";
    public string StreamTypeLabel { get; set; } = "MP4";
    public bool IsHls { get; set; }
    public bool IsDirectMedia { get; set; }
    public string? Referer { get; set; }
    public string? Origin { get; set; }
    public string? Cookies { get; set; }
    public long? ContentLengthBytes { get; set; }
    public DateTime DetectedAt { get; set; } = DateTime.UtcNow;

    public string HostLabel
    {
        get
        {
            if (Uri.TryCreate(StreamUrl, UriKind.Absolute, out var u))
                return u.Host.Replace("www.", "", StringComparison.OrdinalIgnoreCase);
            return "servidor";
        }
    }

    public string SizeBadge
    {
        get
        {
            if (ContentLengthBytes is > 0)
            {
                double mb = ContentLengthBytes.Value / (1024.0 * 1024.0);
                return mb >= 1.0 ? $" · {mb:0.1} MB" : $" · {ContentLengthBytes.Value / 1024.0:0} KB";
            }
            return string.Empty;
        }
    }

    public string DisplaySummary => $"[{StreamTypeLabel}] {Title} ({HostLabel}{SizeBadge})";
}
