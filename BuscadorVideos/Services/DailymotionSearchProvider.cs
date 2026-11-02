using System.Net;
using System.Text.Json;
using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Proveedor nativo para Dailymotion usando su API pública REST (con títulos originales, portadas y tira de fotogramas de preview).
/// </summary>
public class DailymotionSearchProvider
{
    public async Task<List<VideoResult>> SearchAsync(SavedSite site, string query, CancellationToken ct = default)
    {
        var results = new List<VideoResult>();
        var url =
            $"https://api.dailymotion.com/videos?search={Uri.EscapeDataString(query)}&family_filter=false&limit=36&fields=id,title,url,thumbnail_480_url,filmstrip_60_url,duration,owner.screenname";

        var json = await HttpHelper.GetUtf8StringAsync(url, "https://www.dailymotion.com/", ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("list", out var list) || list.ValueKind != JsonValueKind.Array)
            return results;

        foreach (var item in list.EnumerateArray())
        {
            var id = item.TryGetProperty("id", out var idP) ? idP.GetString() : null;
            var title = item.TryGetProperty("title", out var tP) ? tP.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                continue;

            var pageUrl = item.TryGetProperty("url", out var uP) && !string.IsNullOrWhiteSpace(uP.GetString())
                ? uP.GetString()!
                : $"https://www.dailymotion.com/video/{id}";

            var thumb = item.TryGetProperty("thumbnail_480_url", out var thP) ? thP.GetString() ?? string.Empty : string.Empty;
            var filmstrip = item.TryGetProperty("filmstrip_60_url", out var fsP) ? fsP.GetString() ?? string.Empty : string.Empty;
            var author = item.TryGetProperty("owner.screenname", out var owP) ? owP.GetString() ?? string.Empty : string.Empty;

            string durationStr = string.Empty;
            if (item.TryGetProperty("duration", out var dP) && dP.TryGetInt32(out var secs) && secs > 0)
            {
                var ts = TimeSpan.FromSeconds(secs);
                durationStr = ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"mm\:ss");
            }

            results.Add(new VideoResult
            {
                Id = $"dm_{id}",
                Title = WebUtility.HtmlDecode(title).Trim(),
                Author = WebUtility.HtmlDecode(author).Trim(),
                Duration = durationStr,
                ThumbnailUrl = thumb,
                FilmstripUrl = filmstrip,
                VideoPageUrl = pageUrl,
                PlayerUrl = $"https://geo.dailymotion.com/player.html?video={id}&autoplay=true",
                SourceSiteId = site.Id,
                SourceSiteName = site.DisplayName,
                SourceDomain = site.Domain
            });
        }

        return results;
    }
}
