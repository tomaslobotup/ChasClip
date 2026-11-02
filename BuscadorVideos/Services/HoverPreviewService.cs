using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using BuscadorVideos.Models;
using SkiaSharp;

namespace BuscadorVideos.Services;

public record AnimatedFrame(BitmapSource Bitmap, int DelayMs);

public class HoverPreviewPayload
{
    public string? LocalMp4Path { get; init; }
    public IReadOnlyList<AnimatedFrame>? Frames { get; init; }
}

/// <summary>
/// Servicio de carga y caché en segundo plano para portadas (JPG/PNG/WebP/GIF + respaldo WIC) y vistas previas al pasar el mouse
/// (clips de video MP4 silenciosos, WebP/GIF animados fotograma a fotograma con SkiaSharp, tiras de fotogramas y escenas).
/// </summary>
public static class HoverPreviewService
{
    private static readonly ConcurrentDictionary<string, BitmapSource> ThumbnailCache = new();
    private static readonly ConcurrentDictionary<string, IReadOnlyList<AnimatedFrame>> FramesCache = new();
    private static readonly string TempPreviewDir;

    static HoverPreviewService()
    {
        TempPreviewDir = Path.Combine(Path.GetTempPath(), "BuscadorVideos_Previews");
        Directory.CreateDirectory(TempPreviewDir);
        _ = Task.Run(CleanupStaleTempFiles);
    }

