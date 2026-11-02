using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Administra la configuración general de la aplicación y el historial persistente de búsquedas.
/// </summary>
public class AppSettingsService
{
    private const int MaxHistoryEntries = 30;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string AppDataDir { get; }
    public string SettingsFilePath { get; }
    public string HistoryFilePath { get; }

    private readonly object _lock = new();

    public AppSettingsService()
    {
        AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BuscadorVideos");

        Directory.CreateDirectory(AppDataDir);
        SettingsFilePath = Path.Combine(AppDataDir, "configuracion.json");
        HistoryFilePath = Path.Combine(AppDataDir, "historial_busquedas.json");
    }

    public AppSettings LoadSettings()
    {
        lock (_lock)
        {
            if (File.Exists(SettingsFilePath))
            {
                try
                {
                    var json = File.ReadAllText(SettingsFilePath);
                    var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                    if (loaded != null)
                        return loaded;
                }
                catch
                {
                    // Regenerar ajustes limpios si el archivo estuviera corrupto
                }
            }

            var defaults = new AppSettings();
            SaveSettings(defaults);
            return defaults;
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        lock (_lock)
        {
            try
            {
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                File.WriteAllText(SettingsFilePath, json);
            }
            catch
            {
                // Ignorar errores transitorios de E/S
            }
        }
    }

    public List<SearchHistoryEntry> LoadSearchHistory()
    {
        lock (_lock)
        {
            if (File.Exists(HistoryFilePath))
            {
                try
                {
                    var json = File.ReadAllText(HistoryFilePath);
                    var list = JsonSerializer.Deserialize<List<SearchHistoryEntry>>(json, JsonOptions);
                    if (list != null)
                    {
                        return list
                            .Where(x => !string.IsNullOrWhiteSpace(x.Query))
                            .OrderByDescending(x => x.SearchedAtUtc)
                            .Take(MaxHistoryEntries)
                            .ToList();
                    }
                }
                catch
                {
                    // Ignorar archivo corrupto
                }
            }

            return [];
        }
    }

    public void SaveSearchHistory(IEnumerable<SearchHistoryEntry> entries)
    {
        lock (_lock)
        {
            try
            {
                var list = entries
                    .Where(x => !string.IsNullOrWhiteSpace(x.Query))
                    .Take(MaxHistoryEntries)
                    .ToList();
                var json = JsonSerializer.Serialize(list, JsonOptions);
                File.WriteAllText(HistoryFilePath, json);
            }
            catch
            {
                // Ignorar errores transitorios de E/S
            }
        }
    }
}
