using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using BuscadorVideos.Models;

namespace BuscadorVideos.Services;

/// <summary>
/// Servicio integral para:
/// 1) Detectar calidades reales de video (MP4 directo, flashvars/mediaDefinitions y variantes HLS #EXT-X-STREAM-INF).
/// 2) Regular la calidad de reproducción en vivo dentro del reproductor WebView2.
/// 3) Descargar el video en la calidad exacta seleccionada por el usuario (MP4 directo con cookies, HLS vía FFmpeg/C# o motor universal yt-dlp).
/// </summary>
public class VideoQualityAndDownloadService
{
    private static readonly Regex ResolutionAttrRegex = new(
        @"RESOLUTION\s*=\s*(\d+)x(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BandwidthAttrRegex = new(
        @"BANDWIDTH\s*=\s*(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HeightFromUrlOrLabelRegex = new(
        @"(?:^|[^0-9])(2160|1440|1080|720|540|480|380|360|240|144)p?(?:[^0-9]|$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex YtDlpPercentRegex = new(
        @"\[download\]\s+(\d{1,3}(?:\.\d+)?)%",
        RegexOptions.Compiled);

    private static readonly Regex YtDlpPlaylistIndexRegex = new(
        @"\[download\]\s+Downloading (?:video|item)\s+(\d+)\s+of\s+(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DmIdRegex = new(
        @"dailymotion\.com/(?:video/|embed/video/|player\.html\?video=)([a-zA-Z0-9]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly string _toolsDir;
    private string? _cachedFfmpegPath;

    public VideoQualityAndDownloadService()
    {
        _toolsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "BuscadorVideos",
            "Tools");
        Directory.CreateDirectory(_toolsDir);
    }

    /// <summary>
    /// Script JS no destructivo para extraer todas las fuentes de video y definiciones de calidad presentes en la página.
    /// </summary>
    public const string ExtractPageVideoStreamsScript = @"
(() => {
    const found = [];
    const add = (url, height, isHls) => {
        if (!url || typeof url !== 'string') return;
        if (url.startsWith('blob:') || url.startsWith('data:')) return;
        try {
            const abs = new URL(url, location.href).href;
            if (!abs.startsWith('http')) return;
            const h = parseInt(height || '0', 10) || 0;
            const hls = Boolean(isHls) || /\.m3u8(\?|$)/i.test(abs);
            found.push({ url: abs, height: h, isHls: hls });
        } catch(e) {}
    };

    try {
        // 1) Etiquetas <video> y <source> (DOM principal y Shadow DOM de web components)
        const scanRoot = (root) => {
            if (!root) return;
            try {
                root.querySelectorAll('video').forEach(v => {
                    if (v.currentSrc) add(v.currentSrc, v.videoHeight || 0, false);
                    if (v.src) add(v.src, v.videoHeight || 0, false);
                    v.querySelectorAll('source').forEach(s => {
                        const q = s.getAttribute('res') || s.getAttribute('size') || s.getAttribute('label') || s.getAttribute('data-quality') || s.getAttribute('title') || '0';
                        const m = String(q).match(/(2160|1440|1080|720|480|360|240)/);
                        add(s.src || s.getAttribute('src'), m ? m[1] : 0, (s.type || '').includes('mpegurl'));
                    });
                });
                root.querySelectorAll('*').forEach(el => {
                    if (el.shadowRoot) scanRoot(el.shadowRoot);
                });
            } catch(e) {}
        };
        scanRoot(document);

        // 2) Reddit <shreddit-player> y sus propiedades / JSON
        document.querySelectorAll('shreddit-player').forEach(sp => {
            const streamUrl = sp.getAttribute('stream-url') || sp.getAttribute('src');
            if (streamUrl) add(streamUrl, 0, streamUrl.includes('.m3u8'));
            const pkg = sp.getAttribute('packaged-media-json');
            if (pkg) {
                try {
                    const parsed = JSON.parse(pkg);
                    if (parsed.playbackMp4Url) add(parsed.playbackMp4Url.url, parsed.playbackMp4Url.height || 0, false);
                    if (parsed.playbackHlsUrl) add(parsed.playbackHlsUrl.url, 0, true);
                } catch(e) {}
            }
        });

        // 2) XVideos / XNXX html5player
        if (window.html5player) {
            if (window.html5player.url_high) add(window.html5player.url_high, 720, false);
            if (window.html5player.url_low) add(window.html5player.url_low, 360, false);
            if (window.html5player.url_hls) add(window.html5player.url_hls, 0, true);
        }

        // 3) Pornhub / RedTube / YouPorn / Tube8 flashvars mediaDefinitions
        for (const k of Object.keys(window)) {
            if (k.startsWith('flashvars_') && window[k] && Array.isArray(window[k].mediaDefinitions)) {
                for (const md of window[k].mediaDefinitions) {
                    if (md && typeof md.videoUrl === 'string' && md.videoUrl.startsWith('http')) {
                        const qVal = Array.isArray(md.quality) ? 0 : parseInt(md.quality || '0', 10);
                        add(md.videoUrl, qVal, md.format === 'hls');
                    }
                }
            }
        }

        // 4) JWPlayer
        if (typeof window.jwplayer === 'function') {
            try {
                const pl = window.jwplayer().getPlaylist?.();
                if (Array.isArray(pl) && pl[0] && Array.isArray(pl[0].sources)) {
                    for (const src of pl[0].sources) {
                        const m = String(src.label || '').match(/(2160|1440|1080|720|480|360|240)/);
                        add(src.file, m ? m[1] : (src.height || 0), (src.type || '').includes('hls'));
                    }
                }
            } catch(e) {}
        }
    } catch(e) {}

    return JSON.stringify(found);
})();";

