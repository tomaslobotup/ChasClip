using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Proveedor nativo de búsqueda para YouTube. Extrae portadas HD, vistas previas WebP animadas,
/// fotogramas de respaldo y títulos exactos en su idioma original.
/// </summary>
public class YouTubeSearchProvider
{
    private static readonly Regex ApiKeyRegex = new(@"""INNERTUBE_API_KEY""\s*:\s*""([^""]+)""", RegexOptions.Compiled);

    public async Task<List<VideoResult>> SearchAsync(SavedSite site, string query, CancellationToken ct = default)
    {
        var results = new List<VideoResult>();
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        var url = $"https://www.youtube.com/results?search_query={Uri.EscapeDataString(query)}&sp=EgIQAQ%3D%3D";
        var html = await HttpHelper.GetUtf8StringAsync(url, "https://www.youtube.com/", ct);

        string? continuationToken = ParseYouTubeHtmlInto(html, site, results, seenIds);
        if (results.Count == 0 && string.IsNullOrWhiteSpace(continuationToken))
            return results;

        // Obtener segunda tanda (~40 videos en total) para nutrir varias páginas de la cuadrícula de 12
        if (!string.IsNullOrWhiteSpace(continuationToken) && results.Count < 36 && !ct.IsCancellationRequested)
        {
            try
            {
                var apiKeyMatch = ApiKeyRegex.Match(html);
                var apiKey = apiKeyMatch.Success ? apiKeyMatch.Groups[1].Value : string.Empty;
                var endpoint = string.IsNullOrWhiteSpace(apiKey)
                    ? "https://www.youtube.com/youtubei/v1/search?prettyPrint=false"
                    : $"https://www.youtube.com/youtubei/v1/search?key={apiKey}&prettyPrint=false";

                var payload = JsonSerializer.Serialize(new
                {
                    context = new
                    {
                        client = new
                        {
                            clientName = "WEB",
                            clientVersion = "2.20241120.01.00"
                        }
                    },
                    contentCheckOk = true,
                    racyCheckOk = true,
                    continuation = continuationToken
                });

                using var req = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = new StringContent(payload, Encoding.UTF8, "application/json")
                };
                req.Headers.Referrer = new Uri(url);

                using var resp = await HttpHelper.SharedClient.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    var contJson = Encoding.UTF8.GetString(bytes);
                    string? nextToken = null;
                    using var contDoc = JsonDocument.Parse(contJson);
                    ParseNodeRecursive(contDoc.RootElement, site, results, seenIds, ref nextToken);
                }
            }
            catch
            {
                // Si la paginación secundaria falla, devolvemos igualmente los resultados iniciales
            }
        }

        return results;
    }

    public static string? ParseYouTubeHtmlInto(
        string html,
        SavedSite site,
        List<VideoResult> results,
        HashSet<string> seenIds)
    {
        var jsonText = ExtractYtInitialDataJson(html);
        if (string.IsNullOrWhiteSpace(jsonText))
            return null;

        string? continuationToken = null;
        using var doc = JsonDocument.Parse(jsonText);
        ParseNodeRecursive(doc.RootElement, site, results, seenIds, ref continuationToken);
        return continuationToken;
    }

    private static void ParseNodeRecursive(
        JsonElement element,
        SavedSite site,
        List<VideoResult> results,
        HashSet<string> seenIds,
        ref string? continuationToken)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.NameEquals("videoRenderer") && prop.Value.ValueKind == JsonValueKind.Object)
                {
                    var item = ParseVideoRenderer(prop.Value, site);
                    if (item != null && seenIds.Add(item.Id))
                    {
                        results.Add(item);
                    }
                }
                else if (continuationToken == null &&
                         prop.NameEquals("continuationCommand") &&
                         prop.Value.ValueKind == JsonValueKind.Object &&
                         prop.Value.TryGetProperty("token", out var tokenProp))
                {
                    continuationToken = tokenProp.GetString();
                }
                else
                {
                    ParseNodeRecursive(prop.Value, site, results, seenIds, ref continuationToken);
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in element.EnumerateArray())
            {
                ParseNodeRecursive(child, site, results, seenIds, ref continuationToken);
            }
        }
    }

    private static VideoResult? ParseVideoRenderer(JsonElement vr, SavedSite site)
    {
        if (!vr.TryGetProperty("videoId", out var idProp))
            return null;

        var videoId = idProp.GetString();
        if (string.IsNullOrWhiteSpace(videoId))
            return null;

        var title = ReadTextRunsOrSimple(vr, "title");
        if (string.IsNullOrWhiteSpace(title))
            return null;

        var author = ReadTextRunsOrSimple(vr, "ownerText");
        if (string.IsNullOrWhiteSpace(author))
            author = ReadTextRunsOrSimple(vr, "longBylineText");

        var duration = ReadTextRunsOrSimple(vr, "lengthText");

        // Portada estática JPEG limpia (sin parámetro ?sqp= que fuerza AVIF no decodificable en Windows)
        string thumbnailUrl = $"https://i.ytimg.com/vi/{videoId}/hq720.jpg";
        if (vr.TryGetProperty("thumbnail", out var thumbObj) &&
            thumbObj.TryGetProperty("thumbnails", out var thumbArr) &&
            thumbArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in thumbArr.EnumerateArray())
            {
                if (t.TryGetProperty("url", out var uProp))
                {
                    var candidate = NormalizeYouTubeStaticThumbUrl(uProp.GetString(), videoId);
                    if (!string.IsNullOrWhiteSpace(candidate))
                        thumbnailUrl = candidate;
                }
            }
        }

        // Vista previa animada al pasar el mouse (movingThumbnailRenderer -> WebP animado de 6 segundos)
        string previewWebpUrl = string.Empty;
        if (vr.TryGetProperty("richThumbnail", out var richObj) &&
            richObj.TryGetProperty("movingThumbnailRenderer", out var movRenderer) &&
            movRenderer.TryGetProperty("movingThumbnailDetails", out var movDetails) &&
            movDetails.TryGetProperty("thumbnails", out var movThumbs) &&
            movThumbs.ValueKind == JsonValueKind.Array)
        {
            foreach (var mt in movThumbs.EnumerateArray())
            {
                if (mt.TryGetProperty("url", out var muProp))
                {
                    var candidate = NormalizeUrl(muProp.GetString());
                    if (!string.IsNullOrWhiteSpace(candidate))
                        previewWebpUrl = candidate;
                }
            }
        }

        var watchUrl = $"https://www.youtube.com/watch?v={videoId}";

        return new VideoResult
        {
            Id = $"yt_{videoId}",
            Title = WebUtility.HtmlDecode(title).Trim(),
            Author = WebUtility.HtmlDecode(author).Trim(),
            Duration = duration.Trim(),
            ThumbnailUrl = thumbnailUrl,
            PreviewWebpUrl = previewWebpUrl,
            FallbackFrameUrls =
            [
                $"https://i.ytimg.com/vi/{videoId}/hqdefault.jpg",
                $"https://i.ytimg.com/vi/{videoId}/hq1.jpg",
                $"https://i.ytimg.com/vi/{videoId}/hq2.jpg",
                $"https://i.ytimg.com/vi/{videoId}/hq3.jpg"
            ],
            VideoPageUrl = watchUrl,
            PlayerUrl = watchUrl,
            SourceSiteId = site.Id,
            SourceSiteName = site.DisplayName,
            SourceDomain = site.Domain
        };
    }

    private static string ReadTextRunsOrSimple(JsonElement parent, string propName)
    {
        if (!parent.TryGetProperty(propName, out var prop) || prop.ValueKind != JsonValueKind.Object)
            return string.Empty;

        if (prop.TryGetProperty("simpleText", out var simple) && simple.ValueKind == JsonValueKind.String)
            return simple.GetString() ?? string.Empty;

        if (prop.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var r in runs.EnumerateArray())
            {
                if (r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                    sb.Append(t.GetString());
            }
            return sb.ToString();
        }

        return string.Empty;
    }

    private static string NormalizeYouTubeStaticThumbUrl(string? url, string videoId)
    {
        var normalized = NormalizeUrl(url);
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;

        // Si es una URL de i.ytimg.com/vi/... con token ?sqp= (que puede servir AVIF), limpiar a JPEG puro
        if (normalized.Contains("ytimg.com/vi/", StringComparison.OrdinalIgnoreCase))
        {
            int qIdx = normalized.IndexOf('?');
            if (qIdx > 0)
                normalized = normalized[..qIdx];
        }
        else if (normalized.Contains("ytimg.com/vi_webp/", StringComparison.OrdinalIgnoreCase))
        {
            return $"https://i.ytimg.com/vi/{videoId}/hq720.jpg";
        }

        return normalized;
    }

    private static string NormalizeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;
        if (url.StartsWith("//"))
            return "https:" + url;
        return url;
    }

    private static string? ExtractYtInitialDataJson(string html)
    {
        var markers = new[] { "var ytInitialData = ", "window[\"ytInitialData\"] = ", "ytInitialData = " };
        int startSearch = -1;

        foreach (var marker in markers)
        {
            int idx = html.IndexOf(marker, StringComparison.Ordinal);
            if (idx >= 0)
            {
                startSearch = idx + marker.Length;
                break;
            }
        }

        if (startSearch < 0)
            return null;

        int braceStart = html.IndexOf('{', startSearch);
        if (braceStart < 0)
            return null;

        int depth = 0;
        bool inString = false;
        bool escape = false;

        for (int i = braceStart; i < html.Length; i++)
        {
            char c = html[i];
            if (escape)
            {
                escape = false;
                continue;
            }

            if (c == '\\' && inString)
            {
                escape = true;
                continue;
            }

            if (c == '"')
            {
                inString = !inString;
                continue;
            }

            if (!inString)
            {
                if (c == '{')
                    depth++;
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                        return html.Substring(braceStart, i - braceStart + 1);
                }
            }
        }

        return null;
    }
}
