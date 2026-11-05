using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Extrae metadatos y genera tarjetas listas para reproducir o descargar
/// cuando el usuario ingresa un enlace directo (YouTube, TikTok, Instagram, Twitch, Dailymotion o videos web).
/// </summary>
public static class DirectVideoLinkResolver
{
    private static readonly Regex YouTubeRegex = new(
        @"(?:youtube\.com\/(?:watch\?.*v=|shorts\/|embed\/|v\/)|youtu\.be\/)([a-zA-Z0-9_\-]{11})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DailymotionRegex = new(
        @"dailymotion\.com\/(?:video|embed\/video)\/([a-zA-Z0-9]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TwitchVideoRegex = new(
        @"twitch\.tv\/videos\/(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TwitchClipRegex = new(
        @"(?:clips\.twitch\.tv\/|twitch\.tv\/[^\/]+\/clip\/)([a-zA-Z0-9_\-]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex OgTagRegex = new(
        @"<meta\s+[^>]*?(?:property|name)\s*=\s*[""'](og:[a-zA-Z0-9_\-]+|twitter:[a-zA-Z0-9_\-]+)[""'][^>]*?content\s*=\s*[""']([^""']*)[""']|<meta\s+[^>]*?content\s*=\s*[""']([^""']*)[""'][^>]*?(?:property|name)\s*=\s*[""'](og:[a-zA-Z0-9_\-]+|twitter:[a-zA-Z0-9_\-]+)[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HtmlTitleRegex = new(
        @"<title\b[^>]*>([\s\S]*?)<\/title>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public static bool IsSupportedDirectUrl(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return false;
        var s = input.Trim();
        return (s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
               Uri.TryCreate(s, UriKind.Absolute, out _);
    }

    public static async Task<VideoResult?> ResolveLinkAsync(string rawUrl, CancellationToken ct = default)
    {
        if (!IsSupportedDirectUrl(rawUrl)) return null;

        var cleanUrl = rawUrl.Trim();
        var uri = new Uri(cleanUrl);
        var host = uri.Host.ToLowerInvariant();

        // 1) YouTube (videos estándar, Shorts, youtu.be)
        var ytMatch = YouTubeRegex.Match(cleanUrl);
        if (ytMatch.Success)
        {
            var videoId = ytMatch.Groups[1].Value;
            var title = "Video de YouTube";
            var author = "YouTube";
            var thumb = $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg";

            try
            {
                var oembedUrl = $"https://www.youtube.com/oembed?url=https://www.youtube.com/watch?v={videoId}&format=json";
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3.5));
                var json = await HttpHelper.GetUtf8StringWithAgeBypassAsync(oembedUrl, "https://www.youtube.com", cts.Token);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("title", out var titleProp))
                    title = titleProp.GetString() ?? title;
                if (doc.RootElement.TryGetProperty("author_name", out var authorProp))
                    author = authorProp.GetString() ?? author;
                if (doc.RootElement.TryGetProperty("thumbnail_url", out var thumbProp))
                    thumb = thumbProp.GetString() ?? thumb;
            }
            catch
            {
                // Conservar metadatos por defecto del video id
            }

