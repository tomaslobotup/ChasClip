using System.Text.Json.Serialization;
using BuscadorVideos.Models;

namespace BuscadorVideos.Models;

/// <summary>
/// Representa una entrada en el historial de reproducción de videos.
/// </summary>
public class PlaybackHistoryItem
{
    public string VideoPageUrl { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Author { get; set; } = string.Empty;
    public string Duration { get; set; } = string.Empty;
    public string ThumbnailUrl { get; set; } = string.Empty;
    public string PreviewWebpUrl { get; set; } = string.Empty;
    public string PreviewMp4Url { get; set; } = string.Empty;
    public string PlayerUrl { get; set; } = string.Empty;
    public string SourceSiteId { get; set; } = string.Empty;
    public string SourceSiteName { get; set; } = string.Empty;
    public string SourceDomain { get; set; } = string.Empty;
    public DateTime PlayedAtUtc { get; set; } = DateTime.UtcNow;

    public static PlaybackHistoryItem FromVideoResult(VideoResult video)
    {
        return new PlaybackHistoryItem
        {
            VideoPageUrl = video.VideoPageUrl,
            Title = video.Title,
            Author = video.Author,
            Duration = video.Duration,
            ThumbnailUrl = video.ThumbnailUrl,
            PreviewWebpUrl = video.PreviewWebpUrl,
            PreviewMp4Url = video.PreviewMp4Url,
            PlayerUrl = video.PlayerUrl,
            SourceSiteId = video.SourceSiteId,
            SourceSiteName = video.SourceSiteName,
            SourceDomain = video.SourceDomain,
            PlayedAtUtc = DateTime.UtcNow
        };
    }

    public VideoResult ToVideoResult()
    {
        return new VideoResult
        {
            Title = Title,
            Author = Author,
            Duration = Duration,
            ThumbnailUrl = ThumbnailUrl,
            PreviewWebpUrl = PreviewWebpUrl,
            PreviewMp4Url = PreviewMp4Url,
            VideoPageUrl = VideoPageUrl,
            PlayerUrl = PlayerUrl,
            SourceSiteId = SourceSiteId,
            SourceSiteName = SourceSiteName,
            SourceDomain = SourceDomain
        };
    }
}
