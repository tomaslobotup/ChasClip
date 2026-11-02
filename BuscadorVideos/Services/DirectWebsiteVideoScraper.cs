using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Motor universal de extracción de videos: utiliza el navegador Chromium en segundo plano (interacción real con el
/// buscador de cada página y detección geométrica de tarjetas en el DOM) combinado con extracción HTTP directa.
/// Funciona para cualquier página ingresada sin depender de listas fijas de sitios.
/// </summary>
public class DirectWebsiteVideoScraper
{
    private static readonly Regex AnchorWithMediaRegex = new(
        @"<a\b([^>]*?\bhref\s*=\s*(?:""([^""]+)""|'([^']+)')[^>]*)>((?:(?!<a\b)[\s\S]){1,4500}?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ImgTagRegex = new(
        @"<img\b[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AttrPairRegex = new(
        @"([a-zA-Z0-9_\-:]+)\s*=\s*(?:""([^""]*)""|'([^']*)')",
        RegexOptions.Compiled);

    private static readonly Regex DurationTextRegex = new(
        @"(?:class\s*=\s*[""'][^""']*(?:duration|time|length|dur|hd-time)[^""']*[""'][^>]*>\s*|<var\b[^>]*class\s*=\s*[""'][^""']*duration[^""']*[""'][^>]*>\s*)(\d{1,3}:\d{2}(?::\d{2})?|\d{1,3}\s*min(?:\s*\d{1,2}\s*sec)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NearbyTitleTagRegex = new(
        @"<(?:h[1-5]|p|div|span)\b[^>]*class\s*=\s*[""'][^""']*(?:title|vtitle|name|caption|headline|subject)[^""']*[""'][^>]*>([\s\S]{1,400}?)</(?:h[1-5]|p|div|span)>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex XvThumbToMp4Regex = new(
        @"^(https?://[^/]+/videos/)thumbs169[a-z]*(/[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-f]{2}/[0-9a-zA-Z_\-]+)\.\d+\.jpg$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // Separa entradas de srcset únicamente tras descriptores (ej. "320w," o "2x,") o antes de un nuevo esquema URL,
    // sin romper jamás URLs con comas internas como xHamster "/s(w:320,h:180)/" o Cloudinary "/w_320,h_180/"
    private static readonly Regex SrcsetEntrySplitRegex = new(
        @"(?<=\s\d+(?:\.\d+)?[wx])\s*,\s*|,\s*(?=https?://|//)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] IgnoredPathSegments =
    [
        "/login", "/signin", "/signup", "/register", "/auth",
        "/categories", "/category/", "/categorias", "/categoria/", "/cat/", "/tags", "/tag/",
        "/pornstars", "/pornstar/", "/models", "/model/", "/actors", "/actor/", "/actresses", "/actress/", "/performers",
        "/channels", "/channel/", "/users/", "/user/", "/profile/", "/members/",
        "/gifs", "/gif/", "/photos", "/photo/", "/gallery", "/galleries", "/playlists", "/playlist/",
        "/upload", "/premium", "/upgrade", "/billing",
        "/live", "/cams", "/livecams", "/chat",
        "/privacy", "/terms", "/dmca", "/2257", "/contact", "/faq", "/help", "/support", "/about", "/ads"
    ];

    private static readonly string[] CoverImgAttrPriority =
    [
        "data-src", "data-original", "data-thumb_url", "data-mediumthumb",
        "data-poster", "data-lazy-src", "data-lazy", "data-image",
        "data-echo", "data-webp", "data-src-hq", "src", "srcset"
    ];

    private static readonly string[] PreviewUrlAttrNames =
    [
        "data-mediabook", "data-preview", "data-video-preview", "data-preview-url",
        "data-previewvideo", "data-trailer", "data-mp4", "data-webm",
        "data-vurl", "data-video", "data-pvv", "data-webp-preview", "data-gif"
    ];

    private readonly IBrowserScraper? _browserScraper;

    public DirectWebsiteVideoScraper(IBrowserScraper? browserScraper = null)
    {
        _browserScraper = browserScraper;
    }

    public async Task<List<VideoResult>> ScrapeDirectUrlAsync(string targetUrl, CancellationToken ct = default)
    {
        var tempSite = SavedSite.FromUserInput(targetUrl);
        var results = new List<VideoResult>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_browserScraper != null && !ct.IsCancellationRequested)
        {
            var browserItems = await _browserScraper.InteractiveSiteSearchAsync(tempSite, string.Empty, ct);
            foreach (var item in browserItems)
            {
                if (seenUrls.Add(NormalizeUrlKey(item.VideoPageUrl)))
                    results.Add(item);
            }
        }

        if (results.Count < 2 && !ct.IsCancellationRequested)
        {
            try
            {
                var html = await HttpHelper.GetUtf8StringWithAgeBypassAsync(targetUrl, tempSite.BaseUrl, ct);
                ParseGenericVideoCardsFromHtml(html, targetUrl, tempSite, results, seenUrls);
            }
            catch
            {
                // Ignorar fallo HTTP si ya se intentó por navegador
            }
        }

        return results;
    }

    /// <summary>
    /// Busca videos en cualquier página ingresada por el usuario de forma universal:
    /// 1) Prueba primero rutas HTTP directas en paralelo rápido (ideal cuando se marca una categoría entera de páginas).
    /// 2) Si la página usa POST, redirección JS (ej. en.luxuretv.com), SPA o Cloudflare, usa el motor Chromium interactivo.
    /// </summary>
    public async Task<List<VideoResult>> SearchSiteDirectlyAsync(SavedSite site, string query, CancellationToken ct = default)
    {
        var results = new List<VideoResult>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1) Búsqueda rápida HTTP en paralelo (permite buscar en múltiples sitios marcados en 1-2 segundos)
        var encoded = Uri.EscapeDataString(query);
        var plusEncoded = encoded.Replace("%20", "+");
        var baseUrl = site.BaseUrl.TrimEnd('/');

        var candidateUrls = new List<string>();
        if (site.Url.Contains("{q}", StringComparison.OrdinalIgnoreCase))
        {
            candidateUrls.Add(site.Url.Replace("{q}", encoded, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            candidateUrls.AddRange(
            [
                $"{baseUrl}/search/videos/{encoded}/",
                $"{baseUrl}/video/search?search={plusEncoded}",
                $"{baseUrl}/search?q={encoded}",
                $"{baseUrl}/search/{encoded}/",
                $"{baseUrl}/s/{encoded}/",
                $"{baseUrl}/?k={plusEncoded}",
                $"{baseUrl}/?s={encoded}"
            ]);
        }

        var probeSync = new object();
        var probeTasks = candidateUrls.Select(async searchUrl =>
        {
            if (ct.IsCancellationRequested) return;
            try
            {
                using var routeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                routeCts.CancelAfter(TimeSpan.FromSeconds(4.5));

                var html = await HttpHelper.GetUtf8StringWithAgeBypassAsync(searchUrl, site.BaseUrl, routeCts.Token);
                var localResults = new List<VideoResult>();
                var localSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                ParseGenericVideoCardsFromHtml(html, searchUrl, site, localResults, localSeen);

                if (localResults.Count > 0)
                {
                    lock (probeSync)
                    {
                        foreach (var item in localResults)
                        {
                            if (seenUrls.Add(NormalizeUrlKey(item.VideoPageUrl)))
                                results.Add(item);
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // Probar demás rutas en paralelo o respaldo Chromium interactivo
            }
        });

        await Task.WhenAll(probeTasks);
        if (results.Count >= 6)
            return results;

        // 2) Respaldo universal interactivo en Chromium (escribe en el buscador real de la página y extrae por geometría DOM)
        if (results.Count < 4 && _browserScraper != null && !ct.IsCancellationRequested)
        {
            try
            {
                var browserResults = await _browserScraper.InteractiveSiteSearchAsync(site, query, ct);
                foreach (var item in browserResults)
                {
                    if (seenUrls.Add(NormalizeUrlKey(item.VideoPageUrl)))
                        results.Add(item);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // Conservar resultados parciales si existieran
            }
        }

        return results;
    }

    public static void ParseGenericVideoCardsFromHtml(
        string html,
        string pageUrl,
        SavedSite site,
        List<VideoResult> results,
        HashSet<string> seenUrls)
    {
        if (string.IsNullOrWhiteSpace(html) || !Uri.TryCreate(pageUrl, UriKind.Absolute, out var pageUri))
            return;

        foreach (Match anchorMatch in AnchorWithMediaRegex.Matches(html))
        {
            var anchorTagAttrsRaw = anchorMatch.Groups[1].Value;
            var rawHref = !string.IsNullOrEmpty(anchorMatch.Groups[2].Value)
                ? anchorMatch.Groups[2].Value
                : anchorMatch.Groups[3].Value;
            var innerHtml = anchorMatch.Groups[4].Value;

            if (!innerHtml.Contains("<img", StringComparison.OrdinalIgnoreCase) &&
                !innerHtml.Contains("<video", StringComparison.OrdinalIgnoreCase))
                continue;

            var resolvedVideoUrl = ResolveCandidateVideoUrl(rawHref, pageUri, site);
            if (string.IsNullOrWhiteSpace(resolvedVideoUrl))
                continue;

            var dedupKey = NormalizeUrlKey(resolvedVideoUrl);
            if (seenUrls.Contains(dedupKey))
                continue;

            var imgMatch = ImgTagRegex.Match(innerHtml);
            if (!imgMatch.Success)
                continue;

            var imgAttrs = ParseHtmlAttributes(imgMatch.Value);
            var thumbUrl = ExtractBestCoverFromImgAttributes(imgAttrs, pageUri);
            if (string.IsNullOrWhiteSpace(thumbUrl))
                continue;

            var anchorAttrs = ParseHtmlAttributes(anchorTagAttrsRaw);

            int ctxStart = Math.Max(0, anchorMatch.Index - 350);
            int ctxLen = Math.Min(html.Length - ctxStart, anchorMatch.Length + 1200);
            var cardContextHtml = html.Substring(ctxStart, ctxLen);

            int afterStart = anchorMatch.Index + anchorMatch.Length;
            int afterLen = Math.Min(html.Length - afterStart, 850);
            var afterAnchorHtml = afterLen > 0 ? html.Substring(afterStart, afterLen) : string.Empty;

            var (previewMp4, previewWebp) = ExtractHoverPreviewFromContext(imgAttrs, anchorAttrs, cardContextHtml, thumbUrl, pageUri);

            var title = ExtractExactCardTitle(rawHref, anchorAttrs, imgAttrs, innerHtml, afterAnchorHtml);
            if (string.IsNullOrWhiteSpace(title) || title.Length < 2)
                continue;

            string duration = string.Empty;
            var durMatch = DurationTextRegex.Match(innerHtml);
            if (!durMatch.Success)
                durMatch = DurationTextRegex.Match(afterAnchorHtml);
            if (durMatch.Success)
                duration = WebUtility.HtmlDecode(durMatch.Groups[1].Value).Trim();

            seenUrls.Add(dedupKey);
            results.Add(new VideoResult
            {
                Id = $"dir_{Guid.NewGuid():N}",
                Title = title,
                Duration = duration,
                ThumbnailUrl = thumbUrl,
                PreviewMp4Url = previewMp4,
                PreviewWebpUrl = previewWebp,
                VideoPageUrl = resolvedVideoUrl,
                PlayerUrl = UniversalVideoSearchProvider.BuildBestPlayerUrl(resolvedVideoUrl, site.RootDomain),
                SourceSiteId = site.Id,
                SourceSiteName = site.DisplayName,
                SourceDomain = site.Domain
            });
        }
    }

    public static string ResolveCandidateVideoUrl(string? rawHref, Uri pageUri, SavedSite site)
    {
        if (string.IsNullOrWhiteSpace(rawHref))
            return string.Empty;

        var decoded = WebUtility.HtmlDecode(rawHref).Trim();
        if (decoded.StartsWith('#') ||
            decoded.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            decoded.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            return string.Empty;

        if (!Uri.TryCreate(pageUri, decoded, out var resolved))
            return string.Empty;

        if (resolved.Scheme != Uri.UriSchemeHttp && resolved.Scheme != Uri.UriSchemeHttps)
            return string.Empty;

        if (!site.MatchesUrl(resolved.ToString()))
            return string.Empty;

        var path = resolved.AbsolutePath;
        if (string.IsNullOrWhiteSpace(path) || path == "/" || path.Length < 3)
            return string.Empty;

        if (string.Equals(resolved.AbsolutePath, pageUri.AbsolutePath, StringComparison.OrdinalIgnoreCase))
        {
            var q = resolved.Query.ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(q) || q.Contains("page=") || q.Contains("p=") || q.Contains("search=") || q.Contains("q="))
                return string.Empty;
        }

        foreach (var ignored in IgnoredPathSegments)
        {
            if (path.StartsWith(ignored, StringComparison.OrdinalIgnoreCase) ||
                path.Contains(ignored + "/", StringComparison.OrdinalIgnoreCase))
                return string.Empty;
        }

        if (UniversalVideoSearchProvider.IsLikelyNonVideoPage(resolved.ToString()))
            return string.Empty;

        return resolved.ToString();
    }

    private static string ExtractBestCoverFromImgAttributes(Dictionary<string, string> imgAttrs, Uri pageUri)
    {
        foreach (var attrName in CoverImgAttrPriority)
        {
            if (!imgAttrs.TryGetValue(attrName, out var rawVal) || string.IsNullOrWhiteSpace(rawVal))
                continue;

            var candidate = WebUtility.HtmlDecode(rawVal).Trim();
            if (attrName.Equals("srcset", StringComparison.OrdinalIgnoreCase))
            {
                candidate = ExtractBestUrlFromSrcset(candidate);
            }

            if (IsPlaceholderOrIconImage(candidate))
                continue;

            var resolved = ResolveMediaUrl(candidate, pageUri);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
        }

        return string.Empty;
    }

    public static string ExtractBestUrlFromSrcset(string rawSrcset)
    {
        if (string.IsNullOrWhiteSpace(rawSrcset))
            return string.Empty;

        var entries = SrcsetEntrySplitRegex.Split(rawSrcset)
            .Select(e => e.Trim())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .ToList();

        if (entries.Count == 0)
            return string.Empty;

        // Elegir preferiblemente una entrada de ~320w a 720w (o la última disponible de mejor definición)
        string bestUrl = string.Empty;
        foreach (var entry in entries)
        {
            var token = entry.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (!string.IsNullOrWhiteSpace(token) && !IsPlaceholderOrIconImage(token))
            {
                bestUrl = token;
            }
        }

        return bestUrl;
    }

    private static bool IsPlaceholderOrIconImage(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return true;

        var lower = url.ToLowerInvariant();
        return lower.StartsWith("data:image/") ||
               lower.EndsWith(".svg") ||
               lower.Contains("spacer.") ||
               lower.Contains("blank.") ||
               lower.Contains("pixel.") ||
               lower.Contains("loading.") ||
               lower.Contains("/logo") ||
               lower.Contains("favicon") ||
               lower.Contains("/flags/");
    }

    private static (string Mp4Url, string WebpUrl) ExtractHoverPreviewFromContext(
        Dictionary<string, string> imgAttrs,
        Dictionary<string, string> anchorAttrs,
        string cardContextHtml,
        string thumbUrl,
        Uri pageUri)
    {
        foreach (var dict in new[] { imgAttrs, anchorAttrs })
        {
            foreach (var attr in PreviewUrlAttrNames)
            {
                if (dict.TryGetValue(attr, out var val) && !string.IsNullOrWhiteSpace(val))
                {
                    var classified = ClassifyPreviewUrl(val, pageUri);
                    if (classified != default)
                        return classified;
                }
            }
        }

        foreach (Match m in AttrPairRegex.Matches(cardContextHtml))
        {
            var name = m.Groups[1].Value.ToLowerInvariant();
            var val = !string.IsNullOrEmpty(m.Groups[2].Value) ? m.Groups[2].Value : m.Groups[3].Value;
            if (string.IsNullOrWhiteSpace(val))
                continue;

            if (PreviewUrlAttrNames.Contains(name) ||
                ((name is "src" or "data-src") &&
                 (val.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
                  val.Contains(".webm", StringComparison.OrdinalIgnoreCase))))
            {
                var classified = ClassifyPreviewUrl(val, pageUri);
                if (classified != default)
                    return classified;
            }
        }

        if (!string.IsNullOrWhiteSpace(thumbUrl))
        {
            var xvMatch = XvThumbToMp4Regex.Match(thumbUrl);
            if (xvMatch.Success)
            {
                return ($"{xvMatch.Groups[1].Value}videopreview{xvMatch.Groups[2].Value}_169.mp4", string.Empty);
            }
        }

        return (string.Empty, string.Empty);
    }

    public static (string Mp4Url, string WebpUrl) ClassifyPreviewUrl(string rawUrl, Uri pageUri)
    {
        var resolved = ResolveMediaUrl(WebUtility.HtmlDecode(rawUrl), pageUri);
        if (string.IsNullOrWhiteSpace(resolved))
            return default;

        if (resolved.Contains(".webp", StringComparison.OrdinalIgnoreCase) ||
            resolved.Contains(".gif", StringComparison.OrdinalIgnoreCase))
            return (string.Empty, resolved);

        if (resolved.Contains(".mp4", StringComparison.OrdinalIgnoreCase) ||
            resolved.Contains(".webm", StringComparison.OrdinalIgnoreCase) ||
            resolved.Contains("mediabook", StringComparison.OrdinalIgnoreCase) ||
            resolved.Contains("preview", StringComparison.OrdinalIgnoreCase) ||
            resolved.Contains("trailer", StringComparison.OrdinalIgnoreCase))
            return (resolved, string.Empty);

        return default;
    }

    private static string ExtractExactCardTitle(
        string rawHref,
        Dictionary<string, string> anchorAttrs,
        Dictionary<string, string> imgAttrs,
        string innerAnchorHtml,
        string afterAnchorHtml)
    {
        var escapedHref = Regex.Escape(rawHref);
        var siblingLinkMatch = Regex.Match(
            afterAnchorHtml,
            $@"<a\b[^>]*href\s*=\s*[""']{escapedHref}[""'][^>]*?(?:title\s*=\s*[""']([^""']+)[""'][^>]*>|>\s*([^<]+?)\s*</a>)",
            RegexOptions.IgnoreCase);

        if (siblingLinkMatch.Success)
        {
            var t = !string.IsNullOrWhiteSpace(siblingLinkMatch.Groups[1].Value)
                ? siblingLinkMatch.Groups[1].Value
                : siblingLinkMatch.Groups[2].Value;
            t = CleanHtmlTitle(t);
            if (t.Length >= 3) return t;
        }

        if (anchorAttrs.TryGetValue("title", out var aTitle))
        {
            var t = CleanHtmlTitle(aTitle);
            if (t.Length >= 3) return t;
        }

        var nearbyMatch = NearbyTitleTagRegex.Match(afterAnchorHtml);
        if (nearbyMatch.Success)
        {
            var innerTitleAttr = Regex.Match(nearbyMatch.Groups[1].Value, @"title\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
            var t = innerTitleAttr.Success
                ? CleanHtmlTitle(innerTitleAttr.Groups[1].Value)
                : CleanHtmlTitle(nearbyMatch.Groups[1].Value);
            if (t.Length >= 3) return t;
        }

        if (imgAttrs.TryGetValue("alt", out var imgAlt))
        {
            var t = CleanHtmlTitle(imgAlt);
            if (t.Length >= 3) return t;
        }

        return CleanHtmlTitle(innerAnchorHtml);
    }

    private static string CleanHtmlTitle(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        var noTags = Regex.Replace(raw, "<.*?>", " ");
        var decoded = WebUtility.HtmlDecode(noTags);
        var cleaned = Regex.Replace(decoded, @"\s+", " ").Trim();
        return Regex.Replace(cleaned, @"^Permalink\s+to\s+", "", RegexOptions.IgnoreCase).Trim();
    }

    public static string ResolveMediaUrl(string? rawUrl, Uri pageUri)
    {
        if (string.IsNullOrWhiteSpace(rawUrl))
            return string.Empty;

        var cleaned = WebUtility.HtmlDecode(rawUrl).Trim();
        if (cleaned.StartsWith("//"))
            return $"{pageUri.Scheme}:{cleaned}";

        return Uri.TryCreate(pageUri, cleaned, out var u) &&
               (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps)
            ? u.ToString()
            : string.Empty;
    }

    public static string NormalizeUrlKey(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url.Trim();

        return $"{uri.Scheme}://{SavedSite.ExtractRootDomain(uri.Host)}{uri.AbsolutePath.TrimEnd('/')}{uri.Query}";
    }

    private static Dictionary<string, string> ParseHtmlAttributes(string tagFragment)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrPairRegex.Matches(tagFragment))
        {
            var key = m.Groups[1].Value;
            var val = !string.IsNullOrEmpty(m.Groups[2].Value) ? m.Groups[2].Value : m.Groups[3].Value;
            dict.TryAdd(key, val);
        }
        return dict;
    }
}
