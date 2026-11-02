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
    /// </summary>
    public static List<SavedSite> CreateDefaultVerifiedSites()
    {
        var youtube = SavedSite.Create("YouTube", "https://www.youtube.com");
        var dailymotion = SavedSite.Create("Dailymotion", "https://www.dailymotion.com");
        var tiktok = SavedSite.Create("TikTok", "https://www.tiktok.com");
        var vimeo = SavedSite.Create("Vimeo", "https://vimeo.com");
        var bilibili = SavedSite.Create("Bilibili", "https://www.bilibili.com");
        var rumble = SavedSite.Create("Rumble", "https://rumble.com");
        var reddit = SavedSite.Create("Reddit Videos", "https://www.reddit.com");
        var twitch = SavedSite.Create("Twitch", "https://www.twitch.tv");

        // Dejar activas las plataformas principales de respuesta rápida y disponibles las adicionales a un clic
        youtube.IsEnabled = true;
        dailymotion.IsEnabled = true;
        tiktok.IsEnabled = true;
        vimeo.IsEnabled = true;
        bilibili.IsEnabled = false;
        rumble.IsEnabled = false;
        reddit.IsEnabled = false;
        twitch.IsEnabled = false;

        return [youtube, dailymotion, tiktok, vimeo, bilibili, rumble, reddit, twitch];
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
                        return sites;
                }
                catch
                {
                    // Si el archivo estuviera corrupto, regeneramos valores iniciales
                }
            }

            var defaults = CreateDefaultVerifiedSites();
            SaveSites(defaults);
            return defaults;
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
