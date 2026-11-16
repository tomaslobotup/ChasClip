namespace BuscadorVideos.Models;

/// <summary>
/// Elemento de selección de página para la barra de paginación inferior (12 videos por página en cuadrícula de 3x4).
/// </summary>
public class PageSelectorItem
{
    public int PageNumber { get; init; }
    public string Label { get; init; } = string.Empty;
    public bool IsCurrent { get; init; }
    public bool IsEllipsis { get; init; }
}

/// <summary>
/// Filtro rápido por página en los resultados de búsqueda.
/// </summary>
public class SiteFilterChip
{
    public string? SiteId { get; init; }
    public string Label { get; init; } = string.Empty;
    public int Count { get; init; }
    public bool IsSelected { get; init; }
    public string AccentHex { get; init; } = Services.AppThemeService.PaletteAccentHex;
}
