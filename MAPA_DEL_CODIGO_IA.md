# 🗺️ Mapa del Código e Informe de Arquitectura para IAs — ChasClip (`BUSCAPP`)

Este documento está diseñado para que cualquier modelo de IA o desarrollador encuentre **instantáneamente** dónde vive cada módulo, vista XAML, servicio y método sin tener que escanear el repositorio completo.

* **Ruta raíz del proyecto:** `d:\Pruebas\programas\buscapp\BuscadorVideos\`
* **Framework:** `.NET 8.0-windows` (`WPF` + `Microsoft.Web.WebView2`)
* **Identidad visual:** **ChasClip — Descargador Universal Multimedia** (Estilo 8-bit / Paleta algorítmica de 4 colores)

---

## ⚠️ 1. Reglas Críticas de Arquitectura (Leer antes de modificar)

1. **Solución de Airspace (`HwndHost`) en `BrowserWebView`:**
   * En WPF, el control `WebView2` (`BrowserWebView`) es una ventana Win32 (`HwndHost`) que siempre se dibuja por encima de cualquier elemento WPF que comparta su misma región de pantalla, sin importar el `Panel.ZIndex`.
   * **Cómo está resuelto:** El método `RefreshWebViewAirspace()` en `MainWindow.xaml.cs` (línea `~3191`) administra la sub-vista activa del navegador (`SpeedDial`, `HistoryPage`, `WebView`) y comprueba si hay algún modal o cajón abierto (`SnaptubeDownloadModalOverlay`, `AddShortcutModalOverlay`, `DownloadQueueDrawerOverlay`, `SettingsModalOverlay`). Cuando se abre el Historial, los Atajos o cualquier modal, `BrowserWebView` pasa a `Visibility.Collapsed` y se muestra `BrowserAirspacePlaceholderBorder`, garantizando que los modales y el historial jamás queden atrapados debajo del video.
   * **Regla:** Siempre que abras o cierres un modal/cajón por código, llama a `RefreshWebViewAirspace()`.

2. **Sniffer de Red Multimedia Universal (Estilo Video DownloadHelper):**
   * En sitios web con reproductores en iframes cross-origin (como `animeav1.com` con `player.zilla-networks.com`, `voe.sx`, `mp4upload`, etc.), los scripts del DOM no pueden acceder al `<video>` por aislamiento de seguridad.
   * **Cómo está resuelto:** `AttachBrowserMediaSniffer` (`MainWindow.xaml.cs`, línea `~4139`) intercepta todo el tráfico HTTP/HTTPS de la página y de todos los iframes hijos mediante `CoreWebView2.WebResourceRequested` y `CoreWebView2.WebResourceResponseReceived`.
   * Captura en caliente manifiestos HLS (`.m3u8`, `/m3u8/`, `/hls/`), DASH (`.mpd`), flujos MP4/WebM y cabeceras de origen (`Origin`, `Referer`, `Cookie`).
   * Los flujos detectados se registran como `BrowserDetectedMediaItem` y activan la barra inferior brillante `BrowserVideoDetectionBar` y el botón flotante.
   * La descarga se procesa en `VideoQualityAndDownloadService.TryDownloadSniffedStreamAsync` mediante FFmpeg con `-extension_picky 0 -allowed_extensions ALL` e inyección de cabeceras, permitiendo descargar cualquier fragmento o playlist.

3. **Botón Flotante de Descarga con Aura Brillante sobre Videos:**
   * Para mostrarse directamente sobre el reproductor de YouTube o cualquier sitio web (y cumplir con la política `TrustedTypes` de YouTube que prohíbe `.innerHTML`), el botón flotante `#__chasclip_floating_dl_btn__` se inyecta en el DOM del `BrowserWebView` mediante `BuildBrowserFloatingDownloadButtonScript()` (`MainWindow.xaml.cs`, línea `~3484`) usando únicamente `document.createElement`, `.textContent` y `.style.setProperty`.
   * Además, la interfaz WPF incluye un botón superior con aura (`BrowserTopDownloadGlowBorder` en `Grid.Row="0"`) y una barra inferior de detección (`BrowserVideoDetectionBar` en `Grid.Row="2"`), ubicados fuera de la fila del `BrowserWebView`.

