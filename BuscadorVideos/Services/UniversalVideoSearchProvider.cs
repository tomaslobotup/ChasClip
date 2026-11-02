using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Proveedor universal capaz de buscar videos en TikTok (tiktok.com), Vimeo, Bilibili, Twitch, Instagram, Reddit
/// y cualquier otra página agregada por el usuario a su lista editable.
/// Combina búsqueda indexada de video con clips MP4 de vista previa (vtUrl), respaldo WebView2 Chromium y oEmbed oficial.
/// </summary>
public class UniversalVideoSearchProvider
{
    private static readonly Regex VrhmTagRegex = new(
        @"vrhm\s*=\s*(?:""([^""]+)""|'([^']+)')",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ImgAttrRegex = new(
        @"<img[^>]+?(?:data-src-hq|data-src|src)\s*=\s*""(https?://[^""]+|/th\?[^""]+)""",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TitleAttrRegex = new(
        @"class\s*=\s*""[^""]*mc_vtvc_title[^""]*""[^>]*?(?:title\s*=\s*""([^""]+)""|>([^<]+)<)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TikTokVideoIdRegex = new(
        @"tiktok\.com/(?:@[^/?#]+/video/|v/|embed/v2/)(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VimeoVideoIdRegex = new(
        @"vimeo\.com/(?:channels/[^/]+/|groups/[^/]+/videos/|video/)?(\d+)(?:[/?#]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BilibiliBvidRegex = new(
        @"bilibili\.com/video/(BV[0-9A-Za-z]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex YahooCardRegex = new(
        @"data-rurl\s*=\s*""(https?://[^""]+)""[\s\S]{0,1800}?(?:<img[^>]+?src\s*=\s*""(https?://[^""]+)""[\s\S]{0,900}?)?(?:<h3[^>]*>([\s\S]*?)</h3>|aria-label\s*=\s*""([^""]+)"")",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly IBrowserScraper? _browserScraper;
    private readonly DirectWebsiteVideoScraper _directScraper;

    public UniversalVideoSearchProvider(IBrowserScraper? browserScraper = null)
    {
        _browserScraper = browserScraper;
        _directScraper = new DirectWebsiteVideoScraper(browserScraper);
    }

    public async Task<List<VideoResult>> SearchAsync(SavedSite site, string query, CancellationToken ct = default)
    {
        var results = new List<VideoResult>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Exception? lastNetworkError = null;

        var rootDomain = site.RootDomain;
        bool isTikTok = rootDomain.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase);

        // 1) Para cualquier sitio externo (incluyendo páginas con restricción de edad 18+ o enlaces personalizados),
        //    buscamos PRIMERO directamente dentro del propio sitio sorteando bloqueos de edad y SafeSearch.
        if (!isTikTok && !ct.IsCancellationRequested)
        {
            try
            {
                var directResults = await _directScraper.SearchSiteDirectlyAsync(site, query, ct);
                foreach (var item in directResults)
                {
                    if (seenUrls.Add(NormalizeForDedup(item.VideoPageUrl)))
                        results.Add(item);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastNetworkError = ex;
            }
        }

        // 2) Búsqueda complementaria en índices de video con SafeSearch DESACTIVADO (&adlt=off y &vm=p)
        //    usando el dominio raíz (RootDomain) para que subdominios como es.sitio.com coincidan con www.sitio.com.
        if ((isTikTok || results.Count < 18) && !ct.IsCancellationRequested)
        {
            var searchQuery = $"site:{rootDomain} {query}";
            var urlsToFetch = new[]
            {
                $"https://www.bing.com/videos/search?q={Uri.EscapeDataString(searchQuery)}&adlt=off&FORM=HDRSC4",
                $"https://www.bing.com/videos/asyncv2?q={Uri.EscapeDataString(searchQuery)}&adlt=off&async=content&first=36&count=35"
            };

            foreach (var searchUrl in urlsToFetch)
            {
                if (ct.IsCancellationRequested) break;
                try
                {
                    var html = await HttpHelper.GetUtf8StringAsync(searchUrl, "https://www.bing.com/videos/", ct);
                    ParseBingVideoHtml(html, site, results, seenUrls);
                    lastNetworkError = null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastNetworkError ??= ex;
                }
            }

            if (results.Count < 6 && !ct.IsCancellationRequested)
            {
                try
                {
                    var yahooUrl = $"https://video.search.yahoo.com/search/video?p={Uri.EscapeDataString(searchQuery)}&vm=p";
                    var yahooHtml = await HttpHelper.GetUtf8StringAsync(yahooUrl, "https://video.search.yahoo.com/", ct);
                    ParseYahooVideoHtml(yahooHtml, site, results, seenUrls);
                    lastNetworkError = null;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    lastNetworkError ??= ex;
                }
            }
        }

        // 3) Respaldo automático con navegador Chromium real (WebView2) si aún faltan resultados
        if (results.Count < 4 && _browserScraper != null && !ct.IsCancellationRequested)
        {
            var searchQuery = $"site:{rootDomain} {query}";
            await TryWebViewBingExtractionAsync(site, searchQuery, results, seenUrls, ct);

            if (results.Count < 4 && isTikTok)
            {
                await TryDirectTikTokWebViewAsync(site, query, results, seenUrls, ct);
            }
        }

        if (results.Count == 0 && lastNetworkError != null)
        {
            throw lastNetworkError;
        }

        // 4) Enriquecimiento oEmbed para TikTok / Vimeo (título exacto en su idioma original y portada HD)
        if (results.Count > 0 && !ct.IsCancellationRequested)
        {
            if (isTikTok)
            {
                await EnrichTikTokResultsAsync(results, ct);
            }
            else if (rootDomain.Contains("vimeo.com", StringComparison.OrdinalIgnoreCase))
            {
                await EnrichVimeoResultsAsync(results, ct);
            }
        }

        return results;
    }

    public static void ParseBingVideoHtml(
        string html,
        SavedSite site,
        List<VideoResult> results,
        HashSet<string> seenUrls)
    {
        var matches = VrhmTagRegex.Matches(html);
        foreach (Match match in matches)
        {
            var rawEncoded = !string.IsNullOrEmpty(match.Groups[1].Value)
                ? match.Groups[1].Value
                : match.Groups[2].Value;

            var jsonStr = WebUtility.HtmlDecode(rawEncoded);
            try
            {
                using var doc = JsonDocument.Parse(jsonStr);
                var root = doc.RootElement;

                var purl = GetJsonStringCI(root, "purl", "pgurl");
                var murl = GetJsonStringCI(root, "murl");
                var surl = GetJsonStringCI(root, "surl", "ourl", "vpage");

                string pageUrl = ChooseMatchingSiteUrl(site, purl, murl, surl);
                if (string.IsNullOrWhiteSpace(pageUrl) || IsLikelyNonVideoPage(pageUrl))
                    continue;

                var normalizedDedupUrl = NormalizeForDedup(pageUrl);
                if (!seenUrls.Add(normalizedDedupUrl))
                    continue;

                var previewMp4 = NormalizeBingMediaUrl(GetJsonStringCI(root, "vtUrl", "vurl"));
                var title = GetJsonStringCI(root, "vt", "t", "tl", "title");
                var duration = GetJsonStringCI(root, "du");
                var thid = GetJsonStringCI(root, "thid");

                // Buscar imagen de portada y título dentro del bloque HTML de la tarjeta
                int windowStart = Math.Max(0, match.Index - 900);
                int windowLen = Math.Min(html.Length - windowStart, 2600);
                var snippet = html.Substring(windowStart, windowLen);

                string thumbUrl = string.Empty;
                foreach (Match imgM in ImgAttrRegex.Matches(snippet))
                {
                    var candidate = NormalizeBingMediaUrl(WebUtility.HtmlDecode(imgM.Groups[1].Value));
                    if (!string.IsNullOrWhiteSpace(candidate) &&
                        !candidate.Contains("vt=", StringComparison.OrdinalIgnoreCase))
                    {
                        thumbUrl = candidate;
                        if (candidate.Contains("tse", StringComparison.OrdinalIgnoreCase) ||
                            candidate.Contains("th?id=", StringComparison.OrdinalIgnoreCase))
                            break;
                    }
                }

                // Respaldo directo con thid del JSON vrhm de Bing (garantiza miniatura en TikTok, Vimeo, Reddit, Bilibili, Rumble, etc.)
                if (string.IsNullOrWhiteSpace(thumbUrl) && !string.IsNullOrWhiteSpace(thid))
                {
                    thumbUrl = $"https://tse2.mm.bing.net/th?id={Uri.EscapeDataString(thid)}&pid=Api";
                }

                if (string.IsNullOrWhiteSpace(title))
                {
                    var tm = TitleAttrRegex.Match(snippet);
                    if (tm.Success)
                    {
                        title = !string.IsNullOrWhiteSpace(tm.Groups[1].Value)
                            ? tm.Groups[1].Value
                            : tm.Groups[2].Value;
                    }
                }

                title = CleanSiteSuffixFromTitle(WebUtility.HtmlDecode(title ?? string.Empty), site);
                if (string.IsNullOrWhiteSpace(title))
                    title = pageUrl;

                results.Add(new VideoResult
                {
                    Id = $"uni_{Guid.NewGuid():N}",
                    Title = title,
                    Duration = duration ?? string.Empty,
                    ThumbnailUrl = thumbUrl,
                    PreviewMp4Url = previewMp4,
                    VideoPageUrl = pageUrl,
                    PlayerUrl = BuildBestPlayerUrl(pageUrl, site.Domain),
                    SourceSiteId = site.Id,
                    SourceSiteName = site.DisplayName,
                    SourceDomain = site.Domain
                });
            }
            catch
            {
                // Ignorar tarjeta individual con JSON malformado
            }
        }
    }

    private async Task TryWebViewBingExtractionAsync(
        SavedSite site,
        string searchQuery,
        List<VideoResult> results,
        HashSet<string> seenUrls,
        CancellationToken ct)
    {
        if (_browserScraper == null) return;

        var url = $"https://www.bing.com/videos/search?q={Uri.EscapeDataString(searchQuery)}&adlt=off&FORM=HDRSC4";
        const string waitJs = "document.querySelectorAll('[vrhm]').length > 0";
        const string extractJs = @"
(() => {
    const items = [];
    document.querySelectorAll('[vrhm]').forEach(el => {
        try {
            const data = JSON.parse(el.getAttribute('vrhm'));
            const card = el.closest('.dg_u, .mc_vtvc, li, div') || el.parentElement;
            let thumb = '';
            if (card) {
                const imgs = card.querySelectorAll('img');
                for (const img of imgs) {
                    const s = img.getAttribute('data-src-hq') || img.getAttribute('data-src') || img.src || '';
                    if (s && s.startsWith('http')) { thumb = s; break; }
                }
            }
            const titleEl = card ? card.querySelector('.mc_vtvc_title') : null;
            const domTitle = titleEl ? (titleEl.getAttribute('title') || titleEl.textContent || '') : '';
            items.push({
                purl: data.purl || data.pgurl || '',
                murl: data.murl || '',
                vtUrl: data.vtUrl || '',
                t: data.vt || data.t || domTitle || '',
                du: data.du || '',
                thid: data.thid || '',
                thumb: thumb
            });
        } catch(e) {}
    });
    return JSON.stringify(items);
})();";

        var jsonResult = await _browserScraper.NavigateAndRunScriptAsync(url, waitJs, extractJs, 6500, ct);
        if (string.IsNullOrWhiteSpace(jsonResult)) return;

        try
        {
            using var doc = JsonDocument.Parse(jsonResult);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var purl = GetJsonStringCI(item, "purl");
                var murl = GetJsonStringCI(item, "murl");
                var pageUrl = ChooseMatchingSiteUrl(site, purl, murl);
                if (string.IsNullOrWhiteSpace(pageUrl) || IsLikelyNonVideoPage(pageUrl)) continue;

                if (!seenUrls.Add(NormalizeForDedup(pageUrl))) continue;

                var title = CleanSiteSuffixFromTitle(GetJsonStringCI(item, "t") ?? string.Empty, site);
                var thumb = NormalizeBingMediaUrl(GetJsonStringCI(item, "thumb"));
                var thid = GetJsonStringCI(item, "thid");
                if (string.IsNullOrWhiteSpace(thumb) && !string.IsNullOrWhiteSpace(thid))
                    thumb = $"https://tse2.mm.bing.net/th?id={Uri.EscapeDataString(thid)}&pid=Api";
                var vtUrl = NormalizeBingMediaUrl(GetJsonStringCI(item, "vtUrl"));
                var du = GetJsonStringCI(item, "du") ?? string.Empty;

                results.Add(new VideoResult
                {
                    Id = $"wv_{Guid.NewGuid():N}",
                    Title = string.IsNullOrWhiteSpace(title) ? pageUrl : title,
                    Duration = du,
                    ThumbnailUrl = thumb,
                    PreviewMp4Url = vtUrl,
                    VideoPageUrl = pageUrl,
                    PlayerUrl = BuildBestPlayerUrl(pageUrl, site.Domain),
                    SourceSiteId = site.Id,
                    SourceSiteName = site.DisplayName,
                    SourceDomain = site.Domain
                });
            }
        }
        catch
        {
            // Ignorar errores de parseo del script
        }
    }

    private async Task TryDirectTikTokWebViewAsync(
        SavedSite site,
        string query,
        List<VideoResult> results,
        HashSet<string> seenUrls,
        CancellationToken ct)
    {
        if (_browserScraper == null) return;

        var url = $"https://www.tiktok.com/search/video?q={Uri.EscapeDataString(query)}";
        const string waitJs = "document.querySelectorAll('a[href*=\"/video/\"]').length > 0";
        const string extractJs = @"
(() => {
    const items = [];
    document.querySelectorAll('a[href*=""/video/""]').forEach(a => {
        const href = a.href;
        if (!href) return;
        const container = a.closest('[data-e2e=""search_video-item""], div') || a;
        const img = a.querySelector('img');
        const thumb = img ? (img.src || '') : '';
        const alt = img ? (img.alt || '') : '';
        items.push({ url: href, title: alt || container.innerText || '', thumb: thumb });
    });
    return JSON.stringify(items);
})();";

        var jsonResult = await _browserScraper.NavigateAndRunScriptAsync(url, waitJs, extractJs, 7000, ct);
        if (string.IsNullOrWhiteSpace(jsonResult)) return;

        try
        {
            using var doc = JsonDocument.Parse(jsonResult);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var pageUrl = GetJsonStringCI(item, "url");
                if (string.IsNullOrWhiteSpace(pageUrl) || !TikTokVideoIdRegex.IsMatch(pageUrl))
                    continue;

                if (!seenUrls.Add(NormalizeForDedup(pageUrl)))
                    continue;

                var title = (GetJsonStringCI(item, "title") ?? string.Empty).Trim();
                var thumb = GetJsonStringCI(item, "thumb") ?? string.Empty;

                results.Add(new VideoResult
                {
                    Id = $"tt_{Guid.NewGuid():N}",
                    Title = string.IsNullOrWhiteSpace(title) ? pageUrl : title,
                    ThumbnailUrl = thumb,
                    VideoPageUrl = pageUrl,
                    PlayerUrl = BuildBestPlayerUrl(pageUrl, site.Domain),
                    SourceSiteId = site.Id,
                    SourceSiteName = site.DisplayName,
                    SourceDomain = site.Domain
                });
            }
        }
        catch
        {
            // Ignorar si TikTok requiere verificación manual
        }
    }

    private static async Task EnrichTikTokResultsAsync(List<VideoResult> results, CancellationToken ct)
    {
        // Enriquecemos en paralelo usando el endpoint oficial oEmbed de TikTok para obtener el título exacto en su idioma original
        using var sem = new SemaphoreSlim(8);
        var tasks = results.Take(36).Select(async video =>
        {
            if (!TikTokVideoIdRegex.IsMatch(video.VideoPageUrl))
                return;

            await sem.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));

                var oembedUrl = $"https://www.tiktok.com/oembed?url={Uri.EscapeDataString(video.VideoPageUrl)}";
                var json = await HttpHelper.GetUtf8StringAsync(oembedUrl, "https://www.tiktok.com/", cts.Token);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var exactTitle = GetJsonStringCI(root, "title");
                if (!string.IsNullOrWhiteSpace(exactTitle))
                    video.Title = exactTitle.Trim();

                var author = GetJsonStringCI(root, "author_name");
                if (!string.IsNullOrWhiteSpace(author))
                    video.Author = author.Trim();

                var thumb = GetJsonStringCI(root, "thumbnail_url");
                if (!string.IsNullOrWhiteSpace(thumb) && string.IsNullOrWhiteSpace(video.ThumbnailUrl))
                    video.ThumbnailUrl = thumb;
                else if (!string.IsNullOrWhiteSpace(thumb) && video.ThumbnailUrl.Contains("bing.net"))
                    video.ThumbnailUrl = thumb; // Preferir portada HD directa de TikTok cuando está disponible
            }
            catch
            {
                // Conservar metadatos extraídos de la búsqueda si oEmbed no responde para un video puntual
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    private static async Task EnrichVimeoResultsAsync(List<VideoResult> results, CancellationToken ct)
    {
        using var sem = new SemaphoreSlim(6);
        var tasks = results.Take(24).Select(async video =>
        {
            await sem.WaitAsync(ct);
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(4));

                var oembedUrl = $"https://vimeo.com/api/oembed.json?url={Uri.EscapeDataString(video.VideoPageUrl)}";
                var json = await HttpHelper.GetUtf8StringAsync(oembedUrl, "https://vimeo.com/", cts.Token);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var exactTitle = GetJsonStringCI(root, "title");
                if (!string.IsNullOrWhiteSpace(exactTitle))
                    video.Title = exactTitle.Trim();

                var author = GetJsonStringCI(root, "author_name");
                if (!string.IsNullOrWhiteSpace(author))
                    video.Author = author.Trim();

                var thumb = GetJsonStringCI(root, "thumbnail_url");
                if (!string.IsNullOrWhiteSpace(thumb))
                    video.ThumbnailUrl = thumb;
            }
            catch
            {
                // Conservar datos previos si falla
            }
            finally
            {
                sem.Release();
            }
        });

        await Task.WhenAll(tasks);
    }

