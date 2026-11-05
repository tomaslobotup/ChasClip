using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Gestiona la persistencia de los videos marcados como favoritos por el usuario (favoritos.json).
/// </summary>
public class FavoritesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string FilePath { get; }
    private readonly object _lock = new();

    public FavoritesService()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BuscadorVideos");
        Directory.CreateDirectory(appDataDir);
        FilePath = Path.Combine(appDataDir, "favoritos.json");
    }

    public List<FavoriteVideoItem> LoadFavorites()
    {
        lock (_lock)
        {
            if (File.Exists(FilePath))
            {
                try
                {
                    var json = File.ReadAllText(FilePath);
                    var list = JsonSerializer.Deserialize<List<FavoriteVideoItem>>(json, JsonOptions);
                    if (list != null)
                    {
                        return list
                            .Where(x => !string.IsNullOrWhiteSpace(x.VideoPageUrl))
                            .OrderByDescending(x => x.AddedAt)
                            .ToList();
                    }
                }
                catch
                {
                    // Ignorar archivo corrupto y devolver lista vacía
                }
            }
            return [];
        }
    }

    public bool IsFavorite(string? videoUrl)
    {
        if (string.IsNullOrWhiteSpace(videoUrl)) return false;
        var list = LoadFavorites();
        return list.Any(x => string.Equals(x.VideoPageUrl, videoUrl, StringComparison.OrdinalIgnoreCase));
    }

    public bool ToggleFavorite(VideoResult video)
    {
        if (string.IsNullOrWhiteSpace(video.VideoPageUrl)) return false;

        lock (_lock)
        {
            var list = LoadFavorites();
            var existingIndex = list.FindIndex(x => string.Equals(x.VideoPageUrl, video.VideoPageUrl, StringComparison.OrdinalIgnoreCase));

            bool isNowFavorite;
            if (existingIndex >= 0)
            {
                list.RemoveAt(existingIndex);
                isNowFavorite = false;
            }
            else
            {
                list.Insert(0, FavoriteVideoItem.FromVideoResult(video));
                isNowFavorite = true;
            }

            SaveFavorites(list);
            return isNowFavorite;
        }
    }

    public void AddFavorite(VideoResult video)
    {
        if (string.IsNullOrWhiteSpace(video.VideoPageUrl)) return;

        lock (_lock)
        {
            var list = LoadFavorites();
            var existing = list.FirstOrDefault(x => string.Equals(x.VideoPageUrl, video.VideoPageUrl, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                list.Remove(existing);
            }
            list.Insert(0, FavoriteVideoItem.FromVideoResult(video));
            SaveFavorites(list);
        }
    }

    public void RemoveFavorite(string videoUrl)
    {
        if (string.IsNullOrWhiteSpace(videoUrl)) return;

        lock (_lock)
        {
            var list = LoadFavorites();
            var count = list.RemoveAll(x => string.Equals(x.VideoPageUrl, videoUrl, StringComparison.OrdinalIgnoreCase));
            if (count > 0)
            {
                SaveFavorites(list);
            }
        }
    }

    public void ClearFavorites()
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

    private void SaveFavorites(List<FavoriteVideoItem> items)
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