    /// <summary>
    /// Descubre y clasifica todos los flujos disponibles (MP4 y variantes de resolución HLS .m3u8) para el video actual.
    /// </summary>
    public async Task<List<DiscoveredVideoStream>> DiscoverStreamsAsync(
        VideoResult video,
        string? domStreamsJson,
        IReadOnlyCollection<(string Url, string? Referer)> interceptedUrls,
        string? pageCookies,
        string? pageReferer,
        CancellationToken ct = default)
    {
        var rawCandidates = new List<(string Url, int Height, bool IsHls, string? Referer)>();
        var seenRaw = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddCandidate(string? url, int height, bool isHls, string? referer)
        {
            if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                return;
            if (url.Contains("prev_", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("videopreview", StringComparison.OrdinalIgnoreCase) ||
                url.Contains("mediabook", StringComparison.OrdinalIgnoreCase))
                return;

            if (!seenRaw.Add(url))
                return;

            bool hls = isHls || url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
            int inferredH = height > 0 ? height : InferHeightFromUrl(url);
            rawCandidates.Add((url, inferredH, hls, referer ?? pageReferer ?? video.VideoPageUrl));
        }

        // 1) Streams extraídos del DOM / objetos JS del reproductor
        if (!string.IsNullOrWhiteSpace(domStreamsJson))
        {
            try
            {
                using var doc = JsonDocument.Parse(domStreamsJson);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        var u = el.TryGetProperty("url", out var up) ? up.GetString() : null;
                        int h = el.TryGetProperty("height", out var hp) && hp.TryGetInt32(out var hv) ? hv : 0;
                        bool isHls = el.TryGetProperty("isHls", out var hlsP) && hlsP.ValueKind == JsonValueKind.True;
                        AddCandidate(u, h, isHls, pageReferer);
                    }
                }
            }
            catch
            {
                // Ignorar JSON parcial
            }
        }

        // 2) URLs .m3u8 / .mp4 interceptadas a nivel de red en PlayerWebView
        foreach (var (u, r) in interceptedUrls)
        {
            bool hls = u.Contains(".m3u8", StringComparison.OrdinalIgnoreCase);
            AddCandidate(u, 0, hls, r);
        }

