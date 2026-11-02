using System.Text.Json.Serialization;

namespace BuscadorVideos.Models;

/// <summary>
/// Entrada individual del historial desplegable de búsquedas, con soporte para borrado individual o completo.
/// </summary>
public class SearchHistoryEntry
{
    public string Query { get; set; } = string.Empty;
    public DateTime SearchedAtUtc { get; set; } = DateTime.UtcNow;
    public int ResultCount { get; set; }

    [JsonIgnore]
    public string MetaText
    {
        get
        {
            var local = SearchedAtUtc.ToLocalTime();
            var span = DateTime.Now - local;
            string when = span.TotalMinutes < 1 ? "ahora"
                : span.TotalMinutes < 60 ? $"hace {(int)span.TotalMinutes} min"
                : span.TotalHours < 24 ? $"hace {(int)span.TotalHours} h"
                : local.ToString("dd/MM HH:mm");

            return ResultCount > 0 ? $"{ResultCount} videos • {when}" : when;
        }
    }
}

/// <summary>
/// Instantánea en memoria de una búsqueda ejecutada para permitir revertir a la búsqueda anterior al instante.
/// </summary>
public class SearchSessionSnapshot
{
    public required string Query { get; init; }
    public required List<VideoResult> AllResults { get; init; }
    public required List<SavedSite> SearchedSites { get; init; }
    public string? SelectedFilterSiteId { get; init; }
    public int CurrentPage { get; init; } = 1;
}