    public static string BuildBestPlayerUrl(string pageUrl, string domain)
    {
        var ttMatch = TikTokVideoIdRegex.Match(pageUrl);
        if (ttMatch.Success)
            return $"https://www.tiktok.com/player/v1/{ttMatch.Groups[1].Value}?autoplay=1&music_info=1&description=0";

        var vimeoMatch = VimeoVideoIdRegex.Match(pageUrl);
        if (vimeoMatch.Success)
            return $"https://player.vimeo.com/video/{vimeoMatch.Groups[1].Value}?autoplay=1";

        var biliMatch = BilibiliBvidRegex.Match(pageUrl);
        if (biliMatch.Success)
            return $"https://player.bilibili.com/player.html?bvid={biliMatch.Groups[1].Value}&high_quality=1&autoplay=1";

        return pageUrl;
    }

    public static bool IsLikelyNonVideoPage(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return true;

        var host = uri.Host.ToLowerInvariant();
        var path = uri.AbsolutePath.ToLowerInvariant();

        if (host.Contains("vimeo.com") && !host.Contains("player.vimeo.com"))
        {
            if (!VimeoVideoIdRegex.IsMatch(url))
                return true;
        }

        if (host.Contains("tiktok.com") && !TikTokVideoIdRegex.IsMatch(url))
            return true;

        return false;
    }

