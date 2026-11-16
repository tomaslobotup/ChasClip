import os
import tempfile
import uuid
from typing import Optional
from fastapi import FastAPI, HTTPException, Query, BackgroundTasks
from fastapi.middleware.cors import CORSMiddleware
from fastapi.responses import FileResponse, JSONResponse
import yt_dlp

app = FastAPI(title="ChasClip Web API", version="1.0.0")

app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
    expose_headers=["Content-Disposition"],
)

TEMP_DIR = os.path.join(tempfile.gettempdir(), "chasclip_server_downloads")
os.makedirs(TEMP_DIR, exist_ok=True)


def cleanup_file(path: str):
    try:
        if os.path.exists(path):
            os.remove(path)
    except Exception:
        pass


@app.get("/api/health")
def health():
    return {
        "status": "ok",
        "service": "ChasClip Web API",
        "yt_dlp_version": getattr(yt_dlp.version, "__version__", "unknown"),
    }


@app.get("/api/info")
def get_info(url: str = Query(..., description="URL del video")):
    if not url:
        raise HTTPException(status_code=400, detail="URL requerida")

    ydl_opts = {
        "quiet": True,
        "no_warnings": True,
        "skip_download": True,
        "geo_bypass": True,
        "nocheckcertificate": True,
        "extractor_args": {
            "youtube": {
                "player_client": ["android", "ios"]
            }
        },
    }

    try:
        with yt_dlp.YoutubeDL(ydl_opts) as ydl:
            info = ydl.extract_info(url, download=False)
            return {
                "title": info.get("title", "Video"),
                "thumbnail": info.get("thumbnail"),
                "duration": info.get("duration"),
                "uploader": info.get("uploader"),
                "url": url,
            }
    except Exception as e:
        raise HTTPException(status_code=400, detail=str(e))


@app.get("/api/download")
def download_media(
    background_tasks: BackgroundTasks,
    url: str = Query(..., description="URL del video"),
    format: str = Query("mp4", description="mp4 o mp3"),
):
    if not url:
        raise HTTPException(status_code=400, detail="URL requerida")

    req_id = str(uuid.uuid4())[:8]

    common_extractor_args = {
        "youtube": {
            "player_client": ["android", "ios"]
        }
    }

    if format.lower() == "mp3":
        out_template = os.path.join(TEMP_DIR, f"{req_id}_%(title)s.%(ext)s")
        ydl_opts = {
            "format": "bestaudio/best",
            "outtmpl": out_template,
            "geo_bypass": True,
            "nocheckcertificate": True,
            "extractor_args": common_extractor_args,
            "postprocessors": [
                {
                    "key": "FFmpegExtractAudio",
                    "preferredcodec": "mp3",
                    "preferredquality": "320",
                }
            ],
            "quiet": True,
            "no_warnings": True,
        }
    else:
        out_template = os.path.join(TEMP_DIR, f"{req_id}_%(title)s.%(ext)s")
        ydl_opts = {
            "format": "bestvideo[ext=mp4]+bestaudio[ext=m4a]/best[ext=mp4]/best",
            "outtmpl": out_template,
            "geo_bypass": True,
            "nocheckcertificate": True,
            "extractor_args": common_extractor_args,
            "merge_output_format": "mp4",
            "quiet": True,
            "no_warnings": True,
        }

    try:
        with yt_dlp.YoutubeDL(ydl_opts) as ydl:
            info = ydl.extract_info(url, download=True)
            filename = ydl.prepare_filename(info)

            if format.lower() == "mp3":
                base, _ = os.path.splitext(filename)
                target_file = f"{base}.mp3"
            else:
                base, _ = os.path.splitext(filename)
                target_file = f"{base}.mp4"
                if not os.path.exists(target_file) and os.path.exists(filename):
                    target_file = filename

            if not os.path.exists(target_file):
                raise HTTPException(status_code=500, detail="Error al generar el archivo.")

            media_title = info.get("title", "archivo").replace('"', "").strip()
            ext = "mp3" if format.lower() == "mp3" else "mp4"
            download_name = f"{media_title}.{ext}"

            background_tasks.add_task(cleanup_file, target_file)

            return FileResponse(
                path=target_file,
                filename=download_name,
                media_type="audio/mpeg" if ext == "mp3" else "video/mp4",
            )
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))


if __name__ == "__main__":
    import uvicorn

    port = int(os.environ.get("PORT", 8000))
    uvicorn.run("main:app", host="0.0.0.0", port=port)