    private static void CleanupStaleTempFiles()
    {
        try
        {
            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var file in new DirectoryInfo(TempPreviewDir).EnumerateFiles())
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    try { file.Delete(); } catch { }
                }
            }
        }
        catch
        {
            // Ignorar si algún archivo temporal sigue en uso
        }
    }

    /// <summary>
    /// Carga la mejor miniatura disponible para un resultado de video, probando automáticamente URLs de respaldo
    /// (por ejemplo hqdefault.jpg o fotogramas secundarios) si la miniatura principal falla o devuelve 404.
    /// </summary>
    public static async Task<BitmapSource?> LoadBestThumbnailAsync(VideoResult video, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(video.ThumbnailUrl))
        {
            var primary = await LoadThumbnailAsync(video.ThumbnailUrl, video.VideoPageUrl, ct);
            if (primary != null)
                return primary;
        }

        foreach (var fallbackUrl in video.FallbackFrameUrls)
        {
            if (ct.IsCancellationRequested) break;
            if (string.IsNullOrWhiteSpace(fallbackUrl)) continue;

            var fallback = await LoadThumbnailAsync(fallbackUrl, video.VideoPageUrl, ct);
            if (fallback != null)
                return fallback;
        }

        return null;
    }

    /// <summary>
    /// Descarga y decodifica una portada estática de forma segura (verificando SKCodec antes de decodificar
    /// y respaldándose en Windows Imaging Component si el formato requiere códec del sistema).
    /// </summary>
    public static async Task<BitmapSource?> LoadThumbnailAsync(string url, string? refererUrl = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;

        if (ThumbnailCache.TryGetValue(url, out var cached))
            return cached;

        try
        {
            var effectiveReferer = ResolveBestReferer(url, refererUrl);
            var bytes = await HttpHelper.GetBytesAsync(url, effectiveReferer, ct);
            if (bytes.Length < 64)
                return null;

            var bmp = await Task.Run(() => DecodeStaticImageSafe(bytes), ct);
            if (bmp == null)
                return null;

            if (ThumbnailCache.Count > 360)
                ThumbnailCache.Clear();

            ThumbnailCache[url] = bmp;
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource? DecodeStaticImageSafe(byte[] bytes)
    {
        try
        {
            // Validar SKCodec explícitamente para evitar ArgumentNullException('codec') en SkiaSharp 4.x
            using var stream = new SKMemoryStream(bytes);
            using var codec = SKCodec.Create(stream);
            if (codec != null && codec.Info.Width > 0 && codec.Info.Height > 0)
            {
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var skBmp = new SKBitmap(info);
                var result = codec.GetPixels(info, skBmp.GetPixels());
                if (result is SKCodecResult.Success or SKCodecResult.IncompleteInput)
                {
                    if (skBmp.Width > 640)
                    {
                        int targetH = Math.Max(1, (int)Math.Round(skBmp.Height * (640.0 / skBmp.Width)));
                        var resizedInfo = new SKImageInfo(640, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
                        using var resized = skBmp.Resize(resizedInfo, new SKSamplingOptions(SKFilterMode.Linear));
                        if (resized != null)
                            return ConvertSkBitmapToWpf(resized);
                    }

                    return ConvertSkBitmapToWpf(skBmp);
                }
            }
        }
        catch
        {
            // Continuar al decodificador WIC nativo de Windows
        }

        // Respaldo WIC de Windows (soporta ICO, TIFF, BMP exóticos o códecs AVIF/HEIF instalados en Windows 11)
        try
        {
            using var ms = new MemoryStream(bytes);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.DecodePixelWidth = 640;
            bi.StreamSource = ms;
            bi.EndInit();
            bi.Freeze();
            return bi.PixelWidth > 0 ? bi : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Prepara la mejor vista previa disponible para un video al pasar el mouse sobre su tarjeta.
    /// Prioridad: 1) Clip MP4 en movimiento -> 2) WebP/GIF animado -> 3) Tira de fotogramas -> 4) Escenas inicio/mitad/fin.
    /// </summary>
    public static async Task<HoverPreviewPayload?> PreparePreviewAsync(VideoResult video, CancellationToken ct = default)
    {
        // 1) Clip MP4/WebM de vista previa
        if (!string.IsNullOrWhiteSpace(video.PreviewMp4Url))
        {
            var mp4Path = await DownloadAndCacheMp4Async(video.PreviewMp4Url, video.VideoPageUrl, ct);
            if (!string.IsNullOrWhiteSpace(mp4Path))
            {
                return new HoverPreviewPayload { LocalMp4Path = mp4Path };
            }
        }

        // 2) WebP o GIF animado
        if (!string.IsNullOrWhiteSpace(video.PreviewWebpUrl))
        {
            var animFrames = await LoadAnimatedImageFramesAsync(video.PreviewWebpUrl, video.VideoPageUrl, ct);
            if (animFrames is { Count: > 1 })
            {
                return new HoverPreviewPayload { Frames = animFrames };
            }
        }

        // 3) Tira vertical de fotogramas (ej. Dailymotion filmstrip)
        if (!string.IsNullOrWhiteSpace(video.FilmstripUrl))
        {
            var stripFrames = await LoadFilmstripFramesAsync(video.FilmstripUrl, video.VideoPageUrl, ct);
            if (stripFrames is { Count: > 1 })
            {
                return new HoverPreviewPayload { Frames = stripFrames };
            }
        }

        // 4) Secuencia de escenas del video (ej. hq1.jpg, hq2.jpg, hq3.jpg)
        if (video.FallbackFrameUrls.Count > 0)
        {
            var key = "fallback_" + video.Id;
            if (FramesCache.TryGetValue(key, out var cachedFallback))
                return new HoverPreviewPayload { Frames = cachedFallback };

            var list = new List<AnimatedFrame>();
            if (!string.IsNullOrWhiteSpace(video.ThumbnailUrl))
            {
                var cover = await LoadThumbnailAsync(video.ThumbnailUrl, video.VideoPageUrl, ct);
                if (cover != null)
                    list.Add(new AnimatedFrame(cover, 650));
            }

            foreach (var frameUrl in video.FallbackFrameUrls)
            {
                if (ct.IsCancellationRequested) break;
                if (frameUrl.EndsWith("/hqdefault.jpg", StringComparison.OrdinalIgnoreCase))
                    continue; // Evitar repetir la portada base en la animación de escenas

                var bmp = await LoadThumbnailAsync(frameUrl, video.VideoPageUrl, ct);
                if (bmp != null)
                    list.Add(new AnimatedFrame(bmp, 650));
            }

            if (list.Count > 1)
            {
                FramesCache[key] = list;
                return new HoverPreviewPayload { Frames = list };
            }
        }

        return null;
    }

    private static async Task<string?> DownloadAndCacheMp4Async(string mp4Url, string? pageReferer, CancellationToken ct)
    {
        try
        {
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(mp4Url)))[..20];
            var ext = mp4Url.Contains(".webm", StringComparison.OrdinalIgnoreCase) ? ".webm" : ".mp4";
            var filePath = Path.Combine(TempPreviewDir, $"prev_{hash}{ext}");

            if (File.Exists(filePath) && new FileInfo(filePath).Length > 1024)
                return filePath;

            var effectiveReferer = ResolveBestReferer(mp4Url, pageReferer);
            var bytes = await HttpHelper.GetBytesAsync(mp4Url, effectiveReferer, ct);
            if (bytes.Length < 1024)
                return null;

            await File.WriteAllBytesAsync(filePath, bytes, ct);
            return filePath;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<IReadOnlyList<AnimatedFrame>?> LoadAnimatedImageFramesAsync(string url, string? pageReferer, CancellationToken ct)
    {
        if (FramesCache.TryGetValue(url, out var cached))
            return cached;

        try
        {
            var effectiveReferer = ResolveBestReferer(url, pageReferer);
            var bytes = await HttpHelper.GetBytesAsync(url, effectiveReferer, ct);
            var frames = await Task.Run(() =>
            {
                using var stream = new SKMemoryStream(bytes);
                using var codec = SKCodec.Create(stream);
                if (codec == null || codec.FrameCount <= 1)
                    return null;

                int totalFrames = codec.FrameCount;
                var frameInfos = codec.FrameInfo;
                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);

                var renderedBitmaps = new SKBitmap[totalFrames];
                var output = new List<AnimatedFrame>(totalFrames);
                int emitStep = totalFrames > 60 ? 2 : 1;

                try
                {
                    for (int i = 0; i < totalFrames; i++)
                    {
                        ct.ThrowIfCancellationRequested();

                        var bmp = new SKBitmap(info);
                        renderedBitmaps[i] = bmp;

                        int reqFrame = i < frameInfos.Length ? frameInfos[i].RequiredFrame : -1;
                        SKCodecOptions opts;

                        if (reqFrame >= 0 && reqFrame < i && renderedBitmaps[reqFrame] != null)
                        {
                            renderedBitmaps[reqFrame].CopyTo(bmp);
                            opts = new SKCodecOptions(i, reqFrame);
                        }
                        else
                        {
                            opts = new SKCodecOptions(i);
                        }

                        var res = codec.GetPixels(info, bmp.GetPixels(), opts);
                        if (res != SKCodecResult.Success && res != SKCodecResult.IncompleteInput)
                            continue;

                        if (i % emitStep == 0)
                        {
                            int rawDuration = i < frameInfos.Length ? frameInfos[i].Duration : 75;
                            int delayMs = Math.Clamp((rawDuration > 0 ? rawDuration : 75) * emitStep, 30, 1000);
                            output.Add(new AnimatedFrame(ConvertSkBitmapToWpf(bmp), delayMs));
                        }
                    }
                }
                finally
                {
                    foreach (var b in renderedBitmaps)
                        b?.Dispose();
                }

                return output.Count > 1 ? (IReadOnlyList<AnimatedFrame>)output : null;
            }, ct);

            if (frames is { Count: > 1 })
            {
                if (FramesCache.Count > 40)
                    FramesCache.Clear();

                FramesCache[url] = frames;
                return frames;
            }
        }
        catch
        {
            // Usar mecanismo de respaldo si la animación falla
        }

        return null;
    }

    private static async Task<IReadOnlyList<AnimatedFrame>?> LoadFilmstripFramesAsync(string url, string? pageReferer, CancellationToken ct)
    {
        if (FramesCache.TryGetValue(url, out var cached))
            return cached;

        try
        {
            var bytes = await HttpHelper.GetBytesAsync(url, ResolveBestReferer(url, pageReferer), ct);
            var frames = await Task.Run(() =>
            {
                using var stream = new SKMemoryStream(bytes);
                using var codec = SKCodec.Create(stream);
                if (codec == null || codec.Info.Width <= 0 || codec.Info.Height <= 0)
                    return null;

                var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var skBmp = new SKBitmap(info);
                if (codec.GetPixels(info, skBmp.GetPixels()) is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                    return null;

                int sliceHeight = 60;
                if (skBmp.Height < sliceHeight * 2)
                    sliceHeight = Math.Max(1, (int)Math.Round(skBmp.Width * 9.0 / 16.0));

                int count = skBmp.Height / sliceHeight;
                if (count <= 1)
                    return null;

                var list = new List<AnimatedFrame>(count);
                for (int i = 0; i < count; i++)
                {
                    using var subset = new SKBitmap();
                    if (skBmp.ExtractSubset(subset, new SKRectI(0, i * sliceHeight, skBmp.Width, (i + 1) * sliceHeight)))
                    {
                        using var bgra = new SKBitmap(subset.Width, subset.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                        if (subset.CopyTo(bgra, SKColorType.Bgra8888))
                            list.Add(new AnimatedFrame(ConvertSkBitmapToWpf(bgra), 320));
                    }
                }

                return list.Count > 1 ? (IReadOnlyList<AnimatedFrame>)list : null;
            }, ct);

            if (frames is { Count: > 1 })
            {
                FramesCache[url] = frames;
                return frames;
            }
        }
        catch
        {
            // Ignorar error de filmstrip
        }

        return null;
    }

    private static string? ResolveBestReferer(string mediaUrl, string? pageReferer)
    {
        if (mediaUrl.Contains("bing.net", StringComparison.OrdinalIgnoreCase) ||
            mediaUrl.Contains("bing.com", StringComparison.OrdinalIgnoreCase))
            return "https://www.bing.com/";

        if (mediaUrl.Contains("ytimg.com", StringComparison.OrdinalIgnoreCase) ||
            mediaUrl.Contains("youtube.com", StringComparison.OrdinalIgnoreCase))
            return "https://www.youtube.com/";

        if (mediaUrl.Contains("dmcdn.net", StringComparison.OrdinalIgnoreCase))
            return "https://www.dailymotion.com/";

        if (mediaUrl.Contains("vimeocdn.com", StringComparison.OrdinalIgnoreCase))
            return "https://vimeo.com/";

        if (!string.IsNullOrWhiteSpace(pageReferer) && Uri.TryCreate(pageReferer, UriKind.Absolute, out var pageUri))
            return $"{pageUri.Scheme}://{pageUri.Host}/";

        return null;
    }

    private static BitmapSource ConvertSkBitmapToWpf(SKBitmap skBmp)
    {
        int stride = skBmp.RowBytes;
        int bufferSize = stride * skBmp.Height;

        var bmp = BitmapSource.Create(
            skBmp.Width,
            skBmp.Height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            skBmp.GetPixels(),
            bufferSize,
            stride);

        bmp.Freeze();
        return bmp;
    }
}
