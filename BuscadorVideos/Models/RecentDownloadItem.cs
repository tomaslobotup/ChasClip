namespace BuscadorVideos.Models;

/// <summary>
/// Representa una descarga completada en la sesión actual.
/// </summary>
public class RecentDownloadItem
{
    public string FileName { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string FormatType { get; set; } = "MP4"; // "MP4" o "MP3"
    public string SourceUrl { get; set; } = string.Empty;
    public string Timestamp { get; set; } = string.Empty;
}
