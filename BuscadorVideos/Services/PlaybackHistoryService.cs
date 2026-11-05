using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Gestiona el historial de videos reproducidos (historial_reproduccion.json).
/// Respeta el modo incógnito (si está activo, no se añade ningún video al historial).
/// </summary>
public class PlaybackHistoryService
{
    private const int MaxHistoryCount = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string FilePath { get; }
    private readonly object _lock = new();

    public PlaybackHistoryService()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BuscadorVideos");
        Directory.CreateDirectory(appDataDir);
        FilePath = Path.Combine(appDataDir, "historial_reproduccion.json");
    }

    public List<PlaybackHistoryItem> LoadHistory()
    {
        lock (_lock)
        {
            if (File.Exists(FilePath))
            {
                try
                {
                    var json = File.ReadAllText(FilePath);
                    var list = JsonSerializer.Deserialize<List<PlaybackHistoryItem>>(json, JsonOptions);
                    if (list != null)
                    {
                        return list
                            .Where(x => !string.IsNullOrWhiteSpace(x.VideoPageUrl))
                            .OrderByDescending(x => x.PlayedAtUtc)
                            .Take(MaxHistoryCount)
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

    public void AddEntry(VideoResult video, bool isIncognito = false)
    {
        if (isIncognito || string.IsNullOrWhiteSpace(video.VideoPageUrl)) return;

        lock (_lock)
        {
            var list = LoadHistory();
            list.RemoveAll(x => string.Equals(x.VideoPageUrl, video.VideoPageUrl, StringComparison.OrdinalIgnoreCase));
            list.Insert(0, PlaybackHistoryItem.FromVideoResult(video));

            if (list.Count > MaxHistoryCount)
            {
                list = list.Take(MaxHistoryCount).ToList();
            }

            SaveHistory(list);
        }
    }

    public void RemoveEntry(string videoUrl)
    {
        if (string.IsNullOrWhiteSpace(videoUrl)) return;

        lock (_lock)
        {
            var list = LoadHistory();
            var count = list.RemoveAll(x => string.Equals(x.VideoPageUrl, videoUrl, StringComparison.OrdinalIgnoreCase));
            if (count > 0)
            {
                SaveHistory(list);
            }
        }
    }

    public void ClearHistory()
    {
        lock (_lock)
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    File.Delete(FilePath);
                }
            }
            catch
            {
            }
        }
    }

    private void SaveHistory(List<PlaybackHistoryItem> items)
    {
        try
        {
            var json = JsonSerializer.Serialize(items, JsonOptions);
            File.WriteAllText(FilePath, json);
        }
        catch
        {
            // Ignorar errores transitorios de E/S
        }
    }
}
