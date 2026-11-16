using System.Collections.ObjectModel;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Gestiona el historial de navegación y búsqueda en formato de lista para el buscador web.
/// </summary>
public class BrowserHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _filePath;
    private readonly object _lock = new();

    public ObservableCollection<BrowserHistoryItem> Items { get; } = [];

    public BrowserHistoryService()
    {
        var appDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BuscadorVideos");
        Directory.CreateDirectory(appDataDir);
        _filePath = Path.Combine(appDataDir, "historial_navegador.json");

        LoadHistory();
    }

    public void AddEntry(string title, string url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            url.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            url.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var cleanUrl = url.Trim();
        var cleanTitle = string.IsNullOrWhiteSpace(title) ? cleanUrl : title.Trim();

        lock (_lock)
        {
            RunOnUi(() =>
            {
                var first = Items.FirstOrDefault();
                if (first != null && string.Equals(first.Url, cleanUrl, StringComparison.OrdinalIgnoreCase))
                {
                    if (!string.Equals(cleanTitle, cleanUrl, StringComparison.OrdinalIgnoreCase) ||
                        string.IsNullOrWhiteSpace(first.Title))
                    {
                        first.Title = cleanTitle;
                    }
                    first.VisitedAt = DateTime.Now;
                    return;
                }

                var existing = Items.FirstOrDefault(x => string.Equals(x.Url, cleanUrl, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    if (string.Equals(cleanTitle, cleanUrl, StringComparison.OrdinalIgnoreCase) &&
                        !string.IsNullOrWhiteSpace(existing.Title) &&
                        !string.Equals(existing.Title, cleanUrl, StringComparison.OrdinalIgnoreCase))
                    {
                        cleanTitle = existing.Title;
                    }
                    Items.Remove(existing);
                }

                Items.Insert(0, new BrowserHistoryItem
                {
                    Title = cleanTitle,
                    Url = cleanUrl,
                    VisitedAt = DateTime.Now
                });

                while (Items.Count > 300)
                {
                    Items.RemoveAt(Items.Count - 1);
                }
            });

            SaveHistory();
        }
    }

    public void RemoveEntry(BrowserHistoryItem item)
    {
        lock (_lock)
        {
            RunOnUi(() => Items.Remove(item));
            SaveHistory();
        }
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            RunOnUi(() => Items.Clear());
            SaveHistory();
        }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_filePath)) return;

            var json = File.ReadAllText(_filePath);
            var loaded = JsonSerializer.Deserialize<List<BrowserHistoryItem>>(json, JsonOptions);
            if (loaded != null && loaded.Count > 0)
            {
                Items.Clear();
                foreach (var it in loaded)
                {
                    Items.Add(it);
                }
            }
        }
        catch
        {
            // Fallback silencioso si el archivo está bloqueado o corrupto
        }
    }

    private void SaveHistory()
    {
        try
        {
            var list = Items.ToList();
            var json = JsonSerializer.Serialize(list, JsonOptions);
            File.WriteAllText(_filePath, json);
        }
        catch
        {
            // Evitar crash por IO temporal
        }
    }

    private static void RunOnUi(Action action)
    {
        if (Application.Current?.Dispatcher != null && !Application.Current.Dispatcher.CheckAccess())
        {
            Application.Current.Dispatcher.Invoke(action);
        }
        else
        {
            action();
        }
    }
}
