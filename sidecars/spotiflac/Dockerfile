# Streaming-FLAC acquisition sidecar for MusicHoarder. Opt-in and off by default — pulled from GHCR
# only when the `spotiflac` Compose profile is active; never part of the published api/frontend images.
FROM python:3.12-slim

# ffmpeg for any remux the module performs; ca-certificates for HTTPS to the provider backends.
RUN apt-get update \
    && apt-get install -y --no-install-recommends ffmpeg ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app

COPY requirements.txt .
RUN pip install --no-cache-dir -r requirements.txt

COPY app.py .

# The shared staging dir is bind-mounted here at the SAME absolute path the API sees, so the file
# paths returned to MusicHoarder resolve on both sides (see docker-compose in docs/SELF_HOSTING.md).
EXPOSE 8000

HEALTHCHECK --interval=30s --timeout=10s --start-period=20s \
    CMD python -c "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://localhost:8000/health').status==200 else 1)"

CMD ["uvicorn", "app:app", "--host", "0.0.0.0", "--port", "8000"]
