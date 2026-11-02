using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace BuscadorVideos.Services;

/// <summary>
/// Cliente HTTP compartido con descompresión automática, soporte UTF-8 completo,
/// negociación de imágenes sin AVIF (compatible 100% con SkiaSharp/WPF) y cookies universales 18+.
/// </summary>
public static class HttpHelper
{
    public const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0";

    /// <summary>
    /// Cookies universales que evitan muros/redirecciones de mayoría de edad (18+) en portales de video.
    /// </summary>
    public const string UniversalAgeBypassCookies =
        "age_verified=1; accessAgeDisclaimerPH=1; accessAgeDisclaimerUK=1; accessPH=1; over18=1; age_gate=1; " +
        "is_adult=1; disclaimer=1; mature=1; hasVisited=1; platform=pc; parental-control=no; splashWeb=1; " +
        "age_check=1; verified=1; confirm_age=1; warning_accepted=1; rta_accepted=1";

    public static HttpClient SharedClient { get; } = CreateStandardClient();

    /// <summary>
    /// Cliente sin CookieContainer intermedio para enviar explícitamente las cookies de mayoría de edad a cualquier dominio.
    /// </summary>
    public static HttpClient DirectSiteClient { get; } = CreateDirectAgeBypassClient();

    /// <summary>
    /// Cliente dedicado para miniaturas y clips de preview: solicita explícitamente WebP/JPEG/PNG/GIF
    /// (excluyendo image/avif e image/* para evitar que CDNs como ytimg/phncdn/vimeocdn devuelvan AVIF).
    /// </summary>
    public static HttpClient MediaClient { get; } = CreateMediaClient();

    private static HttpClient CreateStandardClient()
    {
        var cookieContainer = new CookieContainer();
        cookieContainer.Add(new Uri("https://www.youtube.com"), new Cookie("SOCS", "CAISHAgBEhJnd3NfMjAyNDA1MTUtMF9SQzIaAmVzIAEaBgiAo_WzBg", "/", ".youtube.com"));
        cookieContainer.Add(new Uri("https://www.youtube.com"), new Cookie("CONSENT", "YES+cb.20240116-07-p0.es+FX+999", "/", ".youtube.com"));
        cookieContainer.Add(new Uri("https://www.youtube.com"), new Cookie("PREF", "f2=8000000", "/", ".youtube.com"));
        cookieContainer.Add(new Uri("https://www.bing.com"), new Cookie("SRCHHPGUSR", "ADLT=OFF&NRSLT=50", "/", ".bing.com"));
        cookieContainer.Add(new Uri("https://www.reddit.com"), new Cookie("over18", "1", "/", ".reddit.com"));

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            CookieContainer = cookieContainer,
            UseCookies = true,
            AllowAutoRedirect = true
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/jpeg,image/png,*/*;q=0.8");

        return client;
    }

    private static HttpClient CreateDirectAgeBypassClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            AllowAutoRedirect = true
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(18)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,image/jpeg,image/png,*/*;q=0.8");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
        client.DefaultRequestHeaders.TryAddWithoutValidation("Sec-Fetch-User", "?1");

        return client;
    }

