"""
Streaming-FLAC acquisition sidecar for MusicHoarder.

A thin FastAPI wrapper around the `SpotiFLAC` pip module (v1.3.1). MusicHoarder's C# side
(`StreamingFlacDownloadProvider` / `StreamingFlacSidecarClient`) treats this as an opaque HTTP
service and owns ALL metadata — so this wrapper runs with enrichment and lyrics OFF and writes a
single, untagged FLAC into the shared staging volume.

Contract (must match StreamingFlacSidecarClient.cs):
  GET  /health   -> 200 {"status":"ok","providers":[...]}
  POST /acquire  -> {"status":"ok","file":"<abs path>","provider":"qobuz"}
                    {"status":"not_found","error":"..."}   # C# maps -> Missing (fall through)
                    {"status":"error","error":"..."}       # C# maps -> Failed (stop the chain)

This directory is intentionally gitignored — it wraps a legally-grey module and must never enter the
public MusicHoarder image/history. Build and run it yourself; see README.md.
"""
import os
import logging
from typing import Optional

from fastapi import FastAPI
from fastapi.concurrency import run_in_threadpool
from pydantic import BaseModel

logging.basicConfig(level=logging.INFO)
logger = logging.getLogger("spotiflac-sidecar")

# Default lossless providers if a request doesn't specify. Amazon is out of scope (CENC decryption).
DEFAULT_SERVICES = ["qobuz", "tidal"]

# Optional self-hosted backends (point at your own hifi-api / Qobuz-DL instead of the community relay).
TIDAL_CUSTOM_API = os.environ.get("SPOTIFLAC_TIDAL_CUSTOM_API") or None
QOBUZ_LOCAL_API_URL = os.environ.get("SPOTIFLAC_QOBUZ_LOCAL_API_URL") or None

app = FastAPI(title="spotiflac-sidecar")


class AcquireRequest(BaseModel):
    spotify_url: str
    quality: str = "LOSSLESS"
    services: Optional[list[str]] = None
    output_dir: str
    filename_stem: str
    timeout_s: int = 120


@app.get("/health")
async def health():
    """Probe the lossless providers and report which are reachable."""
    try:
        from SpotiFLAC.core.health_check import run_health_check, get_working_providers

        results = await run_in_threadpool(run_health_check, DEFAULT_SERVICES)
        providers = get_working_providers(results)
        return {"status": "ok", "providers": list(providers or [])}
    except Exception as exc:  # noqa: BLE001 — health must never throw; report empty instead.
        logger.warning("health check failed: %s", exc)
        return {"status": "ok", "providers": []}


@app.post("/acquire")
async def acquire(req: AcquireRequest):
    """
    Acquire one Spotify track as a single lossless FLAC at
    ``{output_dir}/{filename_stem}.flac`` inside the shared staging volume.
    """
    services = req.services or DEFAULT_SERVICES
    os.makedirs(req.output_dir, exist_ok=True)
    output_path = os.path.join(req.output_dir, f"{req.filename_stem}.flac")

    try:
        # The module is side-effect driven (writes the file; return value is undocumented), so we
        # verify the file on disk afterwards to decide ok vs not_found. Run it off the event loop —
        # it's a long, blocking network/CPU call.
        await run_in_threadpool(_run_spotiflac, req, services, output_path)
    except Exception as exc:  # noqa: BLE001
        message = str(exc)
        # "No lossless source for this track" ⇒ not_found so MusicHoarder falls through to the next
        # provider; anything else (timeout, community-server 5xx/cooldown, network) ⇒ error.
        if _looks_like_no_source(message):
            logger.info("no lossless source for %s: %s", req.spotify_url, message)
            return {"status": "not_found", "error": message}
        logger.warning("acquire failed for %s: %s", req.spotify_url, message)
        return {"status": "error", "error": message}

    if os.path.exists(output_path) and os.path.getsize(output_path) > 0:
        return {"status": "ok", "file": output_path, "provider": services[0]}

    # Completed without raising but produced nothing — treat as no source found.
    logger.info("no file produced for %s (no lossless source)", req.spotify_url)
    return {"status": "not_found", "error": "no lossless source produced"}


def _run_spotiflac(req: AcquireRequest, services: list[str], output_path: str) -> None:
    from SpotiFLAC import SpotiFLAC

    SpotiFLAC(
        url=req.spotify_url,
        output_dir=req.output_dir,
        output_path=output_path,   # exact single-file destination (overrides filename_format)
        services=services,
        quality=req.quality or "LOSSLESS",
        allow_fallback=True,       # HI_RES -> LOSSLESS is fine; still lossless
        enrich_metadata=False,     # MusicHoarder owns metadata
        embed_lyrics=False,        # MusicHoarder owns lyrics
        timeout_s=req.timeout_s,
        track_max_retries=1,
        tidal_custom_api=TIDAL_CUSTOM_API,
        qobuz_local_api_url=QOBUZ_LOCAL_API_URL,
    )


def _looks_like_no_source(message: str) -> bool:
    m = message.lower()
    return any(s in m for s in ("no source", "not found", "no lossless", "unavailable", "not available"))