4. **Algoritmo de Paletas de 4 Colores (`AppThemeService.cs`):**
   * Toda la interfaz obtiene sus colores de una semilla de 4 tonos (`VoidHex`, `MaroonHex`, `MistHex`, `KnightHex`) en `Services/AppThemeService.cs`. Para cambiar la paleta completa en el futuro, basta con cambiar esos 4 códigos HEX en `AppThemeService.cs` (líneas `35-65`).

---

## 📂 2. Estructura de Archivos del Proyecto

| Archivo | Líneas aprox. | Responsabilidad Principal |
| :--- | :--- | :--- |
| `App.xaml` | `603` | Recursos globales de color (`AppBgDarkBrush`, `AppAccentBrush`, `AppGlowColor`, etc.) y estilos base de botones, inputs y scrollbars. |
| `MainWindow.xaml` | `3,575` | Toda la interfaz gráfica dividida en 4 vistas principales (`HomeMenuGrid`, `DownloaderAreaGrid`, `BrowserAreaGrid`, `SearchAreaGrid`) + modales. |
| `MainWindow.xaml.cs` | `4,480` | Lógica de navegación, sniffer multimedia estilo Video DownloadHelper, inyección del botón flotante con aura, control de `RefreshWebViewAirspace()` y eventos UI. |
| `Controls/VideoCardControl.xaml(.cs)` | `211 / 338` | Tarjeta visual de video para la cuadrícula 3x4 del apartado experimental `BETA`. |
| `Models/BrowserDetectedMediaItem.cs` | `41` | Modelo para flujos de video detectados por el sniffer (URL, título, tipo de flujo HLS/MP4, referer, origin, cookies, tamaño). |
| `Models/DownloadQueueItem.cs` | `158` | Ítem de la lista de espera con soporte para cabeceras HTTP (`Referer`, `Origin`, `Cookies`), formato y progreso. |
| `Services/AppThemeService.cs` | `193` | Motor algorítmico de paletas de 4 colores (Modo Oscuro predeterminado y Modo Blanco). |
| `Services/DownloadQueueService.cs` | `304` | Cola de espera secuencial de descargas con progreso en vivo, cancelación, reintento y persistencia en `historial_descargas_cola.json`. |
| `Services/VideoQualityAndDownloadService.cs` | `1,508` | Motor FFmpeg nativo con remuxing HLS avanzado (`TryDownloadSniffedStreamAsync`), selector de calidades y motor yt-dlp universal. |
| `Services/BrowserHistoryService.cs` | `141` | Historial del Buscador Web en lista (`historial_navegador.json`) con soporte para actualización de títulos en SPAs. |
| `Services/BrowserShortcutsService.cs` | `76` | Atajos rápidos (Speed Dial) del Buscador Web (`atajos_navegador.json`). |
| `Services/AdBlockerService.cs` | `93` | Bloqueador de anuncios y rastreadores integrado en `WebView2`. |

---

## 🧭 3. Coordenadas Exactas en `MainWindow.xaml`

* **Líneas `42 - 413` (`HomeMenuGrid`) — Menú Principal (Home):**
  * Logo 8-bit `CHASCLIP` y subtítulo `"DESCARGADOR UNIVERSAL MULTIMEDIA"`.
  * Tarjeta 1 (`Línea 183`): **Descargador Directo** (`EnterDownloaderCard_Click`).
  * Tarjeta 2 (`Línea 248`): **Buscador Web** (`EnterBrowserCard_Click`).
  * Botón inferior (`Línea 318`): **🧪 BETA — Características Experimentales** (`EnterSearchCard_Click`). *(Único lugar donde existe el botón BETA)*.
* **Líneas `415 - 1582` (`SearchAreaGrid`) — Apartado Experimental (BETA):**
  * Panel lateral de sitios (`Líneas 430-810`), barra de búsqueda (`Líneas 815-1150`) y cuadrícula 3x4 (`Líneas 1155-1580`).
* **Líneas `1584 - 2184` (`DownloaderAreaGrid`) — Descargador Directo por Enlace:**
  * Barra superior sin botón BETA (`Líneas 1595-1670`), formulario de URL/formato/calidad (`Líneas 1680-1980`) y progreso (`Líneas 1985-2180`).
