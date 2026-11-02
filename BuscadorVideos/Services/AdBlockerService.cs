namespace BuscadorVideos.Services;

/// <summary>
/// Bloqueador multicapa de anuncios, ventanas emergentes (popups/popunders), rastreadores VAST/IMA
/// y capas invisibles de clickjacking que provocan errores o interrupciones en los reproductores web.
/// Permite activarse y desactivarse dinámicamente en tiempo de ejecución.
/// </summary>
public class AdBlockerService
{
    public bool IsEnabled { get; set; } = true;

    private static readonly string[] BlockedAdPatterns =
    [
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "adservice.google.",
        "imasdk.googleapis.com", "pagead2.googlesyndication", "exoclick.com", "realsrv.com",
        "magsrv.com", "juicyads.com", "trafficjunky.net", "trafficstars.com", "tsyndicate.com",
        "popads.net", "popcash.net", "propellerads.com", "adsterra.com", "adnxs.com",
        "taboola.com", "outbrain.com", "clksite.com", "onclickalgo.com", "hilltopads.net",
        "ero-advertising.com", "plugrush.com", "syndication.realsrv", "adsco.re", "acscdn.com",
        "clickadu.com", "galaksion.com", "bidgear.com", "spotxchange.com", "rubiconproject.com",
        "pubmatic.com", "openx.net", "criteo.com", "mgid.com", "revcontent.com",
        "admaven.com", "monetag.com", "a-ads.com", "tsyndicate", "popunder",
        "/adserver", "/vast.xml", "/preroll", "ads.trafficjunky", "banner.php?"
    ];

    public bool ShouldBlockRequest(string? requestUri)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(requestUri))
            return false;

        // No bloquear nunca manifiestos o segmentos de video reales
        if (requestUri.Contains(".m3u8", StringComparison.OrdinalIgnoreCase) ||
            requestUri.Contains(".ts?", StringComparison.OrdinalIgnoreCase) ||
            requestUri.Contains("googlevideo.com/videoplayback", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var pattern in BlockedAdPatterns)
        {
            if (requestUri.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Script inyectado al crear cada documento que respeta la bandera global window.__buscappAdBlockEnabled.
    /// </summary>
    public static string BuildDocumentCreatedProtectionScript(bool initialEnabled) => $@"
(() => {{
    window.__buscappAdBlockEnabled = {(initialEnabled ? "true" : "false")};
    const origOpen = window.open;
    window.open = function(...args) {{
        if (window.__buscappAdBlockEnabled) return null;
        return origOpen ? origOpen.apply(window, args) : null;
    }};
    window.alert = function() {{}};
    const origConfirm = window.confirm;
    window.confirm = function(msg) {{
        if (window.__buscappAdBlockEnabled) return true;
        return origConfirm ? origConfirm.call(window, msg) : true;
    }};
}})();";

    /// <summary>
    /// Limpia capas invisibles de clickjacking, banners superpuestos y contenedores publicitarios sobre el reproductor.
    /// </summary>
    public const string CleanDomAdOverlaysScript = @"
(() => {
    if (!window.__buscappAdBlockEnabled) return;
    try {
        const adSelectors = [
            '[id*=""ad-container"" i]', '[class*=""ad-container"" i]',
            '[id*=""ad_overlay"" i]', '[class*=""ad_overlay"" i]',
            '[class*=""popunder"" i]', '[id*=""popunder"" i]',
            '[class*=""preroll-ad"" i]', '[class*=""vast-blocker"" i]',
            '.ytp-ad-overlay-container', '.ytp-ad-module', '#player-ads',
            'iframe[src*=""exoclick""]', 'iframe[src*=""trafficjunky""]',
            'iframe[src*=""juicyads""]', 'iframe[src*=""doubleclick""]',
            'iframe[src*=""realsrv""]', 'iframe[src*=""magsrv""]'
        ];
        document.querySelectorAll(adSelectors.join(',')).forEach(el => {
            if (!el.querySelector('video')) el.remove();
        });

        // Eliminar escudos invisibles de clic a pantalla completa que bloquean los controles del video
        document.querySelectorAll('div, a').forEach(el => {
            const st = window.getComputedStyle(el);
            const z = parseInt(st.zIndex || '0', 10);
            if ((st.position === 'fixed' || st.position === 'absolute') && z > 9000 && !el.querySelector('video')) {
                const r = el.getBoundingClientRect();
                const isTransparent = st.opacity === '0' || st.backgroundColor === 'rgba(0, 0, 0, 0)';
                if (isTransparent && r.width > window.innerWidth * 0.6 && r.height > window.innerHeight * 0.6 && (el.innerText || '').trim().length === 0) {
                    el.remove();
                }
            }
        });
    } catch (e) {}
})();";
}