            return new VideoResult
            {
                Title = title,
                Author = author,
                ThumbnailUrl = thumb,
                VideoPageUrl = $"https://www.youtube.com/watch?v={videoId}",
                PlayerUrl = $"https://www.youtube.com/embed/{videoId}?autoplay=1",
                SourceSiteName = "YouTube",
                SourceDomain = "youtube.com"
            };
        }

        // 2) TikTok
        if (host.Contains("tiktok.com"))
        {
            var title = "Video de TikTok";
            var author = "TikTok";
            var thumb = string.Empty;

            try
            {
                var oembedUrl = $"https://www.tiktok.com/oembed?url={Uri.EscapeDataString(cleanUrl)}";
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3.5));
                var json = await HttpHelper.GetUtf8StringWithAgeBypassAsync(oembedUrl, "https://www.tiktok.com", cts.Token);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("title", out var titleProp))
                    title = titleProp.GetString() ?? title;
                if (doc.RootElement.TryGetProperty("author_name", out var authorProp))
                    author = authorProp.GetString() ?? author;
                if (doc.RootElement.TryGetProperty("thumbnail_url", out var thumbProp))
                    thumb = thumbProp.GetString() ?? thumb;
            }
            catch
            {
            }

            return new VideoResult
            {
                Title = title,
                Author = author,
                ThumbnailUrl = thumb,
                VideoPageUrl = cleanUrl,
                PlayerUrl = cleanUrl,
                SourceSiteName = "TikTok",
                SourceDomain = "tiktok.com"
            };
        }

        // 3) Dailymotion
        var dmMatch = DailymotionRegex.Match(cleanUrl);
        if (dmMatch.Success)
        {
            var videoId = dmMatch.Groups[1].Value;
            var title = "Video de Dailymotion";
            var author = "Dailymotion";
            var thumb = $"https://www.dailymotion.com/thumbnail/video/{videoId}";

            try
            {
                var oembedUrl = $"https://www.dailymotion.com/services/oembed?url={Uri.EscapeDataString(cleanUrl)}";
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3.5));
                var json = await HttpHelper.GetUtf8StringWithAgeBypassAsync(oembedUrl, "https://www.dailymotion.com", cts.Token);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("title", out var titleProp))
                    title = titleProp.GetString() ?? title;
                if (doc.RootElement.TryGetProperty("author_name", out var authorProp))
                    author = authorProp.GetString() ?? author;
                if (doc.RootElement.TryGetProperty("thumbnail_url", out var thumbProp))
                    thumb = thumbProp.GetString() ?? thumb;
            }
            catch
            {
            }

            return new VideoResult
            {
                Title = title,
                Author = author,
                ThumbnailUrl = thumb,
                VideoPageUrl = cleanUrl,
                PlayerUrl = $"https://www.dailymotion.com/embed/video/{videoId}?autoplay=1",
                SourceSiteName = "Dailymotion",
                SourceDomain = "dailymotion.com"
            };
        }

        // 4) Twitch
        var twitchVidMatch = TwitchVideoRegex.Match(cleanUrl);
        if (twitchVidMatch.Success)
        {
            var vidId = twitchVidMatch.Groups[1].Value;
            return new VideoResult
            {
                Title = $"Twitch VOD #{vidId}",
                Author = "Twitch",
                ThumbnailUrl = string.Empty,
                VideoPageUrl = cleanUrl,
                PlayerUrl = $"https://player.twitch.tv/?video={vidId}&parent=localhost&autoplay=true",
                SourceSiteName = "Twitch",
                SourceDomain = "twitch.tv"
            };
        }

        var twitchClipMatch = TwitchClipRegex.Match(cleanUrl);
        if (twitchClipMatch.Success)
        {
            var clipId = twitchClipMatch.Groups[1].Value;
            return new VideoResult
            {
                Title = $"Twitch Clip: {clipId}",
                Author = "Twitch",
                ThumbnailUrl = string.Empty,
                VideoPageUrl = cleanUrl,
                PlayerUrl = $"https://clips.twitch.tv/embed?clip={clipId}&parent=localhost&autoplay=true",
                SourceSiteName = "Twitch",
                SourceDomain = "twitch.tv"
            };
        }

        // 5) Archivos directos MP4 / WebM
        if (cleanUrl.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            cleanUrl.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
        {
            var filename = Path.GetFileNameWithoutExtension(uri.LocalPath);
            if (string.IsNullOrWhiteSpace(filename)) filename = "Video MP4";
            return new VideoResult
            {
                Title = filename,
                Author = host,
                ThumbnailUrl = string.Empty,
                VideoPageUrl = cleanUrl,
                PlayerUrl = cleanUrl,
                SourceSiteName = host,
                SourceDomain = host
            };
        }

        // 6) Instagram o cualquier página web general: inspeccionar OpenGraph / HTML Title
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(4));
            var html = await HttpHelper.GetUtf8StringWithAgeBypassAsync(cleanUrl, $"{uri.Scheme}://{host}", cts.Token);

            var title = string.Empty;
            var thumb = string.Empty;

            foreach (Match m in OgTagRegex.Matches(html))
            {
                var tag = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[4].Value;
                var val = m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;

                if (string.IsNullOrWhiteSpace(val)) continue;

                if (string.IsNullOrWhiteSpace(title) && (tag.Equals("og:title", StringComparison.OrdinalIgnoreCase) || tag.Equals("twitter:title", StringComparison.OrdinalIgnoreCase)))
                {
                    title = System.Net.WebUtility.HtmlDecode(val).Trim();
                }
                else if (string.IsNullOrWhiteSpace(thumb) && (tag.Equals("og:image", StringComparison.OrdinalIgnoreCase) || tag.Equals("twitter:image", StringComparison.OrdinalIgnoreCase)))
                {
                    thumb = val.Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                var tm = HtmlTitleRegex.Match(html);
                if (tm.Success)
                {
                    title = System.Net.WebUtility.HtmlDecode(tm.Groups[1].Value).Trim();
                }
            }

            if (string.IsNullOrWhiteSpace(title))
            {
                title = $"Video en {host}";
            }

            var siteName = host.StartsWith("www.") ? host.Substring(4) : host;
            if (host.Contains("instagram.com")) siteName = "Instagram";

            return new VideoResult
            {
                Title = title,
                Author = siteName,
                ThumbnailUrl = thumb,
                VideoPageUrl = cleanUrl,
                PlayerUrl = cleanUrl,
                SourceSiteName = siteName,
                SourceDomain = host
            };
        }
        catch
        {
            // Fallback general
            var siteName = host.StartsWith("www.") ? host.Substring(4) : host;
            return new VideoResult
            {
                Title = $"Video: {uri.AbsolutePath.Trim('/')}",
                Author = siteName,
                ThumbnailUrl = string.Empty,
                VideoPageUrl = cleanUrl,
                PlayerUrl = cleanUrl,
                SourceSiteName = siteName,
                SourceDomain = host
            };
        }
    }
}