    private static void ParseYahooVideoHtml(
        string html,
        SavedSite site,
        List<VideoResult> results,
        HashSet<string> seenUrls)
    {
        foreach (Match m in YahooCardRegex.Matches(html))
        {
            var rawUrl = WebUtility.HtmlDecode(m.Groups[1].Value);
            if (!site.MatchesUrl(rawUrl))
                continue;

            if (!seenUrls.Add(NormalizeForDedup(rawUrl)))
                continue;

            var thumb = WebUtility.HtmlDecode(m.Groups[2].Value);
            var rawTitle = !string.IsNullOrWhiteSpace(m.Groups[3].Value)
                ? Regex.Replace(m.Groups[3].Value, "<.*?>", string.Empty)
                : m.Groups[4].Value;

            var title = CleanSiteSuffixFromTitle(WebUtility.HtmlDecode(rawTitle), site);
            if (string.IsNullOrWhiteSpace(title))
                title = rawUrl;

            results.Add(new VideoResult
            {
                Id = $"yh_{Guid.NewGuid():N}",
                Title = title,
                ThumbnailUrl = thumb,
                VideoPageUrl = rawUrl,
                PlayerUrl = BuildBestPlayerUrl(rawUrl, site.Domain),
                SourceSiteId = site.Id,
                SourceSiteName = site.DisplayName,
                SourceDomain = site.Domain
            });
        }
    }