        // 3) API de metadatos de Dailymotion si corresponde
        var dmMatch = DmIdRegex.Match(video.VideoPageUrl);
        if (!dmMatch.Success) dmMatch = DmIdRegex.Match(video.PlayerUrl);
        if (dmMatch.Success && !ct.IsCancellationRequested)
        {
            try
            {
                var metaUrl = $"https://www.dailymotion.com/player/metadata/video/{dmMatch.Groups[1].Value}";
                var metaJson = await HttpHelper.GetUtf8StringAsync(metaUrl, "https://www.dailymotion.com/", ct);
                using var doc = JsonDocument.Parse(metaJson);
                if (doc.RootElement.TryGetProperty("qualities", out var quals) && quals.ValueKind == JsonValueKind.Object)
                {
                    foreach (var qProp in quals.EnumerateObject())
                    {
                        int qHeight = int.TryParse(qProp.Name, out var parsedQ) ? parsedQ : 0;
                        if (qProp.Value.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var entry in qProp.Value.EnumerateArray())
                            {
                                var u = entry.TryGetProperty("url", out var up) ? up.GetString() : null;
                                var type = entry.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                                AddCandidate(u, qHeight, type.Contains("mpegURL", StringComparison.OrdinalIgnoreCase), "https://www.dailymotion.com/");
                            }
                        }
                    }
                }
            }
            catch
            {
                // Continuar con fuentes estándar
            }
        }

        // 4) Expandir manifiestos maestros HLS (#EXT-X-STREAM-INF) en variantes individuales por resolución
        var finalStreams = new List<DiscoveredVideoStream>();
        var seenFinalUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var cand in rawCandidates)
        {
            if (ct.IsCancellationRequested) break;

            if (cand.IsHls)
            {
                var expanded = await TryExpandHlsMasterPlaylistAsync(cand.Url, cand.Referer, pageCookies, ct);
                if (expanded.Count > 0)
                {
                    foreach (var variant in expanded)
                    {
                        if (seenFinalUrls.Add(variant.Url))
                            finalStreams.Add(variant);
                    }
                    continue;
                }
            }

            if (seenFinalUrls.Add(cand.Url))
            {
                finalStreams.Add(new DiscoveredVideoStream
                {
                    Url = cand.Url,
                    Height = cand.Height,
                    IsHls = cand.IsHls,
                    Referer = cand.Referer,
                    Cookies = pageCookies
                });
            }
        }

        return finalStreams
            .OrderByDescending(s => s.Height)
            .ThenBy(s => s.IsHls ? 1 : 0) // Preferir MP4 progresivo sobre HLS a igual resolución
            .ThenByDescending(s => s.Bandwidth)
            .ToList();
    }

    public static DiscoveredVideoStream? SelectBestStreamForQuality(IReadOnlyList<DiscoveredVideoStream> streams, int targetHeight)
    {
        if (streams.Count == 0)
            return null;

        if (targetHeight <= 0)
            return streams[0];

        var withKnownHeight = streams.Where(s => s.Height > 0).ToList();
        if (withKnownHeight.Count > 0)
        {
            var atOrBelow = withKnownHeight
                .Where(s => s.Height <= targetHeight)
                .OrderByDescending(s => s.Height)
                .ThenBy(s => s.IsHls ? 1 : 0)
                .FirstOrDefault();

            if (atOrBelow != null)
                return atOrBelow;

            return withKnownHeight
                .OrderBy(s => Math.Abs(s.Height - targetHeight))
                .First();
        }

        return streams[0];
    }

    /// <summary>
    /// Genera el script JS para cambiar en caliente la calidad del reproductor activo conservando el segundo exacto de reproducción.
    /// </summary>
    public static string BuildApplyPlaybackQualityScript(QualityOption quality, DiscoveredVideoStream? matchedStream)
    {
        var ytCodeJson = JsonSerializer.Serialize(quality.YouTubeQualityCode);
        var targetH = quality.TargetHeight;
        var directMp4Url = matchedStream is { IsHls: false } ? matchedStream.Url : string.Empty;
        var directMp4Json = JsonSerializer.Serialize(directMp4Url);

        return $@"
(() => {{
    try {{
        const targetH = {targetH};
        const ytQ = {ytCodeJson};
        const directMp4 = {directMp4Json};

        // 1) YouTube (#movie_player API oficial interna)
        const ytPlayer = document.getElementById('movie_player');
        if (ytPlayer) {{
            if (targetH <= 0) {{
                if (typeof ytPlayer.setPlaybackQualityRange === 'function') ytPlayer.setPlaybackQualityRange('default', 'highres');
                if (typeof ytPlayer.setPlaybackQuality === 'function') ytPlayer.setPlaybackQuality('default');
                return 'YOUTUBE_AUTO_SET';
            }} else {{
                if (typeof ytPlayer.setPlaybackQualityRange === 'function') ytPlayer.setPlaybackQualityRange(ytQ, ytQ);
                if (typeof ytPlayer.setPlaybackQuality === 'function') ytPlayer.setPlaybackQuality(ytQ);
                return 'YOUTUBE_QUALITY_SET';
            }}
        }}

        // 2) JWPlayer
        if (typeof window.jwplayer === 'function') {{
            try {{
                const jw = window.jwplayer();
                const levels = jw.getQualityLevels?.();
                if (Array.isArray(levels) && levels.length > 1) {{
                    if (targetH <= 0) {{ jw.setCurrentQuality(0); return 'JW_AUTO'; }}
                    let bestIdx = 0;
                    let bestDiff = 99999;
                    levels.forEach((lv, idx) => {{
                        const h = lv.height || parseInt((lv.label || '').replace(/\D/g, ''), 10) || 0;
                        if (h > 0 && Math.abs(h - targetH) < bestDiff) {{
                            bestDiff = Math.abs(h - targetH);
                            bestIdx = idx;
                        }}
                    }});
                    jw.setCurrentQuality(bestIdx);
                    return 'JW_SET';
                }}
            }} catch(e) {{}}
        }}

        // 3) Reproductor HTML5 con URL MP4 directa de la calidad elegida
        const vid = document.querySelector('video');
        if (vid && directMp4 && vid.currentSrc !== directMp4 && !vid.currentSrc.startsWith('blob:')) {{
            const pos = vid.currentTime || 0;
            const wasPaused = vid.paused;
            const rate = vid.playbackRate || 1;
            const vol = vid.volume;
            const muted = vid.muted;
            vid.src = directMp4;
            vid.addEventListener('loadedmetadata', function onMeta() {{
                vid.removeEventListener('loadedmetadata', onMeta);
                if (pos > 0 && pos < vid.duration) vid.currentTime = pos;
                vid.playbackRate = rate;
                vid.volume = vol;
                vid.muted = muted;
                if (!wasPaused) vid.play().catch(() => {{}});
            }});
            vid.load();
            return 'HTML5_SWITCHED';
        }}
    }} catch(e) {{}}
    return 'NO_OP';
}})();";
    }

    /// <summary>
    /// Descarga el video en la calidad seleccionada por el usuario combinando:
    /// Nivel 1: MP4/WebM directo autenticado con cookies de sesión.
    /// Nivel 2: Variante HLS (.m3u8) de la resolución elegida mediante FFmpeg o ensamblador TS/AES-128 nativo en C#.
    /// Nivel 3: Motor universal yt-dlp + FFmpeg (para YouTube DASH 1080p/720p/480p, TikTok, Vimeo, Bilibili, Reddit, Dailymotion, Rumble, etc.).
    /// </summary>
    public async Task DownloadVideoAsync(
        VideoResult video,
        QualityOption selectedQuality,
        IReadOnlyList<DiscoveredVideoStream> discoveredStreams,
        string destinationPath,
        string? pageCookies,
        IProgress<(double Percent, string Status)> progress,
        CancellationToken ct,
        string? cookieFilePath = null)
    {
        bool preferYtDlpFirst = IsPlatformRequiringUniversalExtractor(video.VideoPageUrl);
        var bestStream = SelectBestStreamForQuality(discoveredStreams, selectedQuality.TargetHeight);

        // Si NO es una plataforma DASH/cifrada (como YouTube/TikTok/Bilibili/Reddit/Dailymotion) y tenemos stream directo/HLS:
        if (!preferYtDlpFirst && bestStream != null)
        {
            try
            {
                if (!bestStream.IsHls)
                {
                    progress.Report((2, $"Descargando flujo directo ({bestStream.Label})..."));
                    var byteProgress = new Progress<double>(p =>
                        progress.Report((p, $"Descargando {bestStream.Label}: {p:0}%")));

                    await HttpHelper.DownloadFileWithProgressAsync(
                        bestStream.Url,
                        destinationPath,
                        bestStream.Referer ?? video.VideoPageUrl,
                        bestStream.Cookies ?? pageCookies,
                        byteProgress,
                        ct);

                    if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 10_000)
                        return;
                }
                else
                {
                    progress.Report((3, $"Descargando flujo HLS ({bestStream.Label})..."));
                    bool hlsOk = await DownloadHlsVariantAsync(
                        bestStream,
                        destinationPath,
                        pageCookies,
                        progress,
                        ct);

                    if (hlsOk && File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 10_000)
                        return;
                }
            }
            catch (OperationCanceledException)
            {
                TryDeletePartialFile(destinationPath);
                throw;
            }
            catch
            {
                // Si el enlace directo caducó o requiere muxing, pasar automáticamente al motor universal yt-dlp
                TryDeletePartialFile(destinationPath);
            }
        }

        // Nivel 3: Motor universal yt-dlp (con soporte FFmpeg para unir video+audio en la resolución elegida)
        try
        {
            await DownloadWithYtDlpAsync(video, selectedQuality, destinationPath, pageCookies, cookieFilePath, progress, ct);
            if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 10_000)
                return;
        }
        catch (OperationCanceledException)
        {
            TryDeletePartialFile(destinationPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDeletePartialFile(destinationPath);

            // Reintento 1: Si falló la URL principal pero capturamos un stream directo/HLS en WebView2 (ej. Reddit v.redd.it o Dailymotion m3u8)
            if (bestStream != null && !string.IsNullOrWhiteSpace(bestStream.Url))
            {
                try
                {
                    progress.Report((5, "Intentando descarga vía flujo multimedia directo..."));
                    await DownloadWithYtDlpAsync(video, selectedQuality, destinationPath, pageCookies, cookieFilePath, progress, ct, overrideUrl: bestStream.Url);
                    if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 10_000)
                        return;
                }
                catch { TryDeletePartialFile(destinationPath); }

                // Reintento 2: Descarga HLS nativa o MP4 directa
                try
                {
                    if (bestStream.IsHls)
                    {
                        bool hlsOk = await DownloadHlsVariantAsync(bestStream, destinationPath, pageCookies, progress, ct);
                        if (hlsOk && File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 10_000)
                            return;
                    }
                    else
                    {
                        var byteProgress = new Progress<double>(p => progress.Report((p, $"Descargando: {p:0}%")));
                        await HttpHelper.DownloadFileWithProgressAsync(
                            bestStream.Url,
                            destinationPath,
                            bestStream.Referer ?? video.VideoPageUrl,
                            bestStream.Cookies ?? pageCookies,
                            byteProgress,
                            ct);
                        if (File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 10_000)
                            return;
                    }
                }
                catch { TryDeletePartialFile(destinationPath); }
            }

            var friendlyMessage = HumanizeDownloadErrorMessage(ex.Message, selectedQuality);
            throw new InvalidOperationException(friendlyMessage);
        }
    }

    private async Task<bool> DownloadHlsVariantAsync(
        DiscoveredVideoStream stream,
        string destinationPath,
        string? pageCookies,
        IProgress<(double Percent, string Status)> progress,
        CancellationToken ct)
    {
        var ffmpegPath = FindFfmpegExecutable();
        var referer = stream.Referer ?? "https://www.google.com/";
        var cookies = stream.Cookies ?? pageCookies ?? HttpHelper.UniversalAgeBypassCookies;

        // 1) Si FFmpeg está instalado en el sistema, remuxear limpiamente a contenedor MP4 nativo sin recodificar
        if (!string.IsNullOrWhiteSpace(ffmpegPath))
        {
            var headersArg = $"Referer: {referer}\r\nUser-Agent: {HttpHelper.BrowserUserAgent}\r\nCookie: {cookies}\r\n";
            var psi = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-headers");
            psi.ArgumentList.Add(headersArg);
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(stream.Url);
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add("copy");
            psi.ArgumentList.Add("-bsf:a");
            psi.ArgumentList.Add("aac_adtstoasc");
            psi.ArgumentList.Add(destinationPath);

            using var proc = Process.Start(psi);
            if (proc != null)
            {
                await using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });
                double fakePct = 8;
                var stderrTask = Task.Run(async () =>
                {
                    while (!proc.StandardError.EndOfStream && !ct.IsCancellationRequested)
                    {
                        var line = await proc.StandardError.ReadLineAsync(ct);
                        if (line != null && line.Contains("time=", StringComparison.Ordinal))
                        {
                            fakePct = Math.Min(95, fakePct + 3.5);
                            progress.Report((fakePct, $"Ensamblando MP4 ({stream.Label})..."));
                        }
                    }
                }, ct);

                await proc.WaitForExitAsync(ct);
                try { await stderrTask; } catch { }

                if (proc.ExitCode == 0 && File.Exists(destinationPath) && new FileInfo(destinationPath).Length > 10_000)
                {
                    progress.Report((100, "¡Descarga completada!"));
                    return true;
                }
                TryDeletePartialFile(destinationPath);
            }
        }

        // 2) Respaldo 100% C# puro: descargar y desencriptar segmentos HLS (.ts / fMP4 + AES-128)
        var playlistText = await HttpHelper.GetUtf8StringWithAgeBypassAsync(stream.Url, referer, ct, cookies);
        if (!Uri.TryCreate(stream.Url, UriKind.Absolute, out var baseUri))
            return false;

        var lines = playlistText.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        var segments = new List<(string Url, byte[]? AesKey, byte[]? AesIv)>();
        byte[]? currentKey = null;
        byte[]? currentIv = null;
        string? initMapUrl = null;
        int mediaSequence = 0;

        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
            {
                int.TryParse(line["#EXT-X-MEDIA-SEQUENCE:".Length..].Trim(), out mediaSequence);
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.OrdinalIgnoreCase))
            {
                var m = Regex.Match(line, @"URI=""([^""]+)""", RegexOptions.IgnoreCase);
                if (m.Success && Uri.TryCreate(baseUri, m.Groups[1].Value, out var mapUri))
                    initMapUrl = mapUri.ToString();
            }
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.OrdinalIgnoreCase))
            {
                if (line.Contains("METHOD=NONE", StringComparison.OrdinalIgnoreCase))
                {
                    currentKey = null;
                    currentIv = null;
                }
                else if (line.Contains("METHOD=AES-128", StringComparison.OrdinalIgnoreCase))
                {
                    var uriMatch = Regex.Match(line, @"URI=""([^""]+)""", RegexOptions.IgnoreCase);
                    if (uriMatch.Success && Uri.TryCreate(baseUri, uriMatch.Groups[1].Value, out var keyUri))
                        currentKey = await HttpHelper.GetBytesAsync(keyUri.ToString(), referer, ct);

                    var ivMatch = Regex.Match(line, @"IV=0x([0-9a-fA-F]+)", RegexOptions.IgnoreCase);
                    currentIv = ivMatch.Success ? Convert.FromHexString(ivMatch.Groups[1].Value.PadLeft(32, '0')) : null;
                }
            }
            else if (!line.StartsWith('#') && Uri.TryCreate(baseUri, line, out var segUri))
            {
                segments.Add((segUri.ToString(), currentKey, currentIv));
            }
        }

        if (segments.Count == 0)
            return false;

        await using var outFs = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        if (!string.IsNullOrWhiteSpace(initMapUrl))
        {
            var initBytes = await HttpHelper.GetBytesAsync(initMapUrl, referer, ct);
            await outFs.WriteAsync(initBytes, ct);
        }

        for (int i = 0; i < segments.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (segUrl, aesKey, aesIv) = segments[i];
            var segBytes = await HttpHelper.GetBytesAsync(segUrl, referer, ct);

            if (aesKey is { Length: 16 })
            {
                byte[] iv = aesIv ?? BuildSequenceIv(mediaSequence + i);
                using var aes = Aes.Create();
                aes.Key = aesKey;
                aes.IV = iv;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                using var dec = aes.CreateDecryptor();
                segBytes = dec.TransformFinalBlock(segBytes, 0, segBytes.Length);
            }

            await outFs.WriteAsync(segBytes, ct);
            double pct = (i + 1) * 100.0 / segments.Count;
            progress.Report((pct, $"Descargando segmentos ({i + 1}/{segments.Count})..."));
        }

        return true;
    }

    private async Task DownloadWithYtDlpAsync(
        VideoResult video,
        QualityOption selectedQuality,
        string destinationPath,
        string? pageCookies,
        string? cookieFilePath,
        IProgress<(double Percent, string Status)> progress,
        CancellationToken ct,
        string? overrideUrl = null)
    {
        var ytDlpPath = await EnsureYtDlpExecutableAsync(progress, ct);
        var ffmpegPath = await EnsureFfmpegExecutableAsync(progress, ct);

        int h = selectedQuality.TargetHeight;
        string formatSelector;

        if (!string.IsNullOrWhiteSpace(ffmpegPath))
        {
            formatSelector = h > 0
                ? $"bestvideo[height<={h}]+bestaudio/bestvideo+bestaudio/best[height<={h}]/best"
                : "bestvideo+bestaudio/best";
        }
        else
        {
            formatSelector = h > 0
                ? $"best[height<={h}][ext=mp4]/best[height<={h}]/best"
                : "best[ext=mp4]/best";
        }

        TryDeletePartialFile(destinationPath);

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        psi.ArgumentList.Add("--no-playlist");
        psi.ArgumentList.Add("--newline");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--user-agent");
        psi.ArgumentList.Add(HttpHelper.BrowserUserAgent);

        var targetUrl = !string.IsNullOrWhiteSpace(overrideUrl) ? overrideUrl : video.VideoPageUrl;
        psi.ArgumentList.Add("--referer");
        psi.ArgumentList.Add(targetUrl.Contains("dailymotion.com", StringComparison.OrdinalIgnoreCase)
            ? "https://www.dailymotion.com/"
            : (targetUrl.Contains("reddit.com", StringComparison.OrdinalIgnoreCase) || targetUrl.Contains("v.redd.it", StringComparison.OrdinalIgnoreCase)
                ? "https://www.reddit.com/"
                : targetUrl));

        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(formatSelector);

        if (!string.IsNullOrWhiteSpace(ffmpegPath))
        {
            psi.ArgumentList.Add("--ffmpeg-location");
            psi.ArgumentList.Add(ffmpegPath);
            psi.ArgumentList.Add("--merge-output-format");
            psi.ArgumentList.Add("mp4");
        }

        string? generatedCookieFile = null;
        try
        {
            string? effectiveCookieFile = cookieFilePath;
            if (string.IsNullOrWhiteSpace(effectiveCookieFile) && !string.IsNullOrWhiteSpace(pageCookies))
            {
                generatedCookieFile = CreateNetscapeCookieFileFromHeader(pageCookies, targetUrl);
                effectiveCookieFile = generatedCookieFile;
            }

            if (!string.IsNullOrWhiteSpace(effectiveCookieFile) && File.Exists(effectiveCookieFile))
            {
                psi.ArgumentList.Add("--cookies");
                psi.ArgumentList.Add(effectiveCookieFile);
            }

            psi.ArgumentList.Add("-o");
            psi.ArgumentList.Add(destinationPath);
            psi.ArgumentList.Add(targetUrl);

            progress.Report((4, $"Iniciando descarga en {selectedQuality.Label}..."));

            using var proc = Process.Start(psi)
                ?? throw new InvalidOperationException("No se pudo iniciar el proceso de descarga.");

            await using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });

            var errSb = new StringBuilder();
            var stdoutTask = Task.Run(async () =>
            {
                while (!proc.StandardOutput.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await proc.StandardOutput.ReadLineAsync(ct);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var m = YtDlpPercentRegex.Match(line);
                    if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                    {
                        progress.Report((pct, $"Descargando ({selectedQuality.Label}): {pct:0.0}%"));
                    }
                    else if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase))
                    {
                        progress.Report((98, "Uniendo audio y video en MP4..."));
                    }
                }
            }, ct);

            var stderrTask = Task.Run(async () =>
            {
                while (!proc.StandardError.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await proc.StandardError.ReadLineAsync(ct);
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    // Filtrar advertencias benignas de yt-dlp para no alarmar al usuario
                    if (line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Deprecated Feature", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("Función obsoleta", StringComparison.OrdinalIgnoreCase) ||
                        line.Contains("JavaScript runtime", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    errSb.AppendLine(line);
                }
            }, ct);

            await proc.WaitForExitAsync(ct);
            try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }

            if (proc.ExitCode != 0 && (!File.Exists(destinationPath) || new FileInfo(destinationPath).Length < 1000))
            {
                var errMsg = errSb.ToString().Trim();
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errMsg) ? $"Código de salida {proc.ExitCode}" : errMsg);
            }

            progress.Report((100, "¡Descarga completada!"));
        }
        finally
        {
            if (generatedCookieFile != null && File.Exists(generatedCookieFile))
            {
                try { File.Delete(generatedCookieFile); } catch { }
            }
        }
    }

    /// <summary>
    /// Descarga un video o extrae audio MP3 directamente a partir de cualquier enlace web compatible
    /// (YouTube, Twitch, TikTok, Instagram, Dailymotion o enlace directo de video).
    /// </summary>
    public async Task<string> DownloadDirectUrlAsync(
        string url,
        bool isAudioOnlyMp3,
        QualityOption selectedQuality,
        string targetDirectory,
        IProgress<(double Percent, string Status)> progress,
        CancellationToken ct,
        bool isPlaylist = false)
    {
        var ytDlpPath = await EnsureYtDlpExecutableAsync(progress, ct);
        var ffmpegPath = await EnsureFfmpegExecutableAsync(progress, ct);

        Directory.CreateDirectory(targetDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = ytDlpPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        if (isPlaylist)
        {
            psi.ArgumentList.Add("--yes-playlist");
        }
        else
        {
            psi.ArgumentList.Add("--no-playlist");
        }
        psi.ArgumentList.Add("--newline");
        psi.ArgumentList.Add("--no-warnings");
        psi.ArgumentList.Add("--windows-filenames");
        psi.ArgumentList.Add("--user-agent");
        psi.ArgumentList.Add(HttpHelper.BrowserUserAgent);

        var cleanUrl = url.Trim();
        if (cleanUrl.Contains("dailymotion.com", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("--referer");
            psi.ArgumentList.Add("https://www.dailymotion.com/");
        }
        else if (cleanUrl.Contains("instagram.com", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("--referer");
            psi.ArgumentList.Add("https://www.instagram.com/");
        }
        else if (cleanUrl.Contains("tiktok.com", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("--referer");
            psi.ArgumentList.Add("https://www.tiktok.com/");
        }
        else if (cleanUrl.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase))
        {
            psi.ArgumentList.Add("--referer");
            psi.ArgumentList.Add("https://www.twitch.tv/");
        }
        else
        {
            psi.ArgumentList.Add("--referer");
            psi.ArgumentList.Add(cleanUrl);
        }

        if (isAudioOnlyMp3)
        {
            psi.ArgumentList.Add("-x");
            psi.ArgumentList.Add("--audio-format");
            psi.ArgumentList.Add("mp3");
            psi.ArgumentList.Add("--audio-quality");
            psi.ArgumentList.Add("0");
            if (!string.IsNullOrWhiteSpace(ffmpegPath))
            {
                psi.ArgumentList.Add("--ffmpeg-location");
                psi.ArgumentList.Add(ffmpegPath);
            }
        }
        else
        {
            int h = selectedQuality.TargetHeight;
            string formatSelector = !string.IsNullOrWhiteSpace(ffmpegPath)
                ? (h > 0 ? $"bestvideo[height<={h}]+bestaudio/bestvideo+bestaudio/best[height<={h}]/best" : "bestvideo+bestaudio/best")
                : (h > 0 ? $"best[height<={h}][ext=mp4]/best[height<={h}]/best" : "best[ext=mp4]/best");

            psi.ArgumentList.Add("-f");
            psi.ArgumentList.Add(formatSelector);

            if (!string.IsNullOrWhiteSpace(ffmpegPath))
            {
                psi.ArgumentList.Add("--ffmpeg-location");
                psi.ArgumentList.Add(ffmpegPath);
                psi.ArgumentList.Add("--merge-output-format");
                psi.ArgumentList.Add("mp4");
            }
        }

        psi.ArgumentList.Add("--print");
        psi.ArgumentList.Add("after_move:filepath");

        var outputTemplate = isPlaylist
            ? Path.Combine(targetDirectory, "%(playlist_title)s", "%(playlist_index)02d - %(title)s.%(ext)s")
            : Path.Combine(targetDirectory, "%(title)s.%(ext)s");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(outputTemplate);
        psi.ArgumentList.Add(cleanUrl);

        progress.Report((4, isAudioOnlyMp3 ? "Conectando para extraer audio MP3..." : $"Conectando para descargar video ({selectedQuality.Label})..."));

        using var proc = Process.Start(psi)
            ?? throw new InvalidOperationException("No se pudo iniciar el motor de descarga.");

        await using var reg = ct.Register(() => { try { proc.Kill(true); } catch { } });

        string? downloadedPath = null;
        var errSb = new StringBuilder();
        int currentPlaylistItem = 0;
        int totalPlaylistItems = 0;

        var stdoutTask = Task.Run(async () =>
        {
            while (!proc.StandardOutput.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await proc.StandardOutput.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;

                var pm = YtDlpPlaylistIndexRegex.Match(line);
                if (pm.Success && int.TryParse(pm.Groups[1].Value, out var cIdx) && int.TryParse(pm.Groups[2].Value, out var tIdx))
                {
                    currentPlaylistItem = cIdx;
                    totalPlaylistItems = tIdx;
                    progress.Report(((double)(cIdx - 1) / Math.Max(1, tIdx) * 100.0, $"[Video {cIdx}/{tIdx}] Iniciando descarga..."));
                }

                var m = YtDlpPercentRegex.Match(line);
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                {
                    if (isPlaylist && totalPlaylistItems > 0)
                    {
                        var overallPct = ((currentPlaylistItem - 1) + (pct / 100.0)) / totalPlaylistItems * 100.0;
                        progress.Report((overallPct, $"[Video {currentPlaylistItem}/{totalPlaylistItems}] {pct:0.0}%"));
                    }
                    else
                    {
                        progress.Report((pct, isAudioOnlyMp3 ? $"Descargando audio: {pct:0.0}%" : $"Descargando video: {pct:0.0}%"));
                    }
                }
                else if (line.Contains("[ExtractAudio]", StringComparison.OrdinalIgnoreCase))
                {
                    progress.Report((95, "Extrayendo y codificando audio a MP3..."));
                }
                else if (line.Contains("[Merger]", StringComparison.OrdinalIgnoreCase))
                {
                    progress.Report((98, "Uniendo pistas en MP4..."));
                }
                else if (Path.IsPathRooted(line.Trim()))
                {
                    downloadedPath = line.Trim();
                }
            }
        }, ct);

        var stderrTask = Task.Run(async () =>
        {
            while (!proc.StandardError.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await proc.StandardError.ReadLineAsync(ct);
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("WARNING:", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Deprecated Feature", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("JavaScript runtime", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                errSb.AppendLine(line);
            }
        }, ct);

        await proc.WaitForExitAsync(ct);
        try { await Task.WhenAll(stdoutTask, stderrTask); } catch { }

        if (proc.ExitCode != 0)
        {
            var errMsg = errSb.ToString().Trim();
            if (string.IsNullOrWhiteSpace(downloadedPath) || !File.Exists(downloadedPath))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(errMsg) ? $"Error durante la descarga (código {proc.ExitCode})" : errMsg);
        }

        if (!string.IsNullOrWhiteSpace(downloadedPath))
        {
            if (isPlaylist)
            {
                var parentDir = Path.GetDirectoryName(downloadedPath);
                if (!string.IsNullOrWhiteSpace(parentDir) && Directory.Exists(parentDir))
                    return parentDir;
            }
            if (File.Exists(downloadedPath))
                return downloadedPath;
        }

        var extExpected = isAudioOnlyMp3 ? ".mp3" : ".mp4";
        var dirInfo = new DirectoryInfo(targetDirectory);
        var recentFile = dirInfo.GetFiles($"*{extExpected}")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault();

        if (recentFile != null && DateTime.UtcNow - recentFile.LastWriteTimeUtc < TimeSpan.FromMinutes(2))
        {
            return recentFile.FullName;
        }

        return downloadedPath ?? Path.Combine(targetDirectory, "descarga" + extExpected);
    }

    private async Task<string> EnsureYtDlpExecutableAsync(
        IProgress<(double Percent, string Status)> progress,
        CancellationToken ct)
    {
        // 0) Carpeta de instalación de la aplicación
        var appBase = AppDomain.CurrentDomain.BaseDirectory;
        var inAppTools = Path.Combine(appBase, "Tools", "yt-dlp.exe");
        if (File.Exists(inAppTools)) return inAppTools;

        var inAppBase = Path.Combine(appBase, "yt-dlp.exe");
        if (File.Exists(inAppBase)) return inAppBase;

        // 1) Buscar en carpeta local de herramientas
        var localTool = Path.Combine(_toolsDir, "yt-dlp.exe");
        if (File.Exists(localTool) && new FileInfo(localTool).Length > 1_000_000)
            return localTool;

        // 2) Buscar en PATH del sistema o paquetes de WinGet
        var inSystem = FindExecutableInPathOrWinget("yt-dlp.exe");
        if (!string.IsNullOrWhiteSpace(inSystem))
            return inSystem;

        // 3) Descargar automáticamente binario oficial standalone de yt-dlp por única vez
        progress.Report((1, "Configurando motor universal de descarga (1ª vez)..."));
        const string officialUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        var dlProgress = new Progress<double>(p =>
            progress.Report((Math.Clamp(p * 0.15, 1, 15), $"Preparando motor de descarga ({p:0}%)...")));

        var tempPath = localTool + ".tmp";
        await HttpHelper.DownloadFileWithProgressAsync(officialUrl, tempPath, null, null, dlProgress, ct);
        File.Move(tempPath, localTool, overwrite: true);
        return localTool;
    }

    public async Task<string?> EnsureFfmpegExecutableAsync(
        IProgress<(double Percent, string Status)>? progress,
        CancellationToken ct)
    {
        var existing = FindFfmpegExecutable();
        if (!string.IsNullOrWhiteSpace(existing))
            return existing;

        var localTool = Path.Combine(_toolsDir, "ffmpeg.exe");
        if (File.Exists(localTool) && new FileInfo(localTool).Length > 1_000_000)
        {
            _cachedFfmpegPath = localTool;
            return localTool;
        }

        try
        {
            progress?.Report((2, "Configurando códecs de video (1ª vez)..."));
            const string ffmpegZipUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
            var tempZip = Path.Combine(_toolsDir, "ffmpeg_download.tmp");

            var dlProgress = new Progress<double>(p =>
                progress?.Report((Math.Clamp(p * 0.20, 2, 20), $"Descargando soporte HD ({p:0}%)...")));

            await HttpHelper.DownloadFileWithProgressAsync(ffmpegZipUrl, tempZip, null, null, dlProgress, ct);

            progress?.Report((21, "Instalando soporte HD..."));
            using (var zip = System.IO.Compression.ZipFile.OpenRead(tempZip))
            {
                var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase));
                if (entry != null)
                {
                    using var entryStream = entry.Open();
                    using var outFs = File.Create(localTool);
                    entryStream.CopyTo(outFs);
                }
            }

            try { File.Delete(tempZip); } catch { }

            if (File.Exists(localTool))
            {
                _cachedFfmpegPath = localTool;
                return localTool;
            }
        }
        catch
        {
            // Continuar con lo disponible
        }

        return null;
    }

    private string? FindFfmpegExecutable()
    {
        if (_cachedFfmpegPath != null && File.Exists(_cachedFfmpegPath))
            return _cachedFfmpegPath;

        // 1) Carpeta de instalación de la aplicación
        var appBase = AppDomain.CurrentDomain.BaseDirectory;
        var inAppTools = Path.Combine(appBase, "Tools", "ffmpeg.exe");
        if (File.Exists(inAppTools)) return _cachedFfmpegPath = inAppTools;

        var inAppBase = Path.Combine(appBase, "ffmpeg.exe");
        if (File.Exists(inAppBase)) return _cachedFfmpegPath = inAppBase;

        // 2) Carpeta local de herramientas de usuario
        var inLocalTools = Path.Combine(_toolsDir, "ffmpeg.exe");
        if (File.Exists(inLocalTools) && new FileInfo(inLocalTools).Length > 1_000_000)
            return _cachedFfmpegPath = inLocalTools;

        // 3) PATH y WinGet
        var inSystem = FindExecutableInPathOrWinget("ffmpeg.exe");
        if (!string.IsNullOrWhiteSpace(inSystem))
            return _cachedFfmpegPath = inSystem;

        return null;
    }

    private static string? FindExecutableInPathOrWinget(string exeName)
    {
        // 1) Variables de entorno PATH
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), exeName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch { }
        }

        // 2) Enlaces directos de WinGet (Links)
        try
        {
            var wingetLinks = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Links", exeName);
            if (File.Exists(wingetLinks))
                return wingetLinks;
        }
        catch { }

        // 3) Paquetes instalados de WinGet
        try
        {
            var wingetDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Microsoft", "WinGet", "Packages");

            if (Directory.Exists(wingetDir))
            {
                var found = Directory.EnumerateFiles(wingetDir, exeName, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(found))
                    return found;
            }
        }
        catch { }

        return null;
    }

    private static async Task<List<DiscoveredVideoStream>> TryExpandHlsMasterPlaylistAsync(
        string m3u8Url,
        string? referer,
        string? cookies,
        CancellationToken ct)
    {
        var variants = new List<DiscoveredVideoStream>();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var text = string.IsNullOrWhiteSpace(cookies)
                ? await HttpHelper.GetUtf8StringAsync(m3u8Url, referer, cts.Token)
                : await HttpHelper.GetUtf8StringWithAgeBypassAsync(m3u8Url, referer, cts.Token, cookies);

            if (!text.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase) ||
                !Uri.TryCreate(m3u8Url, UriKind.Absolute, out var baseUri))
            {
                return variants;
            }

            var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (!line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.OrdinalIgnoreCase))
                    continue;

                var attrs = line["#EXT-X-STREAM-INF:".Length..];
                string? rawTarget = null;
                for (int j = i + 1; j < lines.Length; j++)
                {
                    var candidateLine = lines[j].Trim();
                    if (!candidateLine.StartsWith('#'))
                    {
                        rawTarget = candidateLine;
                        i = j;
                        break;
                    }
                }

                if (string.IsNullOrWhiteSpace(rawTarget) || !Uri.TryCreate(baseUri, rawTarget, out var variantUri))
                    continue;

                int height;
                var resMatch = ResolutionAttrRegex.Match(attrs);
                if (resMatch.Success && int.TryParse(resMatch.Groups[2].Value, out var hVal))
                    height = hVal;
                else
                    height = InferHeightFromUrl(rawTarget);

                long bw = 0;
                var bwMatch = BandwidthAttrRegex.Match(attrs);
                if (bwMatch.Success)
                    long.TryParse(bwMatch.Groups[1].Value, out bw);

                variants.Add(new DiscoveredVideoStream
                {
                    Url = variantUri.ToString(),
                    Height = height,
                    Bandwidth = bw,
                    IsHls = true,
                    Referer = referer,
                    Cookies = cookies
                });
            }
        }
        catch
        {
            // Si no es master playlist o no responde, se conserva la URL HLS original
        }

        return variants;
    }

    private static bool IsPlatformRequiringUniversalExtractor(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        var lower = url.ToLowerInvariant();
        return lower.Contains("youtube.com") ||
               lower.Contains("youtu.be") ||
               lower.Contains("tiktok.com") ||
               lower.Contains("dailymotion.com") ||
               lower.Contains("vimeo.com") ||
               lower.Contains("bilibili.com") ||
               lower.Contains("reddit.com") ||
               lower.Contains("redd.it") ||
               lower.Contains("twitch.tv") ||
               lower.Contains("rumble.com");
    }

    public static string HumanizeDownloadErrorMessage(string rawError, QualityOption quality)
    {
        if (string.IsNullOrWhiteSpace(rawError))
            return $"No se pudo completar la descarga en la calidad seleccionada ({quality.Label}).";

        if (rawError.Contains("Requested format is not available", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("formato solicitado no está disponible", StringComparison.OrdinalIgnoreCase))
        {
            return $"La calidad {quality.Label} no está disponible para este video en particular. Intenta descargándolo en calidad 'Automática (Mejor)'.";
        }

        if (rawError.Contains("Account authentication is required", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Private video", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("login", StringComparison.OrdinalIgnoreCase))
        {
            return "Este video tiene restricción de acceso o requiere una cuenta en la plataforma para reproducirse.";
        }

        if (rawError.Contains("Video unavailable", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("Not found", StringComparison.OrdinalIgnoreCase) ||
            rawError.Contains("404", StringComparison.OrdinalIgnoreCase))
        {
            return "El video seleccionado fue retirado o no se encuentra disponible en el servidor.";
        }

        // Extraer solo líneas informativas eliminando advertencias y tracebacks técnicos
        var cleanLines = rawError
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(l => !l.Contains("traceback", StringComparison.OrdinalIgnoreCase) &&
                        !l.Contains("File \"", StringComparison.OrdinalIgnoreCase) &&
                        !l.Contains("WARNING:", StringComparison.OrdinalIgnoreCase) &&
                        !l.Contains("Deprecated Feature", StringComparison.OrdinalIgnoreCase) &&
                        !l.Contains("Función obsoleta", StringComparison.OrdinalIgnoreCase))
            .Select(l => l.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase) ? l["ERROR:".Length..].Trim() : l.Trim())
            .ToList();

        var detail = cleanLines.Count > 0 ? string.Join(" ", cleanLines) : rawError;
        return $"No fue posible descargar este video en la calidad seleccionada ({quality.Label}). Detalle: {detail}";
    }

    public static string? CreateNetscapeCookieFileFromHeader(string cookieHeader, string targetUrl)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader) || !Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri))
            return null;

        try
        {
            var host = uri.Host;
            var domain = host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[3..] : "." + host;
            var tempPath = Path.Combine(Path.GetTempPath(), $"buscapp_c_{Guid.NewGuid():N}.txt");

            var sb = new StringBuilder();
            sb.AppendLine("# Netscape HTTP Cookie File");
            sb.AppendLine("# Created by BUSCAPP");

            var pairs = cookieHeader.Split(';', StringSplitOptions.RemoveEmptyEntries);
            foreach (var rawPair in pairs)
            {
                var pair = rawPair.Trim();
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var name = pair[..eq].Trim();
                var val = pair[(eq + 1)..].Trim();
                if (string.IsNullOrEmpty(name)) continue;

                sb.AppendLine($"{domain}\tTRUE\t/\tTRUE\t2147483647\t{name}\t{val}");
            }

            File.WriteAllText(tempPath, sb.ToString(), Encoding.ASCII);
            return tempPath;
        }
        catch
        {
            return null;
        }
    }

    private static int InferHeightFromUrl(string url)
    {
        var m = HeightFromUrlOrLabelRegex.Match(url);
        return m.Success && int.TryParse(m.Groups[1].Value, out var h) ? h : 0;
    }

    private static byte[] BuildSequenceIv(int seq)
    {
        var iv = new byte[16];
        var bytes = BitConverter.GetBytes(seq);
        if (BitConverter.IsLittleEndian)
            Array.Reverse(bytes);
        Buffer.BlockCopy(bytes, 0, iv, 12, 4);
        return iv;
    }

    public static string SanitizeFileName(string rawTitle)
    {
        if (string.IsNullOrWhiteSpace(rawTitle))
            return "video_buscapp";

        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var sb = new StringBuilder(rawTitle.Length);
        foreach (var ch in rawTitle.Trim())
        {
            sb.Append(invalid.Contains(ch) ? '_' : ch);
        }

        var clean = Regex.Replace(sb.ToString(), @"\s+", " ").Trim().Trim('.');
        if (clean.Length > 95)
            clean = clean[..95].Trim();

        return string.IsNullOrWhiteSpace(clean) ? "video_buscapp" : clean;
    }

    private static void TryDeletePartialFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch { }
    }
}
