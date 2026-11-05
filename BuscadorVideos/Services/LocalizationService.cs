namespace BuscadorVideos.Services;

/// <summary>
/// Gestiona los textos de la interfaz en Español e Inglés con cambio dinámico en tiempo real.
/// </summary>
public static class LocalizationService
{
    public static string CurrentLanguage { get; set; } = "es";

    private static readonly Dictionary<string, (string Es, string En)> Strings = new()
    {
        // Navigation & General
        ["HomeMenu"] = ("🏠 Menú", "🏠 Menu"),
        ["SearchArea"] = ("🔍 Buscador", "🔍 Search"),
        ["DownloaderArea"] = ("📥 Descargas", "📥 Downloader"),
        ["Favorites"] = ("⭐ Favoritos", "⭐ Favorites"),
        ["History"] = ("🕒 Historial", "🕒 History"),
        ["Settings"] = ("⚙ Ajustes", "⚙ Settings"),
        ["Incognito"] = ("🕶️ Incógnito", "🕶️ Incognito"),
        ["AdBlockOn"] = ("🛡️ AdBlock: ON", "🛡️ AdBlock: ON"),
        ["AdBlockOff"] = ("⚠️ AdBlock: OFF", "⚠️ AdBlock: OFF"),
        ["DonateCafecito"] = ("☕ Invitar un Cafecito", "☕ Buy me a Coffee"),

        // Search Bar & Actions
        ["SearchPlaceholder"] = ("Buscar videos en tus páginas activas o pegar enlace https://...", "Search videos across active sites or paste https:// link..."),
        ["SearchBtn"] = ("🔍 Buscar", "🔍 Search"),
        ["CancelBtn"] = ("Cancelar", "Cancel"),
        ["RevertBtn"] = ("↩ Revertir", "↩ Revert"),
        ["ClearBtn"] = ("🧹 Borrar", "🧹 Clear"),
        ["GridLabel"] = ("⊞ Cuadrícula:", "⊞ Grid:"),

        // Sidebar
        ["MySites"] = ("▶ Mis Páginas", "▶ My Sites"),
        ["NsfwSites"] = ("🔞 Páginas NSFW", "🔞 Adult Sites (18+)"),
        ["AddSiteHeader"] = ("Nombre del buscador / página:", "Search engine / site name:"),
        ["AddSiteUrlHeader"] = ("Enlace (URL, dominio o plantilla {q}):", "Link (URL, domain or {q} template):"),
        ["AddSiteBtn"] = ("+ Agregar a mi lista", "+ Add to my list"),
        ["SelectAll"] = ("Marcar todas", "Select all"),
        ["UnselectAll"] = ("Desmarcar", "Deselect all"),
        ["ResetDefaults"] = ("↺ Base", "↺ Reset"),
        ["UndoDelete"] = ("↶ Revertir borrado en mi lista", "↶ Undo delete in my list"),

        // Home Menu
        ["HomeTitle"] = ("¿Qué deseas hacer hoy?", "What would you like to do today?"),
        ["HomeSubtitle"] = ("Selecciona una opción para comenzar. Puedes regresar a este menú o alternar entre herramientas en cualquier momento.", "Select an option to begin. You can return to this menu or switch between tools at any time."),
        ["HomeSearchTitle"] = ("Buscador Universal de Videos", "Universal Video Search"),
        ["HomeSearchDesc"] = ("Explora y encuentra videos al mismo tiempo en YouTube, Twitch, TikTok, Instagram y Dailymotion. Incluye vista previa interactiva al pasar el mouse, selector de cuadrícula de 3, 4 o 6 columnas y reproductor limpio sin publicidad.", "Explore and find videos simultaneously across YouTube, Twitch, TikTok, Instagram, and Dailymotion. Features mouse hover previews, 3/4/6 column grid layouts, and clean ad-free playback."),
        ["HomeSearchBtn"] = ("🔍 Abrir Buscador Universal →", "🔍 Open Universal Search →"),
        ["HomeDownloaderTitle"] = ("Descargador de Video y MP3", "Video & MP3 Downloader"),
        ["HomeDownloaderDesc"] = ("Pega el enlace directo de cualquier video de tus plataformas favoritas. Descarga el archivo de video en alta definición (1080p, 720p...) o extrae únicamente la pista de audio en formato MP3 de alta fidelidad.", "Paste any direct video link from your favorite platforms. Download HD videos (1080p, 720p...) or extract audio-only MP3 in high quality."),
        ["HomeDownloaderBtn"] = ("📥 Abrir Descargador →", "📥 Open Downloader →"),
        ["HomeFavBtn"] = ("⭐ Videos Favoritos", "⭐ Favorite Videos"),
        ["HomeHistBtn"] = ("🕒 Historial de Videos", "🕒 Video History"),
        ["HomeSupportedLabel"] = ("Plataformas principales compatibles:", "Supported main platforms:"),

        // Downloader View
        ["DownloaderHeader"] = ("📥 Descargador Universal de Videos y MP3", "📥 Universal Video & MP3 Downloader"),
        ["DownloaderUrlLabel"] = ("Enlace del video a descargar:", "Video link to download:"),
        ["DownloaderUrlPlaceholder"] = ("Pega aquí el enlace https://... del video", "Paste the https://... video link here"),
        ["DownloaderPasteBtn"] = ("📋 Pegar", "📋 Paste"),
        ["DownloaderFormatLabel"] = ("Selecciona qué deseas descargar:", "Choose what you want to download:"),
        ["DownloaderFormatVideo"] = ("Video Completo (MP4)", "Full Video (MP4)"),
        ["DownloaderFormatVideoSub"] = ("Descarga imagen y audio sincronizados en la mejor resolución disponible", "Downloads video and audio synchronized in highest available resolution"),
        ["DownloaderFormatAudio"] = ("Solo Audio (MP3)", "Audio Only (MP3)"),
        ["DownloaderFormatAudioSub"] = ("Extrae la pista de audio sin video, ideal para canciones y podcasts", "Extracts audio track without video, ideal for music and podcasts"),
        ["DownloaderQualityLabel"] = ("Calidad preferida para video:", "Preferred video quality:"),
        ["DownloaderFolderLabel"] = ("Carpeta donde guardar la descarga:", "Folder to save downloads:"),
        ["DownloaderBrowseBtn"] = ("📁 Cambiar...", "📁 Browse..."),
        ["DownloaderStartBtn"] = ("Descargar Ahora", "Download Now"),
        ["DownloaderPlaylistText"] = ("📑 Descargar Lista de Reproducción Completa (Playlist)", "📑 Download Full Playlist"),
        ["DownloaderPlaylistSub"] = (" — Descargará todos los videos/canciones en una subcarpeta organizada", " — Downloads all videos/tracks into an organized subfolder"),

        // Settings Modal
        ["SettingsTitle"] = ("Ajustes y Personalización", "Settings & Customization"),
        ["SettingsAppearance"] = ("🎨 Apariencia y Tema de la Aplicación", "🎨 Appearance & App Theme"),
        ["ThemeDark"] = ("Oscuro", "Dark"),
        ["ThemeDarkSub"] = ("Predeterminado", "Default"),
        ["ThemeLight"] = ("Blanco", "Light"),
        ["ThemeLightSub"] = ("Luminoso", "Clean & bright"),
        ["ThemeCustom"] = ("Personalizado", "Custom"),
        ["ThemeCustomSub"] = ("Color a elección", "Choice of accent"),
        ["CustomColorLabel"] = ("Elige tu color de acento favorito:", "Choose your favorite accent color:"),
        ["SettingsLanguage"] = ("🌐 Idioma de la Aplicación", "🌐 Application Language"),
        ["LangSpanish"] = ("Español", "Spanish"),
        ["LangEnglish"] = ("English", "English"),
        ["SettingsPrivacy"] = ("🕶️ Privacidad y Contenido", "🕶️ Privacy & Content"),
        ["SettingsIncognito"] = ("Activar Modo Incógnito", "Enable Incognito Mode"),
        ["SettingsIncognitoSub"] = ("Cuando está activo, no se guardan búsquedas en el historial reciente ni se registran los videos reproducidos.", "When active, search history and video playback history will not be saved."),
        ["SettingsAdult"] = ("Habilitar sección para adultos (+18)", "Enable adult content section (18+)"),
        ["SettingsAdultSub"] = ("Muestra el botón 'Páginas NSFW' en el panel lateral para buscar en portales para adultos.", "Shows the 'Adult Sites (18+)' button in the sidebar to search adult sites."),
        ["SettingsDownloads"] = ("📥 Descargas y Carpetas", "📥 Downloads & Folders"),
        ["SettingsDownloadDirLabel"] = ("Carpeta de descargas predeterminada:", "Default downloads folder:"),
        ["SettingsPlaylistCheck"] = ("Descargar playlist completa al detectar una lista de YouTube", "Download entire playlist when detecting a YouTube playlist"),
        ["SettingsPlaylistCheckSub"] = ("Descarga todos los videos o canciones de la lista en una subcarpeta organizada.", "Downloads all videos or songs in the playlist into an organized subfolder."),
        ["SettingsData"] = ("🧹 Datos y Almacenamiento Local", "🧹 Data & Local Storage"),
        ["ClearSearchHistBtn"] = ("Limpiar Historial de Búsquedas", "Clear Search History"),
        ["ClearPlaybackHistBtn"] = ("Limpiar Historial de Reproducción", "Clear Playback History"),
        ["ClearFavsBtn"] = ("Vaciar Favoritos", "Clear Favorites"),
        ["SettingsDoneBtn"] = ("Listo / Guardar", "Done / Save"),

        // Empty states
        ["NoFavsTitle"] = ("No tienes videos favoritos aún", "No favorite videos yet"),
        ["NoFavsSub"] = ("Toca la estrella ☆ en la esquina superior derecha de cualquier video para guardarlo aquí.", "Tap the ☆ star in the top-right corner of any video to save it here."),
        ["NoHistTitle"] = ("Historial de reproducción vacío", "Playback history is empty"),
        ["NoHistSub"] = ("Los videos que reproduzcas dentro de la aplicación se guardarán automáticamente aquí para que puedas retomarlos cuando quieras.", "Videos you play in the app will automatically appear here so you can revisit them anytime.")
    };

    public static string T(string key)
    {
        if (Strings.TryGetValue(key, out var pair))
        {
            return CurrentLanguage.Equals("en", StringComparison.OrdinalIgnoreCase) ? pair.En : pair.Es;
        }
        return key;
    }
}
