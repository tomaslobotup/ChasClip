# BUSCAPP — Buscador Multisitio Universal de Videos

[![Plataforma](https://img.shields.io/badge/Windows-10%20%2F%2011%20(64--bit)-0078D6?logo=windows&logoColor=white)](https://github.com/tomaslobotup/BUSCAPP/releases/latest)
[![.NET](https://img.shields.io/badge/.NET-8.0%20WPF-512BD4?logo=dotnet&logoColor=white)](https://dotnet.microsoft.com/)
[![Cafecito](https://img.shields.io/badge/Apoyar-Cafecito%20☕-FF813F)](https://cafecito.app/atomichead)

**BUSCAPP** es una aplicación de escritorio para Windows desarrollada en **C# (WPF .NET 8)** que permite buscar, previsualizar en movimiento, reproducir con control de calidad y descargar videos simultáneamente desde múltiples plataformas de video desde una única interfaz oscura, rápida y sin publicidad.

---

## ⬇️ Descarga e Instalación (Para probar el programa)

No necesitas instalar `.NET` ni compilar código para usar **BUSCAPP**:

1. Ve a la sección de [**Releases**](https://github.com/tomaslobotup/BUSCAPP/releases/latest) o descarga directamente el instalador:
   👉 **[Descargar `BUSCAPP-Setup.exe` (Última versión)](https://github.com/tomaslobotup/BUSCAPP/releases/latest/download/BUSCAPP-Setup.exe)**
2. Ejecuta **`BUSCAPP-Setup.exe`** (si Windows SmartScreen muestra un aviso por ser un ejecutable independiente sin firma comercial, haz clic en *"Más información"* → *"Ejecutar de todas formas"*).
3. Sigue el asistente de instalación y abre **BUSCAPP** desde el acceso directo en tu Escritorio o Menú Inicio.

> **Requisito del sistema**: Windows 10 o Windows 11 (64 bits) con *Microsoft Edge WebView2* (incluido de serie en Windows 10/11).

---

## ✨ Características principales

- **Animación de apertura e interfaz oscura pulida**:
  - Pantalla de bienvenida animada al iniciar el programa y tema oscuro moderno con controles, barras de desplazamiento, selectores y menús estilizados.
  - **Panel lateral ocultable**: Muestra u oculta el panel izquierdo con animación suave mediante el botón `☰` / `◀` o el atajo `Ctrl + B`.
- **Lista predeterminada de buscadores verificados + Gestión con Deshacer**:
  - Incluye de fábrica buscadores listos para usar (**YouTube, Dailymotion, TikTok, Vimeo, Bilibili, Rumble, Reddit Videos y Twitch**), además de permitir agregar cualquier sitio web o plantilla de búsqueda con `{q}`.
  - Botón **`↶ Revertir borrado en mi lista`** para restaurar páginas eliminadas en su posición original y botón **`↺ Base`** para recuperar los buscadores predeterminados.
  - Catálogo opcional **NSFW de video directo** (depurado sin sitios en formato serie para garantizar reproducción directa limpia).
- **Motor de Miniaturas de alta compatibilidad y Vista Previa al pasar el mouse**:
  - Negociación HTTP dedicada para miniaturas WebP/JPEG/PNG, reintento automático de `Referer`, parser seguro de `srcset`, extracción de `thid` y doble decodificador (**SkiaSharp `SKCodec` + respaldo Windows WIC**) con cascada de fotogramas de respaldo.
  - Vista previa silenciosa en movimiento al pasar el cursor sobre una tarjeta (clips MP4/WebM, WebP/GIF animados o tiras de escenas).
- **Historial desplegable en la búsqueda, Revertir y Borrar**:
  - **Historial desplegable (`🕒▾`)** integrado directamente bajo la barra de búsqueda con borrado individual (`✕`) y borrado completo (`🗑️ Borrar todo`).
  - Botón **`↩ Revertir`** para volver instantáneamente a los resultados de la búsqueda anterior y botón **`🧹 Borrar`** para limpiar la sesión actual.
- **Bloqueador de anuncios integrado con advertencia de seguridad**:
  - Filtra redes publicitarias, ventanas emergentes (*popups/popunders*) y capas invisibles de *clickjacking* para evitar errores de carga.
  - Incluye el interruptor **`🛡️ AdBlock: ON / OFF`**, mostrando un cartel de advertencia antes de desactivarlo indicando que puede ocasionar errores.
- **Reproductor limpio con Regulador de Calidad, Descarga y Cartel de Incompatibilidad**:
  - **Regulador de calidad** (`Automática`, `1080p`, `720p`, `480p`, `360p`, `240p`) para ajustar la reproducción en tiempo real.
  - **Botón `⬇ Descargar`** que guarda el video en formato `.mp4` en la calidad exacta seleccionada en el regulador (soportando descargas HTTP directas autenticadas, variantes HLS `.m3u8` y extracción universal automática con `yt-dlp` + `FFmpeg`).
  - **Cartel de incompatibilidad**: Si un enlace o sitio externo bloquea la incrustación o extracción del video, muestra un aviso claro con opción directa para abrirlo en el navegador externo.

---

## 🛠️ Compilar desde el código fuente (Desarrolladores)

Si deseas modificar o compilar el proyecto por tu cuenta (requiere **.NET 8 SDK** o superior en Windows):

1. **Desde Visual Studio 2022 / 2026**:
   - Abre `BuscadorVideos.sln` y presiona **F5**.
2. **Desde terminal (`.NET CLI`)**:
   ```powershell
   dotnet run --project BuscadorVideos/BuscadorVideos.csproj
   ```
3. **Publicar versión independiente (`win-x64`)**:
   ```powershell
   dotnet publish BuscadorVideos/BuscadorVideos.csproj -c Release -r win-x64 --self-contained true -o publish
   ```

---

## ☕ Apoyar el proyecto

Si **BUSCAPP** te resulta útil y quieres apoyar su desarrollo para seguir sumando mejoras:

👉 **[Regálame un cafecito en cafecito.app/atomichead](https://cafecito.app/atomichead)**