    private static HttpClient CreateMediaClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            UseCookies = false,
            AllowAutoRedirect = true
        };

        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        client.DefaultRequestHeaders.UserAgent.ParseAdd(BrowserUserAgent);
        // Importante: NO incluir image/avif ni image/* para que CDNs modernos negocien siempre WebP/JPEG/PNG
        client.DefaultRequestHeaders.Accept.ParseAdd("image/webp,image/jpeg,image/png,image/gif;q=0.9,video/mp4,video/webm;q=0.8");

        return client;
    }

    /// <summary>
    /// Descarga texto garantizando codificación UTF-8 para preservar títulos en cualquier idioma.
    /// </summary>
    public static async Task<string> GetUtf8StringAsync(string url, string? referer = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
            req.Headers.Referrer = refUri;

        using var resp = await SharedClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Descarga HTML o manifiestos enviando cookies opcionales o universales de verificación de edad (18+).
    /// </summary>
    public static async Task<string> GetUtf8StringWithAgeBypassAsync(
        string url,
        string? referer = null,
        CancellationToken ct = default,
        string? customCookies = null)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("Cookie", string.IsNullOrWhiteSpace(customCookies) ? UniversalAgeBypassCookies : customCookies);

        if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
            req.Headers.Referrer = refUri;
        else if (Uri.TryCreate(url, UriKind.Absolute, out var selfUri))
            req.Headers.Referrer = new Uri($"{selfUri.Scheme}://{selfUri.Host}/");

        using var resp = await DirectSiteClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>
    /// Descarga bytes de miniaturas o clips con reintento inteligente de Referer para evitar bloqueos anti-hotlink (403/400).
    /// </summary>
    public static async Task<byte[]> GetBytesAsync(string url, string? referer = null, CancellationToken ct = default)
    {
        if (url.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
        {
            int commaIdx = url.IndexOf(',');
            if (commaIdx > 0 && commaIdx + 1 < url.Length)
                return Convert.FromBase64String(url[(commaIdx + 1)..]);
        }

        Uri? primaryRef = null;
        if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var parsedRef))
            primaryRef = parsedRef;

        Uri? selfOriginRef = null;
        if (Uri.TryCreate(url, UriKind.Absolute, out var mediaUri))
            selfOriginRef = new Uri($"{mediaUri.Scheme}://{mediaUri.Host}/");

        // Construir secuencia de intentos de Referer sin duplicados: 1) Referer de página -> 2) Origen propio del CDN -> 3) Sin Referer
        var refererAttempts = new List<Uri?>();
        if (primaryRef != null) refererAttempts.Add(primaryRef);
        if (selfOriginRef != null && (primaryRef == null || primaryRef.Host != selfOriginRef.Host))
            refererAttempts.Add(selfOriginRef);
        refererAttempts.Add(null);

        Exception? lastError = null;
        foreach (var candidateRef in refererAttempts)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (candidateRef != null)
                    req.Headers.Referrer = candidateRef;

                using var resp = await MediaClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (!resp.IsSuccessStatusCode)
                {
                    // Si es 404 definitivo no tiene sentido reintentar con otro Referer
                    if (resp.StatusCode == HttpStatusCode.NotFound)
                        resp.EnsureSuccessStatusCode();

                    lastError = new HttpRequestException($"HTTP {(int)resp.StatusCode}");
                    continue;
                }

                return await resp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                if (ex is HttpRequestException { StatusCode: HttpStatusCode.NotFound })
                    break;
            }
        }

        throw lastError ?? new HttpRequestException("No se pudo descargar el recurso multimedia.");
    }

    /// <summary>
    /// Descarga un archivo de video por streaming directo a disco reportando progreso porcentual.
    /// </summary>
    public static async Task DownloadFileWithProgressAsync(
        string url,
        string destinationPath,
        string? referer,
        string? cookies,
        IProgress<double>? progress,
        CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Clear();
        req.Headers.Accept.ParseAdd("*/*");

        if (!string.IsNullOrWhiteSpace(cookies))
            req.Headers.TryAddWithoutValidation("Cookie", cookies);
        else
            req.Headers.TryAddWithoutValidation("Cookie", UniversalAgeBypassCookies);

        if (!string.IsNullOrWhiteSpace(referer) && Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
            req.Headers.Referrer = refUri;

        using var resp = await MediaClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long? totalBytes = resp.Content.Headers.ContentLength;
        await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
        await using var fileStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
        int read;
        while ((read = await netStream.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            totalRead += read;

            if (totalBytes is > 0)
            {
                double pct = Math.Clamp(totalRead * 100.0 / totalBytes.Value, 0, 100);
                progress?.Report(pct);
            }
        }

        progress?.Report(100);
    }
}
