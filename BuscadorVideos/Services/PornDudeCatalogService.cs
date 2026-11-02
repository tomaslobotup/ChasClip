using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Administra las colecciones NSFW verificadas de video directo compatibles con el buscador universal,
/// excluyendo portales de series/hentai episódico que impiden la reproducción limpia.
/// </summary>
public class PornDudeCatalogService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string CacheFilePath { get; }
    private readonly object _lock = new();

    public PornDudeCatalogService()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BuscadorVideos");
        Directory.CreateDirectory(dir);
        CacheFilePath = Path.Combine(dir, "nsfw_categorias.json");
    }

    public List<NsfwCategory> LoadCachedCategories()
    {
        lock (_lock)
        {
            var curated = BuildVerifiedCollections();

            if (File.Exists(CacheFilePath))
            {
                try
                {
                    var json = File.ReadAllText(CacheFilePath);
                    var saved = JsonSerializer.Deserialize<List<NsfwCategory>>(json, JsonOptions) ?? [];
                    var enabledDomains = saved
                        .Where(c => !c.Name.Contains("Hentai", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(c => c.Sites)
                        .Where(s => s.IsEnabled)
                        .Select(s => s.RootDomain)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase);

                    foreach (var cat in curated)
                    {
                        foreach (var site in cat.Sites)
                            site.IsEnabled = enabledDomains.Contains(site.RootDomain);
                        cat.RefreshSelectionState();
                    }
                }
                catch
                {
                    // Usar colecciones limpias por defecto si el archivo previo estaba corrupto
                }
            }

            SaveCategories(curated);
            return curated;
        }
    }

    public void SaveCategories(IEnumerable<NsfwCategory> categories)
    {
        lock (_lock)
        {
            var json = JsonSerializer.Serialize(categories.ToList(), JsonOptions);
            File.WriteAllText(CacheFilePath, json);
        }
    }

    private static List<NsfwCategory> BuildVerifiedCollections()
    {
        var pornoGratis = CreateCategory("Videos Adultos (Directos)",
        [
            ("Pornhub", "https://es.pornhub.com/video/search?search={q}"),
            ("XVideos", "https://www.xvideos.com/?k={q}"),
            ("XNXX", "https://www.xnxx.com/search/{q}"),
            ("xHamster", "https://es.xhamster.com/search/{q}"),
            ("RedTube", "https://es.redtube.com/?search={q}"),
            ("YouPorn", "https://www.youporn.com/search/?query={q}"),
            ("Eporner", "https://www.eporner.com/search/{q}/"),
            ("Tube8", "https://es.tube8.com/searches.html?q={q}"),
            ("PornTrex", "https://www.porntrex.com/search/{q}/")
        ]);

        return [pornoGratis];
    }

    private static NsfwCategory CreateCategory(string name, (string Name, string SearchUrl)[] entries)
    {
        var sites = new ObservableCollection<SavedSite>();
        foreach (var (siteName, url) in entries)
        {
            var site = SavedSite.Create(siteName, url);
            site.IsEnabled = false;
            sites.Add(site);
        }

        var category = new NsfwCategory
        {
            Name = name,
            IsExpanded = true,
            Sites = sites
        };
        category.AttachSiteListeners();
        category.RefreshSelectionState();
        return category;
    }
}