* **Líneas `2186 - 2718` (`BrowserAreaGrid`) — Buscador Web Integrado:**
  * `Grid.Row="0"` (`Líneas 2196-2358`): Barra de navegación superior con `🏠 Menú`, `⭐ Atajos`, `🕒 Historial`, barra de direcciones y botón brillante `BrowserTopDownloadGlowBorder` (`Línea 2303`).
  * `Grid.Row="1"` (`Líneas 2360-2655`):
    * `BrowserSpeedDialScrollViewer` (`Líneas 2364-2485`): Vista de Atajos + botón `"↩ Volver al Video / Página Actual"`.
    * `BrowserHistoryPageScrollViewer` (`Líneas 2487-2625`): **Apartado de Historial en Página Aparte (Solo en Lista)** + botón `"↩ Volver al Video / Página Actual"`.
    * `BrowserAirspacePlaceholderBorder` (`Líneas 2627-2650`): Placeholder anti-airspace cuando se abre un modal.
    * `BrowserWebView` (`Línea 2652`): Navegador `WebView2`.
  * `Grid.Row="2"` (`Líneas 2657-2716`): `BrowserToastBorder` y `BrowserVideoDetectionBar` (`Línea 2677`).
* **Líneas `2720 - 3575` — Modales y Cajón de Cola de Descargas:**
  * `SettingsModalOverlay` (`Línea 2723`)
  * `SnaptubeDownloadModalOverlay` (`Línea 3420`): Modal con selector múltiple de flujos detectados (`SnaptubeModalStreamsComboBox`) e insignia de formato (`SnaptubeModalStreamBadgeText`).
  * `DownloadQueueDrawerOverlay` (`Línea 3581`)

---

## ⚙️ 4. Coordenadas Exactas en `MainWindow.xaml.cs`

* **Campos del Sniffer:** `_browserDetectedMediaList`, `_selectedBrowserMediaItem`, `_lastBrowserPageUrl` (`Líneas 73 - 85`).
* **Inicialización de WebView2 y Conexión del Sniffer:** `InitializeWebViewsAsync()` (`Líneas 307 - 345`).
* **Navegación Principal:** `ShowHomeMenu()`, `ShowBrowserArea()`, `PauseOrStopBrowserMedia()` (`Líneas 1329 - 1435`).
* **Ajustes y Modo Incógnito Global:** `SettingsIncognitoCheckBox_Changed`, `UpdateIncognitoBadgeVisual()` (`Líneas 3080 - 3110`) — Bloquea el guardado en el historial de navegación del Buscador Web (`_browserHistoryService.AddEntry`) y en las búsquedas/reproducciones del apartado BETA.
* **Gestión de Sub-Vistas del Buscador Web y Airspace:** `RefreshWebViewAirspace()` (`Líneas 3191 - 3245`).
* **Botón Flotante DOM con Aura Brillante:** `BuildBrowserFloatingDownloadButtonScript()` (`Líneas 3484 - 3676`).
* **Detección de Video y Apertura de Modal:**
  * `CheckAndDetectVideoInCurrentPage()` (`Líneas 3817 - 3885`)
  * `OpenVideoDownloadModal(...)` (`Líneas 3887 - 3925`)
  * `SnaptubeModalStreamsComboBox_SelectionChanged(...)` (`Líneas 3927 - 3940`)
  * `BrowserFloatingDownloadButton_Click(...)` (`Líneas 3950 - 4020`)
  * `ConfirmSnaptubeDownload_Click(...)` (`Líneas 4040 - 4135`)
* **Módulo Sniffer Multimedia Estilo Video DownloadHelper:**
  * `AttachBrowserMediaSniffer(...)` (`Líneas 4139 - 4234`)
  * `RegisterDetectedMediaStream(...)` (`Líneas 4236 - 4324`)
  * `ClearDetectedMediaForNewPage(...)` (`Líneas 4325 - 4344`)
  * `IsAdOrTrackerUrl(...)` y `IsStaticWebAsset(...)` (`Líneas 4345 - 4395`)
* **Historial del Buscador Web (Página Aparte en Lista):** `Líneas 4397 - 4440`.
* **Cajón Lateral de Cola de Descargas:** `Líneas 4444 - 4480`.
