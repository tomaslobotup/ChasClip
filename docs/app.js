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

  // Modal de Ajustes
  const btnSettings = document.getElementById("btnSettings");
  const settingsModal = document.getElementById("settingsModal");
  const btnCloseModal = document.getElementById("btnCloseModal");
  const btnSaveSettings = document.getElementById("btnSaveSettings");
  const apiUrlInput = document.getElementById("apiUrlInput");

  // Instancias públicas de fallback para extracción sin backend propio
  const PUBLIC_RESOLVERS = [
    "https://api.cobalt.tools",
    "https://cobalt-api.kwiatekm.tokyo"
  ];

  function getBackendUrl() {
    return (localStorage.getItem("chasclip_backend_url") || "").trim();
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

  function loadVideo() {
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
      setStatus("Enlace detectado. Puedes proceder con la descarga directa en MP4 o MP3.", "info");
    }
  }

  async function triggerDownload(format = "mp4") {
    const url = videoUrlInput.value.trim();
    if (!url) {
      setStatus("Por favor ingresa un enlace para descargar.", "error");
      return;
    }

    const customBackend = getBackendUrl();

    // 1) Si hay backend propio configurado
    if (customBackend) {
      setStatus(`Solicitando descarga en formato ${format.toUpperCase()} a tu servidor backend...`, "info");
      const downloadUrl = `${customBackend.replace(/\/+$/, "")}/api/download?url=${encodeURIComponent(url)}&format=${format}`;
      window.open(downloadUrl, "_blank");
      setStatus("Peticion enviada al backend. La descarga comenzara en breve.", "success");
      return;
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
      return;
    }

    // 3) Resolver mediante instancias de alta disponibilidad
    setStatus(`Procesando descarga de ${format.toUpperCase()} mediante el servicio en la nube...`, "info");

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
            return;
          }
        }
      } catch (e) {
        // Continuar con el siguiente resolver
      }
    }

    // Si no se pudo procesar en la web por restricciones de plataforma
    setStatus(
      "Esta plataforma requiere descargar desde la aplicacion de escritorio ChasClip para saltar restricciones del navegador. Descarga el instalador mas abajo.",
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

  // Modal
  btnSettings.addEventListener("click", () => {
    apiUrlInput.value = getBackendUrl();
    settingsModal.classList.remove("hidden");
  });

  btnCloseModal.addEventListener("click", () => {
    settingsModal.classList.add("hidden");
  });

  btnSaveSettings.addEventListener("click", () => {
    const val = apiUrlInput.value.trim();
    localStorage.setItem("chasclip_backend_url", val);
    settingsModal.classList.add("hidden");
    if (val) {
      setStatus(`Servidor backend guardado: ${val}`, "success");
    } else {
      setStatus("Servidor backend restablecido al predeterminado.", "info");
    }
  });

  window.addEventListener("click", (e) => {
    if (e.target === settingsModal) {
      settingsModal.classList.add("hidden");
    }
  });
})();
