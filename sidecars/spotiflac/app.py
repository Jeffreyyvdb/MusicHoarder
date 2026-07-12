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

Opt-in and off by default — see README.md. Although the only caller is MusicHoarder's own API (which
sends a GUID stem and the configured staging dir), the request fields are still validated here:
the write path is confined to SPOTIFLAC_STAGING_DIR and the filename is restricted to a safe charset,
so a malformed/hostile request can't traverse out of the staging volume, and error responses never
carry raw exception text.
"""
import os
import re
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

# All writes are confined under this root (the shared staging volume, mounted at the same path the API
# sees). A request's output_dir must resolve to it or a subdirectory; anything else is rejected.
STAGING_ROOT = os.path.realpath(os.environ.get("SPOTIFLAC_STAGING_DIR", "/data/downloads"))

# Filenames are caller-supplied but must be a bare, safe stem (the API sends a GUID hex). This blocks
# path separators, "..", NUL, etc. before the value is ever used to build a filesystem path.
_STEM_RE = re.compile(r"^[A-Za-z0-9_-]{1,128}$")

app = FastAPI(title="spotiflac-sidecar")


class AcquireRequest(BaseModel):
    spotify_url: str
    quality: str = "LOSSLESS"
    services: Optional[list[str]] = None
    output_dir: str
    filename_stem: str
    timeout_s: int = 120


def _safe_output_path(output_dir: str, filename_stem: str) -> Optional[str]:
    """
    Resolve the absolute FLAC path for this request, or None if it would escape the staging root or
    the filename isn't a safe bare stem. Both checks act as sanitizers before any path is used on disk.
    """
    if not _STEM_RE.fullmatch(filename_stem):
        return None

    base = os.path.realpath(output_dir)
    if base != STAGING_ROOT and not base.startswith(STAGING_ROOT + os.sep):
        return None

    candidate = os.path.realpath(os.path.join(base, filename_stem + ".flac"))
    if candidate != os.path.join(base, filename_stem + ".flac"):
        # realpath changed the string (symlink / traversal) — reject rather than follow it.
        return None
    if not candidate.startswith(STAGING_ROOT + os.sep):
        return None
    return candidate


@app.get("/health")
async def health():
    """Probe the lossless providers and report which are reachable."""
    try:
        from SpotiFLAC.core.health_check import run_health_check, get_working_providers

        results = await run_in_threadpool(run_health_check, DEFAULT_SERVICES)
        providers = get_working_providers(results)
        return {"status": "ok", "providers": list(providers or [])}
    except Exception:  # noqa: BLE001 — health must never throw; report empty instead.
        logger.warning("health check failed", exc_info=True)
        return {"status": "ok", "providers": []}


@app.post("/acquire")
async def acquire(req: AcquireRequest):
    """
    Acquire one Spotify track as a single lossless FLAC named ``{filename_stem}.flac`` inside the
    request's output_dir, which must resolve within the staging root.
    """
    services = req.services or DEFAULT_SERVICES

    output_path = _safe_output_path(req.output_dir, req.filename_stem)
    if output_path is None:
        logger.warning("rejected unsafe output path (dir=%r stem=%r)", req.output_dir, req.filename_stem)
        return {"status": "error", "error": "invalid output path"}

    os.makedirs(os.path.dirname(output_path), exist_ok=True)

    try:
        # The module is side-effect driven (writes the file; return value is undocumented), so we
        # verify the file on disk afterwards to decide ok vs not_found. Run it off the event loop —
        # it's a long, blocking network/CPU call.
        await run_in_threadpool(_run_spotiflac, req, services, output_path)
    except Exception as exc:  # noqa: BLE001
        # Classify on the exception text internally, but never return it — a generic message keeps
        # stack/internal detail out of the HTTP response (the C# side only logs the string).
        if _looks_like_no_source(str(exc)):
            logger.info("no lossless source for %s", req.spotify_url, exc_info=True)
            return {"status": "not_found", "error": "no lossless source"}
        logger.warning("acquire failed for %s", req.spotify_url, exc_info=True)
        return {"status": "error", "error": "acquisition failed"}

    if os.path.exists(output_path) and os.path.getsize(output_path) > 0:
        return {"status": "ok", "file": output_path, "provider": services[0]}

    # Completed without raising but produced nothing — treat as no source found.
    logger.info("no file produced for %s (no lossless source)", req.spotify_url)
    return {"status": "not_found", "error": "no lossless source"}


def _run_spotiflac(req: AcquireRequest, services: list[str], output_path: str) -> None:
    from SpotiFLAC import SpotiFLAC

    SpotiFLAC(
        url=req.spotify_url,
        output_dir=os.path.dirname(output_path),
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
