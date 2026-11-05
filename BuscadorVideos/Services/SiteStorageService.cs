using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Servicio encargado de cargar, restaurar y guardar automáticamente la lista editable de buscadores de video del usuario.
/// Incluye una lista predeterminada de plataformas de video verificadas y funcionales.
/// </summary>
public class SiteStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string StorageFilePath { get; }
    private readonly string _localMirrorPath;
    private readonly object _syncLock = new();

    public SiteStorageService()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BuscadorVideos");

        Directory.CreateDirectory(appDataDir);
        StorageFilePath = Path.Combine(appDataDir, "paginas_guardadas.json");
        _localMirrorPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "paginas_guardadas.json");
    }

    /// <summary>
    /// Crea la lista predeterminada de buscadores de video probados y funcionales.
    /// Plataformas permitidas: YouTube, Twitch, TikTok, Instagram y Dailymotion.
    /// </summary>
    public static List<SavedSite> CreateDefaultVerifiedSites()
    {
        var youtube = SavedSite.Create("YouTube", "https://www.youtube.com");
        var twitch = SavedSite.Create("Twitch", "https://www.twitch.tv");
        var tiktok = SavedSite.Create("TikTok", "https://www.tiktok.com");
        var instagram = SavedSite.Create("Instagram", "https://www.instagram.com");
        var dailymotion = SavedSite.Create("Dailymotion", "https://www.dailymotion.com");

        youtube.IsEnabled = true;
        twitch.IsEnabled = true;
        tiktok.IsEnabled = true;
        instagram.IsEnabled = true;
        dailymotion.IsEnabled = true;

        return [youtube, twitch, tiktok, instagram, dailymotion];
    }

    public List<SavedSite> LoadSites(bool forceSeedIfEmpty = false)
    {
        lock (_syncLock)
        {
            string? pathToRead = null;
            if (File.Exists(StorageFilePath))
                pathToRead = StorageFilePath;
            else if (File.Exists(_localMirrorPath))
                pathToRead = _localMirrorPath;

            if (pathToRead != null)
            {
                try
                {
                    var json = File.ReadAllText(pathToRead);
                    var sites = JsonSerializer.Deserialize<List<SavedSite>>(json, JsonOptions);
                    if (sites != null && (!forceSeedIfEmpty || sites.Count > 0))
                    {
                        // Limpiar plataformas obsoletas no solicitadas (Vimeo, Bilibili, Rumble, Reddit)
                        bool modified = sites.RemoveAll(s =>
                        {
                            var r = s.RootDomain.ToLowerInvariant();
                            return r is "vimeo.com" or "bilibili.com" or "rumble.com" or "reddit.com";
                        }) > 0;

                        // Asegurar que Instagram esté presente
                        if (!sites.Any(s => s.RootDomain.Equals("instagram.com", StringComparison.OrdinalIgnoreCase)))
                        {
                            var insta = SavedSite.Create("Instagram", "https://www.instagram.com");
                            insta.IsEnabled = true;
                            sites.Add(insta);
                            modified = true;
                        }

                        // Asegurar que las plataformas permitidas principales existan
                        var defaults = CreateDefaultVerifiedSites();
                        foreach (var def in defaults)
                        {
                            if (!sites.Any(s => s.RootDomain.Equals(def.RootDomain, StringComparison.OrdinalIgnoreCase)))
                            {
                                sites.Add(def);
                                modified = true;
                            }
                        }

                        if (modified)
                        {
                            SaveSites(sites);
                        }

                        return sites;
                    }
                }
                catch
                {
                    // Si el archivo estuviera corrupto, regeneramos valores iniciales
                }
            }

            var newDefaults = CreateDefaultVerifiedSites();
            SaveSites(newDefaults);
            return newDefaults;
        }
    }

    public void SaveSites(IEnumerable<SavedSite> sites)
    {
        lock (_syncLock)
        {
            var list = sites.ToList();
            var json = JsonSerializer.Serialize(list, JsonOptions);

            File.WriteAllText(StorageFilePath, json);

            try
            {
                File.WriteAllText(_localMirrorPath, json);
            }
            catch
            {
                // Ignorar si el directorio del ejecutable es de solo lectura
            }
        }
    }
}
