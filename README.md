# ChasClip — Descargador Universal Multimedia

[![Plataforma](https://img.shields.io/badge/Windows-10%20%2F%2011%20(64--bit)-0078D6?logo=windows&logoColor=white)](https://github.com/tomaslobotup/ChasClip/releases/latest)
[![.NET](https://img.shields.io/badge/.NET-8.0%20WPF-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Estilo](https://img.shields.io/badge/Diseño-Pixel%208--Bit-crimson)](#)
[![Cafecito](https://img.shields.io/badge/Apoyar-Cafecito%20☕-FF813F)](https://cafecito.app/atomichead)

**ChasClip** es un software de escritorio para Windows desarrollado en **C# (.NET 8 WPF)** que funciona como un **Descargador Universal Multimedia**, reproductor integrado y navegador web especializado. Permite detectar, encolar y descargar cualquier video o audio MP3 desde la web con máxima velocidad, calidad personalizable y una interfaz limpia en tonos oscuros Void/Maroon con estética pixel 8-bit.

---

## ⚡ Características Principales

* 🌐 **Buscador Web Integrado con Atajos:** Navega en cualquier portal o red social (YouTube, TikTok, Twitch, Instagram, portales de anime y series) manteniendo tus cuentas y sesiones activas.
* 🛰️ **Sniffer Multimedia Inteligente (Estilo Video DownloadHelper):** Detecta automáticamente flujos de video directos (MP4, WebM) y manifiestos de streaming (HLS `.m3u8` y DASH `.mpd`), incluso cuando se reproducen dentro de iframes externos o reproductores incrustados (JWPlayer, Zilla, Voe, etc.).
* ⬇️ **Botón Flotante con Aura Brillante:** Al estar viendo un video en el navegador, aparece un botón flotante con aura de color dinámico para descargarlo en un clic.
* 📥 **Cola de Descargas Secuencial:** Agrega descargas a una lista de espera que procesa descargas en segundo plano con barra de progreso en vivo, soporte de pausa, reintento y persistencia.
* 🎵 **Extracción de Audio a MP3:** Descarga videos completos en alta definición (hasta 4K/1080p) o extrae directamente la pista de audio en formato MP3 de 320 kbps.
* 🕶️ **Modo Incógnito Completo:** Al activarse, no se guardan búsquedas ni páginas en el historial del Buscador Web ni en el apartado experimental.
* 🧪 **Apartado Experimental (BETA):** Motor de búsqueda multisitio universal que consulta y previsualiza resultados de video simultáneamente en múltiples plataformas con cuadrícula configurable y filtros avanzados.
* 🛡️ **Bloqueador de Anuncios Integrado:** AdBlocker activo para evitar ventanas emergentes molestas y rastreadores mientras navegas.

---

## ⬇️ Descarga e Instalación

No necesitas compilar código ni instalar dependencias para usar **ChasClip**:

1. Descarga el instalador oficial desde la sección de [**Releases**](https://github.com/tomaslobotup/ChasClip/releases/latest):
   👉 **[Descargar ChasClip-Setup.exe](https://github.com/tomaslobotup/ChasClip/releases/latest/download/ChasClip-Setup.exe)**
2. Ejecuta el instalador **`ChasClip-Setup.exe`** y sigue el asistente.
3. Abre **ChasClip** desde el acceso directo creado en tu **Escritorio** o en el **Menú Inicio**.

> **Requisito del sistema:** Windows 10 o Windows 11 (64-bit) con *Microsoft Edge WebView2* (incluido de serie en Windows 10 y 11).

---

## 🛠️ Tecnologías y Arquitectura

* **Lenguaje & Framework:** C# 12 / .NET 8.0 (Windows Presentation Foundation - WPF).
* **Motor Web:** Microsoft Edge WebView2 (Chromium nativo aislado con WebResource sniffing).
* **Backend de Descarga y Muxing:** FFmpeg con remuxing multiformato y soporte yt-dlp para plataformas con cifrado DASH.
* **Diseño Visual:** Algoritmo dinámico de paleta de 4 colores (`Dark Void`, `Dark Maroon`, `Maui Mist`, `Shining Knight`), aceleración por hardware y animaciones fluidas.
* **Instalador:** Inno Setup 6 x64.

---

## ☕ Apoyar el Proyecto

Si **ChasClip** te resulta útil y quieres apoyar su desarrollo continuo:

👉 **[Invítame un cafecito en cafecito.app/atomichead](https://cafecito.app/atomichead)**
