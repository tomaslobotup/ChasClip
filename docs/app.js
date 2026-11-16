(() => {
  const videoUrlInput = document.getElementById("videoUrlInput");
  const btnLoadVideo = document.getElementById("btnLoadVideo");
  const btnDownloadMp4 = document.getElementById("btnDownloadMp4");
  const btnDownloadMp3 = document.getElementById("btnDownloadMp3");
  const btnDownloadCurrentMp4 = document.getElementById("btnDownloadCurrentMp4");
  const btnDownloadCurrentMp3 = document.getElementById("btnDownloadCurrentMp3");
  const statusMessage = document.getElementById("statusMessage");
  const playerSection = document.getElementById("playerSection");
  const embedContainer = document.getElementById("embedContainer");
  const playerTitle = document.getElementById("playerTitle");
  const shortcutButtons = document.querySelectorAll(".shortcut-card");

  const DEFAULT_BACKEND_URL = "https://chasclip.onrender.com";

  // Instancias públicas de fallback para extracción sin backend propio
  const PUBLIC_RESOLVERS = [
    "https://api.cobalt.tools",
    "https://cobalt-api.kwiatekm.tokyo"
  ];

  function getBackendUrl() {
    return (localStorage.getItem("chasclip_backend_url") || DEFAULT_BACKEND_URL).trim();
  }

  function setStatus(text, type = "info") {
    if (!text) {
      statusMessage.className = "status-message";
      statusMessage.textContent = "";
      return;
    }
    statusMessage.className = `status-message ${type}`;
    statusMessage.textContent = text;
  }

  // Parsear URL para embed
  function parseEmbed(url) {
    if (!url) return null;
    const cleanUrl = url.trim();

    // YouTube
    const ytMatch = cleanUrl.match(/(?:youtube\.com\/(?:watch\?v=|shorts\/|embed\/)|youtu\.be\/)([a-zA-Z0-9_-]{11})/i);
    if (ytMatch) {
      return {
        type: "iframe",
        src: `https://www.youtube-nocookie.com/embed/${ytMatch[1]}?autoplay=1`,
        title: "Video de YouTube"
      };
    }

    // Dailymotion
    const dmMatch = cleanUrl.match(/dailymotion\.com\/(?:video|embed\/video)\/([a-zA-Z0-9]+)/i);
    if (dmMatch) {
      return {
        type: "iframe",
        src: `https://www.dailymotion.com/embed/video/${dmMatch[1]}?autoplay=1`,
        title: "Video de Dailymotion"
      };
    }

    // Twitch
    const twitchClip = cleanUrl.match(/clips\.twitch\.tv\/([a-zA-Z0-9_-]+)/i);
    if (twitchClip) {
      return {
        type: "iframe",
        src: `https://clips.twitch.tv/embed?clip=${twitchClip[1]}&parent=${window.location.hostname || "localhost"}&autoplay=true`,
        title: "Clip de Twitch"
      };
    }

    // Direct Video (mp4, webm, ogg)
    if (/\.(mp4|webm|ogg)($|\?)/i.test(cleanUrl)) {
      return {
        type: "video",
        src: cleanUrl,
        title: "Video directo"
      };
    }

    return null;
  }

  async function loadVideo() {
    const url = videoUrlInput.value.trim();
    if (!url) {
      setStatus("Por favor ingresa un enlace valido.", "error");
      return;
    }

    const embed = parseEmbed(url);
    if (embed) {
      embedContainer.innerHTML = "";
      if (embed.type === "iframe") {
        const iframe = document.createElement("iframe");
        iframe.src = embed.src;
        iframe.allow = "accelerometer; autoplay; clipboard-write; encrypted-media; gyroscope; picture-in-picture";
        iframe.allowFullscreen = true;
        embedContainer.appendChild(iframe);
      } else if (embed.type === "video") {
        const video = document.createElement("video");
        video.src = embed.src;
        video.controls = true;
        video.autoplay = true;
        embedContainer.appendChild(video);
      }
      playerTitle.textContent = embed.title;
      playerSection.classList.remove("hidden");
      setStatus("Video cargado en el reproductor.", "success");
    } else {
      playerSection.classList.add("hidden");
      setStatus("Enlace detectado. Listo para descargar en MP4 o MP3.", "info");
    }

    // Consultar título real al backend en segundo plano
    const backend = getBackendUrl();
    if (backend) {
      try {
        const res = await fetch(`${backend.replace(/\/+$/, "")}/api/info?url=${encodeURIComponent(url)}`);
        if (res.ok) {
          const data = await res.json();
          if (data && data.title) {
            playerTitle.textContent = data.title;
          }
        }
      } catch (e) {
      }
    }
  }

  function setDownloadButtonsState(disabled) {
    [btnDownloadMp4, btnDownloadMp3, btnDownloadCurrentMp4, btnDownloadCurrentMp3].forEach((btn) => {
      if (btn) btn.disabled = disabled;
    });
  }

  async function triggerDownload(format = "mp4") {
    const url = videoUrlInput.value.trim();
    if (!url) {
      setStatus("Por favor ingresa un enlace para descargar.", "error");
      return;
    }

    setDownloadButtonsState(true);
    setStatus(`Descargando ${format.toUpperCase()}... Por favor espera un momento mientras el servidor procesa el archivo.`, "info");

    const backend = getBackendUrl();

    // 1) Intentar mediante el backend de ChasClip API (Render)
    if (backend) {
      try {
        const downloadUrl = `${backend.replace(/\/+$/, "")}/api/download?url=${encodeURIComponent(url)}&format=${format}`;
        const res = await fetch(downloadUrl);

        if (!res.ok) {
          let errorMsg = `Error del servidor (${res.status})`;
          try {
            const errJson = await res.json();
            if (errJson && errJson.detail) errorMsg = errJson.detail;
          } catch (_) {
            const errText = await res.text();
            if (errText) errorMsg = errText;
          }
          throw new Error(errorMsg);
        }

        // Obtener nombre del archivo del header o generar uno
        let filename = `ChasClip_${Date.now()}.${format}`;
        const disposition = res.headers.get("Content-Disposition");
        if (disposition) {
          const match = /filename\*?=['"]?(?:UTF-\d['"]*)?([^;\r\n"']*)['"]?/i.exec(disposition);
          if (match && match[1]) {
            filename = decodeURIComponent(match[1]);
          }
        }

        const blob = await res.blob();
        const blobUrl = window.URL.createObjectURL(blob);
        const tempLink = document.createElement("a");
        tempLink.href = blobUrl;
        tempLink.download = filename;
        document.body.appendChild(tempLink);
        tempLink.click();
        document.body.removeChild(tempLink);
        setTimeout(() => window.URL.revokeObjectURL(blobUrl), 60000);

        setStatus(`Descarga completada con éxito: ${filename}`, "success");
        setDownloadButtonsState(false);
        return;
      } catch (err) {
        console.warn("Error en backend propio, intentando fallback:", err);
      }
    }

    // 2) Si es un enlace directo de video o audio
    if (/\.(mp4|webm|mp3)($|\?)/i.test(url)) {
      const a = document.createElement("a");
      a.href = url;
      a.download = `video_${Date.now()}.${format}`;
      a.target = "_blank";
      document.body.appendChild(a);
      a.click();
      document.body.removeChild(a);
      setStatus("Descarga iniciada directamente desde el origen.", "success");
      setDownloadButtonsState(false);
      return;
    }

    // 3) Resolver mediante instancias de alta disponibilidad
    setStatus(`Intentando procesar descarga de ${format.toUpperCase()} mediante servidores alternativos...`, "info");

    for (const resolver of PUBLIC_RESOLVERS) {
      try {
        const payload = {
          url: url,
          videoQuality: "1080",
          downloadMode: format === "mp3" ? "audio" : "auto",
          audioFormat: format === "mp3" ? "mp3" : undefined
        };

        const res = await fetch(resolver, {
          method: "POST",
          headers: {
            "Accept": "application/json",
            "Content-Type": "application/json"
          },
          body: JSON.stringify(payload)
        });

        if (res.ok) {
          const data = await res.json();
          const targetUrl = data.url || (data.audio ? data.audio : null);
          if (targetUrl) {
            const a = document.createElement("a");
            a.href = targetUrl;
            a.target = "_blank";
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            setStatus(`Descarga de ${format.toUpperCase()} generada exitosamente.`, "success");
            setDownloadButtonsState(false);
            return;
          }
        }
      } catch (e) {
      }
    }

    // Si falló todo
    setDownloadButtonsState(false);
    setStatus(
      "No se pudo completar la descarga en la web debido a bloqueos de la plataforma. Para descargar sin limitaciones, utiliza la aplicación de escritorio ChasClip para Windows.",
      "error"
    );
  }

  // Event Listeners
  btnLoadVideo.addEventListener("click", loadVideo);
  videoUrlInput.addEventListener("keydown", (e) => {
    if (e.key === "Enter") loadVideo();
  });

  btnDownloadMp4.addEventListener("click", () => triggerDownload("mp4"));
  btnDownloadMp3.addEventListener("click", () => triggerDownload("mp3"));
  btnDownloadCurrentMp4.addEventListener("click", () => triggerDownload("mp4"));
  btnDownloadCurrentMp3.addEventListener("click", () => triggerDownload("mp3"));

  // Atajos
  shortcutButtons.forEach((btn) => {
    btn.addEventListener("click", () => {
      const siteUrl = btn.dataset.url;
      videoUrlInput.value = siteUrl;
      videoUrlInput.focus();
      setStatus(`Pega el enlace del video que desees de ${btn.querySelector(".shortcut-title").textContent}.`, "info");
    });
  });

  // Resolver enlace dinamico a la ultima release de GitHub
  async function resolveLatestReleaseSetup() {
    const fallback = "https://github.com/tomaslobotup/ChasClip/releases/latest/download/ChasClip-Setup.exe";
    try {
      const res = await fetch("https://api.github.com/repos/tomaslobotup/ChasClip/releases/latest");
      if (res.ok) {
        const data = await res.json();
        const asset = (data.assets || []).find((a) => a.name && a.name.toLowerCase().endsWith(".exe"));
        if (asset && asset.browser_download_url) {
          applySetupUrl(asset.browser_download_url);
          return;
        }
      }
    } catch (e) {
    }
    applySetupUrl(fallback);
  }

  function applySetupUrl(url) {
    const downloadBtns = document.querySelectorAll("a[href*='download/ChasClip-Setup.exe']");
    downloadBtns.forEach((btn) => {
      btn.href = url;
    });
  }

  resolveLatestReleaseSetup();
})();
