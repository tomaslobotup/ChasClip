namespace BuscadorVideos.Models;

/// <summary>
/// Opción visible en el regulador de calidad del reproductor integrado y selector para descarga.
/// </summary>
public class QualityOption
{
    public required string Label { get; init; }
    public required int TargetHeight { get; init; } // 0 = Automática / Máxima
    public required string YouTubeQualityCode { get; init; }

    public override string ToString() => Label;

    public static IReadOnlyList<QualityOption> DefaultOptions { get; } =
    [
        new() { Label = "Automática (Mejor)", TargetHeight = 0, YouTubeQualityCode = "default" },
        new() { Label = "1080p Full HD", TargetHeight = 1080, YouTubeQualityCode = "hd1080" },
        new() { Label = "720p HD", TargetHeight = 720, YouTubeQualityCode = "hd720" },
        new() { Label = "480p SD", TargetHeight = 480, YouTubeQualityCode = "large" },
        new() { Label = "360p", TargetHeight = 360, YouTubeQualityCode = "medium" },
        new() { Label = "240p", TargetHeight = 240, YouTubeQualityCode = "small" }
    ];
}

/// <summary>
/// Flujo multimedia directo (MP4/WebM o variante HLS .m3u8) detectado en tiempo real dentro del reproductor.
/// </summary>
public class DiscoveredVideoStream
{
    public required string Url { get; init; }
    public int Height { get; init; }
    public long Bandwidth { get; init; }
    public bool IsHls { get; init; }
    public string? Referer { get; init; }
    public string? Cookies { get; init; }
    public string Label => Height > 0 ? $"{Height}p" : (IsHls ? "HLS Auto" : "Directo");
}
