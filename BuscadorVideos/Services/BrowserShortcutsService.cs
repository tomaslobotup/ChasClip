using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Gestiona la lista de atajos rápidos y personalizados para el buscador web.
/// </summary>
public class BrowserShortcutsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    public BrowserShortcutsService()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BuscadorVideos");
        Directory.CreateDirectory(appDataDir);
        _filePath = Path.Combine(appDataDir, "atajos_navegador.json");
    }

    public List<BrowserShortcut> LoadShortcuts()
    {
        lock (_lock)
        {
            if (File.Exists(_filePath))
            {
                try
                {
                    var json = File.ReadAllText(_filePath);
                    var list = JsonSerializer.Deserialize<List<BrowserShortcut>>(json, JsonOptions);
                    if (list != null && list.Count > 0)
                    {
                        var filtered = list.Where(s => !IsDeprecatedShortcut(s)).ToList();
                        if (filtered.Count != list.Count)
                        {
                            SaveShortcuts(filtered);
                        }
                        if (filtered.Count > 0)
                            return filtered;
                    }
                }
                catch
                {
                }
            }

            var defaults = GetDefaultShortcuts();
            SaveShortcuts(defaults);
            return defaults;
        }
    }

    private static bool IsDeprecatedShortcut(BrowserShortcut s)
    {
        if (s == null) return true;
        var title = s.Title?.ToLowerInvariant() ?? "";
        var url = s.Url?.ToLowerInvariant() ?? "";
        return title.Contains("facebook") || url.Contains("facebook.com") ||
               title.Contains("twitter") || title.Contains("x / twitter") || url.Contains("x.com") || url.Contains("twitter.com") ||
               title.Contains("reddit") || url.Contains("reddit.com");
    }

    public void SaveShortcuts(List<BrowserShortcut> shortcuts)
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(shortcuts, JsonOptions);
                File.WriteAllText(_filePath, json);
            }
            catch
            {
            }
        }
    }

    public static List<BrowserShortcut> GetDefaultShortcuts()
    {
        return
        [
            new BrowserShortcut { Title = "YouTube", Url = "https://www.youtube.com", IconEmoji = "🔴", BadgeHex = "#EF4444" },
            new BrowserShortcut { Title = "Twitch", Url = "https://www.twitch.tv", IconEmoji = "🟣", BadgeHex = "#9146FF" },
            new BrowserShortcut { Title = "TikTok", Url = "https://www.tiktok.com", IconEmoji = "⚫", BadgeHex = "#00E5D4" },
            new BrowserShortcut { Title = "Instagram", Url = "https://www.instagram.com", IconEmoji = "🟠", BadgeHex = "#E1306C" },
            new BrowserShortcut { Title = "Dailymotion", Url = "https://www.dailymotion.com", IconEmoji = "🔷", BadgeHex = "#2F80ED" }
        ];
    }
}