    private static string ChooseMatchingSiteUrl(SavedSite site, params string?[] candidates)
    {
        foreach (var c in candidates)
        {
            if (site.MatchesUrl(c))
                return c!;
        }
        return string.Empty;
    }

    private static string NormalizeForDedup(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url.Trim();

        // Para TikTok y páginas similares ignoramos query params de tracking (?is_from_webapp=1...)
        if (uri.Host.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase) ||
            uri.Host.Contains("vimeo.com", StringComparison.OrdinalIgnoreCase))
        {
            return $"{uri.Scheme}://{uri.Host.ToLowerInvariant()}{uri.AbsolutePath.TrimEnd('/')}";
        }

        return url.Trim();
    }

    private static string NormalizeBingMediaUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;
        if (url.StartsWith("//"))
            return "https:" + url;
        if (url.StartsWith('/'))
            return "https://www.bing.com" + url;
        return url;
    }

    private static string CleanSiteSuffixFromTitle(string rawTitle, SavedSite site)
    {
        var t = rawTitle.Trim();
        string[] suffixes =
        [
            " | TikTok", " - TikTok", " on TikTok",
            " on Vimeo", " | Vimeo",
            $" | {site.DisplayName}", $" - {site.DisplayName}",
            $" | {site.Domain}", $" - {site.Domain}"
        ];

        foreach (var s in suffixes)
        {
            if (t.EndsWith(s, StringComparison.OrdinalIgnoreCase) && t.Length > s.Length + 2)
            {
                t = t[..^s.Length].Trim();
            }
        }

        if (t.StartsWith("TikTok · ", StringComparison.OrdinalIgnoreCase) && t.Length > 10)
            t = t[9..].Trim();

        return t;
    }

    private static string? GetJsonStringCI(JsonElement obj, params string[] names)
    {
        if (obj.ValueKind != JsonValueKind.Object)
            return null;

        foreach (var target in names)
        {
            foreach (var prop in obj.EnumerateObject())
            {
                if (string.Equals(prop.Name, target, StringComparison.OrdinalIgnoreCase) &&
                    prop.Value.ValueKind == JsonValueKind.String)
                {
                    return prop.Value.GetString();
                }
            }
        }

        return null;
    }
}
