using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Motor de navegador Chromium real (WebView2 en segundo plano con resolución de escritorio 1280x800)
/// capaz de buscar interactivamente en cualquier página web y extraer sus tarjetas de video renderizadas.
/// </summary>
public interface IBrowserScraper
{
    Task<string?> NavigateAndRunScriptAsync(string url, string waitSelectorJs, string extractScriptJs, int timeoutMs = 6000, CancellationToken ct = default);

    /// <summary>
    /// Abre el enlace del sitio en Chromium, salta muros de edad 18+, escribe la búsqueda en el buscador real de la página
    /// (o navega a la búsqueda), ejecuta el envío (GET, POST o redirección JS) y extrae todos los videos del DOM.
    /// </summary>
    Task<List<VideoResult>> InteractiveSiteSearchAsync(SavedSite site, string query, CancellationToken ct = default);
}
