using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Coordina la búsqueda simultánea de videos en todas las páginas activas de la lista editable del usuario,
/// así como la extracción directa cuando el usuario pega un enlace completo en el buscador.
/// </summary>
public class VideoSearchCoordinator
{
    private readonly YouTubeSearchProvider _youTubeProvider = new();
    private readonly DailymotionSearchProvider _dailymotionProvider = new();
    private readonly UniversalVideoSearchProvider _universalProvider;
    private readonly DirectWebsiteVideoScraper _directScraper;

    public VideoSearchCoordinator(IBrowserScraper? browserScraper = null)
    {
        _universalProvider = new UniversalVideoSearchProvider(browserScraper);
        _directScraper = new DirectWebsiteVideoScraper(browserScraper);
    }

    public static bool IsDirectWebUrl(string query)
    {
        var q = (query ?? string.Empty).Trim();
        return (q.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                q.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) &&
               Uri.TryCreate(q, UriKind.Absolute, out _);
    }

    public async Task<(List<VideoResult> Results, List<string> Warnings)> SearchAllAsync(
        IReadOnlyList<SavedSite> activeSites,
        string query,
        CancellationToken ct = default)
    {
        var warnings = new List<string>();

        // Si el usuario pegó directamente un enlace https://... en la barra de búsqueda, extraemos los videos de ese enlace
        if (IsDirectWebUrl(query))
        {
            try
            {
                var directUrlVideos = await _directScraper.ScrapeDirectUrlAsync(query.Trim(), ct);
                return (directUrlVideos, warnings);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                warnings.Add(ex.Message);
                return ([], warnings);
            }
        }

        var perSiteResults = new List<VideoResult>[activeSites.Count];
        using var concurrencyGate = new SemaphoreSlim(8, 8);

        var tasks = activeSites.Select(async (site, index) =>
        {
            await concurrencyGate.WaitAsync(ct);
            try
            {
                var siteList = await SearchSingleSiteAsync(site, query, ct);
                perSiteResults[index] = siteList;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                perSiteResults[index] = [];
                lock (warnings)
                {
                    warnings.Add($"{site.DisplayName}: {ex.Message}");
                }
            }
            finally
            {
                concurrencyGate.Release();
            }
        });

        await Task.WhenAll(tasks);

        // Intercalar resultados equitativamente entre todas las páginas activas (round-robin)
        var combined = new List<VideoResult>();
        var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int maxCount = perSiteResults.Length > 0 ? perSiteResults.Max(l => l?.Count ?? 0) : 0;
        for (int i = 0; i < maxCount; i++)
        {
            for (int s = 0; s < perSiteResults.Length; s++)
            {
                var list = perSiteResults[s];
                if (list != null && i < list.Count)
                {
                    var item = list[i];
                    if (seenUrls.Add(item.VideoPageUrl))
                    {
                        combined.Add(item);
                    }
                }
            }
        }

        return (combined, warnings);
    }

    private async Task<List<VideoResult>> SearchSingleSiteAsync(SavedSite site, string query, CancellationToken ct)
    {
        var root = site.RootDomain.ToLowerInvariant();

        if (root is "youtube.com" or "youtu.be")
        {
            var ytResults = await _youTubeProvider.SearchAsync(site, query, ct);
            if (ytResults.Count > 0)
                return ytResults;

            return await _universalProvider.SearchAsync(site, query, ct);
        }

        if (root == "dailymotion.com")
        {
            var dmResults = await _dailymotionProvider.SearchAsync(site, query, ct);
            if (dmResults.Count > 0)
                return dmResults;

            return await _universalProvider.SearchAsync(site, query, ct);
        }

        return await _universalProvider.SearchAsync(site, query, ct);
    }
}
