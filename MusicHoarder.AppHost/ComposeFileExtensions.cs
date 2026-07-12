using Aspire.Hosting.Docker.Resources;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes.Swarm;

/// <summary>
/// Which deploy target a published compose file is shaped for. The app + env contract is shared
/// across all targets (that is the whole point of generating them from one AppHost); only the
/// orchestrator-specific shape — networking, rollout, image refs, ports, volumes — differs.
/// Selected at publish time via the <c>DEPLOY_TARGET</c> config value (see AppHost.cs).
/// </summary>
internal enum DeployTarget
{
    /// <summary>musichoarder.app — deployed as a Docker Stack (Swarm) under Dokploy. The default.</summary>
    Swarm,

    /// <summary>Per-PR preview stack — plain <c>docker compose up</c> via scripts/dokploy-preview.sh.</summary>
    Compose,

    /// <summary>Published self-host template — plain <c>docker compose up</c>, GHCR-pulled images.</summary>
    SelfHost,
}

/// <summary>
/// Post-processing applied to the Aspire-generated Docker Compose file
/// (<c>aspire publish</c> → <c>aspire-output/docker-compose.yaml</c>) that Dokploy deploys.
/// Kept out of <c>AppHost.cs</c> so the composition root stays focused on resource wiring.
/// </summary>
internal static class ComposeFileExtensions
{
    /// <summary>
    /// Applies every MusicHoarder-specific tweak the published compose needs, shaped for the requested
    /// <paramref name="target"/>. The app/env contract is shared; the dispatch below picks the
    /// orchestrator-specific shape so all deploy targets stay generated from this one AppHost.
    /// </summary>
    public static void ConfigureMusicHoarderDeployment(this ComposeFile file, DeployTarget target)
    {
        switch (target)
        {
            case DeployTarget.Swarm:
                file.ConfigureForSwarm();
                break;
            case DeployTarget.Compose:
                file.ConfigureForPreview();
                break;
            case DeployTarget.SelfHost:
                file.ConfigureForSelfHost();
                break;
            default:
                throw new NotSupportedException($"Deploy target '{target}' is not implemented.");
        }
    }

    /// <summary>
    /// musichoarder.app shape: deployed as a Docker Stack (Swarm) under Dokploy — overlay networks,
    /// swarm <c>deploy:</c>/<c>update_config</c> rollout, no <c>pull_policy</c>/top-level <c>restart</c>,
    /// host-bind music library + demo media. This is the body that shipped before the target seam, kept
    /// verbatim so the generated prod compose is byte-for-byte unchanged.
    /// </summary>
    private static void ConfigureForSwarm(this ComposeFile file)
    {
        var api = file.Services["api"];
        var frontend = file.Services["frontend"];

        // Do NOT set pull_policy: `docker stack deploy` rejects it ("Additional property pull_policy
        // is not allowed"). Swarm pulls on image-reference change, so re-pulling a mutable :latest
        // tag relies on the deploy running with `--resolve-image always` (or, more robustly, pinning
        // API_IMAGE/FRONTEND_IMAGE to immutable :X.Y.Z tags). Keep it unset so the file stays valid
        // for both `docker stack deploy` and plain `docker compose up`.
        api.PullPolicy = null;
        frontend.PullPolicy = null;

        // The Aspire dashboard service carries a top-level `restart: always`, which `docker stack
        // deploy` rejects the same way ("Additional property restart is not allowed"). Swap it for a
        // swarm restart_policy. Guarded in case the dashboard is disabled (WithProperties).
        if (file.Services.TryGetValue("compose-dashboard", out var dashboard))
        {
            dashboard.Restart = null;
            dashboard.WithStopFirstUpdate();
        }

        // Swarm services can only attach to swarm-scoped networks. Aspire emits the inter-service
        // `aspire` network as a plain bridge, which `docker stack deploy` rejects ("The network ...
        // cannot be used with services. Only networks scoped to the swarm can be used, such as those
        // created with the overlay driver."). Promote every network to an attachable overlay. Dokploy
        // separately attaches the public service to its own `dokploy-network` and injects the Traefik
        // labels when a Domain is configured, so inter-service traffic just needs this overlay.
        foreach (var network in file.Networks.Values)
        {
            network.Driver = "overlay";
            network.Attachable = true;
        }

        file.PersistDataProtectionKeys(api);
        MountMusicLibrary(api);
        MountDemoMedia(api);
        file.ConfigureWishlistDownloads(api);
        file.MountSyncedSource(api);

        // ── Zero-downtime deploys ──────────────────────────────────────────────────────────────
        // Dokploy "Compose" deploys (`docker compose up`) stop the old container before the new one
        // is ready → a 502 window every release. Running the stack as a Docker Stack (swarm) instead
        // lets `update_config` gate the swap on a healthcheck. The Aspire-published images carry no
        // Docker HEALTHCHECK (those in the root Dockerfile / frontend/Dockerfile are only used by the
        // build-from-source compose), so the probes are defined here — without one swarm treats
        // "process started" as "healthy" and swaps too early. These blocks are inert under plain
        // `docker compose up`. (The api listens on ${API_PORT} (HTTP_PORTS), so probe that.)
        //
        // Both services use start-first (zero-downtime: the new task must pass its healthcheck before
        // the old one is removed). The frontend is a stateless SSR/proxy, so overlap is free.
        //
        // The api hosts the singleton pipeline (Scanner / Fingerprint / Enrichment / LibraryBuilder),
        // which is NOT designed to run as two concurrent processes — workers don't claim DB rows
        // atomically, the one-job-at-a-time JobManager is in-memory (per-process), the
        // LibraryBuilder's destination locks are in-process, and a new instance's startup EF migration
        // can disrupt a still-running old one. start-first briefly overlaps two api tasks during a
        // deploy, so that's a known, accepted risk for this low-frequency single-user deploy (deploys
        // usually land while the pipeline is idle). Mitigations: keep api replicas at 1 (so two cold
        // starts never race), and keep migrations backward-compatible. Switch the api to
        // WithStopFirstUpdate() if you ever want to eliminate the overlap entirely.
        api.WithHttpHealthcheck("${API_PORT}", "/alive", startPeriod: "40s").WithRollingUpdate();
        frontend.WithHttpHealthcheck("8001", "/api/health", startPeriod: "20s").WithRollingUpdate();

        // Postgres has a single data volume, so it must never run two tasks at once — stop-first
        // releases the volume before any replacement claims it. (Usually a no-op on app-only deploys:
        // swarm leaves postgres untouched when its image tag doesn't change.)
        file.Services["postgres"].WithStopFirstUpdate();

        ApplyProviderEnvDefaults(api);

        // Aspire emits `depends_on` in the long (map+condition) form, which `docker stack deploy`
        // rejects ("depends_on must be a list"). Swarm ignores depends_on conditions regardless, and
        // startup ordering is already tolerated at runtime (the API retries Postgres via Npgsql; the
        // frontend's /api/health probe is independent of the API), so drop it for the published
        // compose. (The build-from-source root docker-compose.yml keeps its own healthy-gated
        // depends_on — this only affects the Aspire/Dokploy stack.)
        api.DependsOn.Clear();
        frontend.DependsOn.Clear();
    }

    /// <summary>
    /// Per-PR preview shape: plain <c>docker compose up</c> via scripts/dokploy-preview.sh. Shares the
    /// swarm build's app/env contract but keeps an orchestrator-appropriate plain-compose shape —
    /// <c>restart</c>/<c>pull_policy: always</c> instead of swarm <c>deploy:</c> blocks, the destination
    /// as a managed named volume (reaped on teardown) instead of a host bind, no demo media, and the
    /// preview-only knobs (manual pipeline, no auto wishlist sync, shared-parent passkey Rp id + seed).
    /// </summary>
    private static void ConfigureForPreview(this ComposeFile file)
    {
        var api = file.Services["api"];
        var frontend = file.Services["frontend"];
        var postgres = file.Services["postgres"];

        // Shared app + env contract — same helpers the swarm build uses, so a new provider/feature env
        // lands on the preview automatically (no parallel file to forget).
        file.PersistDataProtectionKeys(api);
        file.ConfigureWishlistDownloads(api);
        ApplyProviderEnvDefaults(api);

        // Music library: read-only source bind at the deploy-provided path; destination is a managed
        // named volume so Dokploy's compose.delete(deleteVolumes:true) reaps the per-PR build on teardown.
        api.AddVolume(new Volume { Name = "music-source", Type = "bind", Source = "${SOURCE_DIRECTORY}", Target = "${SOURCE_DIRECTORY}", ReadOnly = true });
        api.AddVolume(new Volume { Name = "musichoarder-dest", Type = "volume", Source = "musichoarder-dest", Target = "${DESTINATION_DIRECTORY}" });
        file.Volumes["musichoarder-dest"] = new Volume { Name = "musichoarder-dest", Driver = "local" };

        // Plain-compose rollout: re-pull the rebuilt pr-<n> tag every redeploy, restart on crash. No
        // swarm deploy/update_config blocks (ignored by `docker compose up` anyway).
        api.PullPolicy = "always";
        api.Restart = "always";
        frontend.PullPolicy = "always";
        frontend.Restart = "always";
        postgres.Restart = "always";

        // Readiness probes for Dokploy (no swarm rolling-update gating). Same endpoints as the swarm build.
        api.WithHttpHealthcheck("${API_PORT}", "/alive", startPeriod: "40s");
        frontend.WithHttpHealthcheck("8001", "/api/health", startPeriod: "20s");

        // Preview-only behaviour: previews are resource-constrained, so discovery runs but the heavy
        // stages (fingerprint/enrich/build) are driven manually from the UI; no background wishlist sync.
        api.Environment["MusicEnricher__AutoStartPipeline"] = "false";
        api.Environment["Spotify__WishlistSyncIntervalMinutes"] = "0";
        // Pin the passkey relying-party id to the shared parent domain (not the per-PR host) so one
        // registered passkey works on every pr-<n> subdomain; the seed gives owner login on a fresh DB.
        api.Environment["WebAuthn__RpId"] = "${WEBAUTHN_RP_ID:-}";
        api.Environment["Auth__OwnerSeedCredentialJson"] = "${OWNER_SEED_CREDENTIAL_JSON:-}";

        // No swarm depends_on conditions (the api retries Postgres via Npgsql; the frontend's health
        // probe is independent). No demo media on previews (MountDemoMedia is swarm-only).
        api.DependsOn.Clear();
        frontend.DependsOn.Clear();

        // ── Single shared network (fixes Dokploy preview 504 Gateway Timeouts) ───────────────────
        // Aspire emits a dedicated `aspire` bridge network and pins every service to it. On Dokploy,
        // each *isolated* compose stack ALSO gets a per-app project network (named after the appName,
        // e.g. `mh-pr-278-ohgunr`) that Dokploy attaches every service AND dokploy-traefik to for
        // routing — so the containers end up dual-homed. Dokploy injects only the `traefik.http.*`
        // labels onto plain compose stacks, no `traefik.docker.network` disambiguator, so Traefik's
        // docker provider can resolve the frontend to its `aspire` IP — a network Traefik is NOT on —
        // and every request black-holes into a 504 even though the container serves 200 internally
        // (sveltejs unaffected; this is pure ingress routing). Services already reach each other by
        // name on the shared Dokploy project network, so drop the redundant `aspire` network: all
        // services then sit on that one network — the working pre-#262 / pr-199 topology.
        //
        // Preview-only: the swarm/prod build keeps the network (promoted to an overlay) because Dokploy
        // stamps `traefik.swarm.network=dokploy-network` on swarm services, which disambiguates; and
        // self-host is a standalone single-network `docker compose up` that never had the bug.
        foreach (var service in file.Services.Values)
        {
            service.Networks.Clear();
        }
        file.Networks.Clear();
    }

    /// <summary>
    /// Published self-host template: plain <c>docker compose up</c> with prebuilt GHCR images. Like the
    /// preview shape (bridge network, restart/pull_policy, no swarm/dashboard) but tuned for a standalone
    /// self-hoster — versioned GHCR image refs, the music library + built destination as host binds (real
    /// files on disk, not a managed volume), published host ports so the app is reachable without a
    /// reverse proxy, and a direct Spotify redirect (self-hosters register their own app, so there is no
    /// shared relay). End-user env var names (MUSIC_SOURCE_PATH / MUSIC_DESTINATION_PATH / PUBLIC_BASE_URL /
    /// MUSICHOARDER_VERSION) are preserved so existing .env files keep working.
    /// </summary>
    private static void ConfigureForSelfHost(this ComposeFile file)
    {
        var api = file.Services["api"];
        var frontend = file.Services["frontend"];
        var postgres = file.Services["postgres"];

        // Shared app + env contract — same helpers as swarm/preview, so new provider/feature env lands here too.
        file.PersistDataProtectionKeys(api);
        file.ConfigureWishlistDownloads(api);
        file.MountSyncedSource(api);
        ApplyProviderEnvDefaults(api);

        // Prebuilt images pulled from GHCR, pinned by MUSICHOARDER_VERSION (defaults to :latest). The
        // build-from-source override (docker-compose.build.yml) layers `build:` on these same services.
        api.Image = "ghcr.io/jeffreyyvdb/musichoarder/api:${MUSICHOARDER_VERSION:-latest}";
        frontend.Image = "ghcr.io/jeffreyyvdb/musichoarder/frontend:${MUSICHOARDER_VERSION:-latest}";

        // Keep the Postgres data volume name the hand-written template used. Aspire names it
        // "musichoarder-volume"; switching the published file to that would mount a fresh volume and
        // orphan an existing self-hoster's database on upgrade. Rename it back to "postgres-data".
        foreach (var v in postgres.Volumes)
        {
            if (v.Source == "musichoarder-volume") { v.Source = "postgres-data"; v.Name = "postgres-data"; }
        }
        file.Volumes.Remove("musichoarder-volume");
        file.Volumes["postgres-data"] = new Volume { Name = "postgres-data", Driver = "local" };

        // Host music library on disk: read-only source + writable destination, both host binds at the
        // app's fixed container paths. Keeps the familiar MUSIC_SOURCE_PATH / MUSIC_DESTINATION_PATH env.
        api.Environment["MusicEnricher__SourceDirectory"] = "/music/source";
        api.Environment["MusicEnricher__DestinationDirectory"] = "/music/destination";
        api.AddVolume(new Volume { Name = "music-source", Type = "bind", Source = "${MUSIC_SOURCE_PATH}", Target = "/music/source", ReadOnly = true });
        api.AddVolume(new Volume { Name = "music-destination", Type = "bind", Source = "${MUSIC_DESTINATION_PATH}", Target = "/music/destination" });

        // Soulseek (user-operated slskd): the api needs read-write access to slskd's completed-downloads
        // directory so it can move finished files into its own staging dir. Optional — the /dev/null
        // fallback (same trick as the cookies bind) keeps the compose valid when slskd isn't used;
        // the integration stays off unless SLSKD_URL + SLSKD_API_KEY are set. Read-write on purpose.
        api.Environment["Slskd__DownloadsDirectory"] = "${SLSKD_DOWNLOADS_DIR:-/data/slskd-downloads}";
        api.AddVolume(new Volume { Name = "slskd-downloads", Type = "bind", Source = "${SLSKD_DOWNLOADS_HOST_PATH:-/dev/null}", Target = "/data/slskd-downloads" });

        // Published host ports so `docker compose up` is reachable at localhost without a reverse proxy.
        // Pin the API's container port to 8080 (self-host has no API_PORT deploy var) and point the
        // frontend straight at it; drop Aspire's service-discovery vars that reference ${API_PORT}.
        api.Ports = ["5050:8080"];
        frontend.Ports = ["3000:8001"];
        api.Environment["HTTP_PORTS"] = "8080";
        frontend.Environment["MUSICHOARDER_API_URL"] = "http://api:8080";
        frontend.Environment.Remove("API_HTTP");
        frontend.Environment.Remove("API_HTTPS");
        frontend.Environment.Remove("services__api__http__0");

        // No hosted demo on a self-host install — drop the demo-media dir (the seeder falls back to its
        // synthetic seed when unset).
        api.Environment.Remove("MusicEnricher__DemoMediaDirectory");

        // Plain-compose rollout.
        api.PullPolicy = "always";
        api.Restart = "always";
        frontend.PullPolicy = "always";
        frontend.Restart = "always";
        postgres.Restart = "always";

        api.WithHttpHealthcheck("8080", "/alive", startPeriod: "40s");
        frontend.WithHttpHealthcheck("8001", "/api/health", startPeriod: "20s");

        // Spotify: self-hosters register their own app and redirect the browser straight back through
        // their public origin (PUBLIC_BASE_URL) — no shared relay, so drop the relay env and the
        // frontend's relay-only vars.
        api.Environment.Remove("Spotify__OAuthRelayUrl");
        api.Environment.Remove("Spotify__OAuthStateSigningKey"); // relay-only; direct redirect doesn't sign state
        api.Environment["Spotify__OAuthRedirectBaseUrl"] = "${PUBLIC_BASE_URL}";
        frontend.Environment.Remove("SPOTIFY_OAUTH_STATE_SIGNING_KEY");
        frontend.Environment.Remove("SPOTIFY_RETURN_ORIGIN_ALLOWLIST");

        // End-user ergonomics: a human edits .env here (unlike the deploy-env targets, which always
        // set every var), so fail fast on the required secrets and default the optional ones — the
        // guards the hand-written template carried.
        file.Services["postgres"].Environment["POSTGRES_PASSWORD"] = "${POSTGRES_PASSWORD:?POSTGRES_PASSWORD is required}";
        api.Environment["Auth__OwnerEmail"] = "${OWNER_EMAIL:?OWNER_EMAIL is required}";
        api.Environment["Frontend__PublicBaseUrl"] = "${PUBLIC_BASE_URL:?PUBLIC_BASE_URL is required}";
        api.Environment["Auth__DemoUserEmail"] = "${DEMO_USER_EMAIL:-demo@musichoarder.local}";
        api.Environment["Resend__FromAddress"] = "${RESEND_FROM_ADDRESS:-noreply@musichoarder.local}";

        // Optional streaming-FLAC acquisition sidecar (spotiflac). Profile-gated so it is inert unless
        // the operator opts in with COMPOSE_PROFILES=spotiflac — safe to ship in the shared self-host
        // compose (a git-synced instance enables it via .env alone, no compose edit). Pulls its own
        // GHCR image and shares the API's download staging volume at the same path so the FLAC it
        // writes is visible to the API. Only self-host ships the service; prod/preview carry just the
        // inert StreamingFlac__SidecarUrl env. The build-from-source override layers `build:` on it.
        file.Services["spotiflac"] = new Service
        {
            Name = "spotiflac",
            Image = "ghcr.io/jeffreyyvdb/musichoarder/spotiflac:${SPOTIFLAC_VERSION:-latest}",
            Restart = "always",
            PullPolicy = "always",
            Profiles = ["spotiflac"],
            Networks = ["aspire"],
            Environment = new()
            {
                // Optional self-hosted backends instead of the module's built-in community relay.
                ["SPOTIFLAC_TIDAL_CUSTOM_API"] = "${SPOTIFLAC_TIDAL_CUSTOM_API:-}",
                ["SPOTIFLAC_QOBUZ_LOCAL_API_URL"] = "${SPOTIFLAC_QOBUZ_LOCAL_API_URL:-}",
            },
            Volumes =
            [
                new Volume { Name = "musichoarder-downloads", Type = "volume", Source = "musichoarder-downloads", Target = "/data/downloads" },
            ],
            Healthcheck = new Healthcheck
            {
                Test = ["CMD", "python", "-c", "import urllib.request,sys; sys.exit(0 if urllib.request.urlopen('http://localhost:8000/health').status==200 else 1)"],
                Interval = "30s",
                Timeout = "10s",
                Retries = 3,
                StartPeriod = "20s",
            },
        };

        api.DependsOn.Clear();
        frontend.DependsOn.Clear();
    }

    /// <summary>
    /// Provider/feature env that both deploy shapes share: the QualityGrading + LyricsTranscription
    /// endpoints default to compose <c>${VAR:-default}</c> fallbacks (the deploy env may leave them
    /// unset). Encoding them here keeps every generated compose faithful so none drifts back to a bare
    /// <c>${...}</c> with no fallback — the failure that once shipped AI lyrics transcription invisible.
    /// A blank API key still leaves each feature off; only the model/endpoint defaults are filled.
    /// </summary>
    private static void ApplyProviderEnvDefaults(Service api)
    {
        api.Environment["QualityGrading__Model"] = "${QUALITY_GRADING_MODEL:-deepseek/deepseek-v4-flash}";
        api.Environment["QualityGrading__BaseUrl"] = "${QUALITY_GRADING_BASE_URL:-https://openrouter.ai/api/v1}";
        api.Environment["LyricsTranscription__BaseUrl"] = "${LYRICS_TRANSCRIPTION_BASE_URL:-https://api.groq.com/openai/v1}";
        api.Environment["LyricsTranscription__Model"] = "${LYRICS_TRANSCRIPTION_MODEL:-whisper-large-v3}";
        api.Environment["LyricsTranscription__LlmModel"] = "${LYRICS_TRANSCRIPTION_LLM_MODEL:-google/gemini-2.5-flash-lite}";
        // Soulseek via user-operated slskd. All blank → integration off; the "slskd" chain entry then
        // reports NotFound and every wishlist download falls through to yt-dlp, so these defaults are
        // safe on instances (e.g. the public VPS) that never configure slskd.
        api.Environment["Slskd__BaseUrl"] = "${SLSKD_URL:-}";
        api.Environment["Slskd__ApiKey"] = "${SLSKD_API_KEY:-}";
        api.Environment["Slskd__DownloadsDirectory"] = "${SLSKD_DOWNLOADS_DIR:-}";
        // Optional streaming-FLAC acquisition sidecar (spotiflac). Blank → the "spotiflac" chain entry
        // reports NotFound and downloads fall through, so this default is safe on instances that never
        // run the (separate, off-by-default) sidecar. Operators opt in by setting the URL and adding
        // "spotiflac" to the provider chain (e.g. DOWNLOAD_PROVIDER_1=spotiflac).
        api.Environment["StreamingFlac__SidecarUrl"] = "${SPOTIFLAC_SIDECAR_URL:-}";
        api.Environment["MusicEnricher__DownloadProviders__0"] = "${DOWNLOAD_PROVIDER_1:-slskd}";
        api.Environment["MusicEnricher__DownloadProviders__1"] = "${DOWNLOAD_PROVIDER_2:-yt-dlp}";
        api.Environment["MusicEnricher__DownloadProviders__2"] = "${DOWNLOAD_PROVIDER_3:-}";
        // Instance sync: role is pure config on one shared build — Push on the private instance,
        // Receive on the public one, Off (the default) everywhere else. The key gates the
        // internet-facing receive endpoints, so generate a long random one (openssl rand -base64 48).
        api.Environment["Sync__Mode"] = "${SYNC_MODE:-Off}";
        api.Environment["Sync__ApiKey"] = "${SYNC_API_KEY:-}";
        api.Environment["Sync__RemoteBaseUrl"] = "${SYNC_REMOTE_URL:-}";
        api.Environment["Sync__SyncedSourceDirectory"] = "${SYNC_SOURCE_DIR:-/data/synced-source}";
        // Two-way like/favorite sync with Navidrome (Subsonic). Inert unless all three creds are set;
        // point BaseUrl at the Navidrome origin reachable from the API container.
        api.Environment["Navidrome__BaseUrl"] = "${NAVIDROME_URL:-}";
        api.Environment["Navidrome__Username"] = "${NAVIDROME_USERNAME:-}";
        api.Environment["Navidrome__Password"] = "${NAVIDROME_PASSWORD:-}";
    }

    /// <summary>
    /// Named volume backing the sync-receive managed directory. Harmless (empty) when the instance
    /// isn't a receiver; required so received files survive redeploys when it is.
    /// </summary>
    private static void MountSyncedSource(this ComposeFile file, Service api)
    {
        api.AddVolume(new Volume { Name = "musichoarder-synced-source", Type = "volume", Source = "musichoarder-synced-source", Target = "/data/synced-source" });
        file.Volumes["musichoarder-synced-source"] = new Volume { Name = "musichoarder-synced-source", Driver = "local" };
    }

    /// <summary>
    /// Swarm rolling update for a stateless request-path service: <c>start-first</c> keeps the old
    /// task serving until the new one passes its healthcheck (zero-downtime), rolling back if it
    /// never goes healthy.
    /// </summary>
    public static Service WithRollingUpdate(this Service service)
    {
        service.Deploy = SwarmUpdate("start-first");
        return service;
    }

    /// <summary>
    /// Swarm update for a single-instance service — one that must never run two copies at once
    /// (Postgres' single data volume, or the API with its in-process singleton pipeline).
    /// <c>stop-first</c> tears down the old task before the new one starts, so they never overlap, at
    /// the cost of a brief gap on redeploy.
    /// </summary>
    public static Service WithStopFirstUpdate(this Service service)
    {
        service.Deploy = SwarmUpdate("stop-first");
        return service;
    }

    private static Deploy SwarmUpdate(string order) => new()
    {
        Mode = "replicated",
        Replicas = 1,
        UpdateConfig = new()
        {
            Order = order,
            Parallelism = 1,
            Delay = "5s",
            FailureAction = "rollback",
            Monitor = "60s",
        },
        RestartPolicy = new() { Condition = "any", Delay = "5s" },
    };

    /// <summary>
    /// Adds a <c>curl</c>-based HTTP liveness healthcheck. <paramref name="port"/> may be a literal
    /// or a compose variable reference (e.g. <c>${API_PORT}</c>); <c>curl</c> is present in both
    /// runtime images.
    /// </summary>
    public static Service WithHttpHealthcheck(this Service service, string port, string path, string startPeriod)
    {
        service.Healthcheck = new Healthcheck
        {
            Test = new() { "CMD-SHELL", $"curl -fsS http://localhost:{port}{path} || exit 1" },
            Interval = "10s",
            Timeout = "5s",
            Retries = 6,
            StartPeriod = startPeriod,
        };
        return service;
    }

    /// <summary>
    /// Persists ASP.NET DataProtection keys in a named volume so auth sessions survive a redeploy
    /// (otherwise every deploy invalidates existing session cookies).
    /// </summary>
    private static void PersistDataProtectionKeys(this ComposeFile file, Service api)
    {
        api.AddVolume(new Volume { Name = "musichoarder-dpkeys", Type = "volume", Source = "musichoarder-dpkeys", Target = "/data/dpkeys" });
        file.Volumes["musichoarder-dpkeys"] = new Volume { Name = "musichoarder-dpkeys", Driver = "local" };
    }

    /// <summary>
    /// Bind-mounts the host music library into the API container at the same paths the app reads
    /// (compose interpolates <c>${SOURCE_DIRECTORY}</c>/<c>${DESTINATION_DIRECTORY}</c> from the
    /// deploy env; Docker creates the host dirs if missing).
    /// </summary>
    private static void MountMusicLibrary(Service api)
    {
        api.AddVolume(new Volume { Name = "music-source", Type = "bind", Source = "${SOURCE_DIRECTORY}", Target = "${SOURCE_DIRECTORY}", ReadOnly = true });
        api.AddVolume(new Volume { Name = "music-destination", Type = "bind", Source = "${DESTINATION_DIRECTORY}", Target = "${DESTINATION_DIRECTORY}" });
    }

    /// <summary>
    /// Read-only bind mount that supplies the <em>hosted demo</em> with real, playable songs. Only the
    /// hosted deploy populates <c>${DEMO_MEDIA_DIRECTORY}</c> (the owner rsyncs a few curated albums
    /// there); the env var and mount default to <c>/srv/demo-media</c>, so an empty/absent directory
    /// makes the seeder fall back to the synthetic demo seed (see <c>DemoSeederHostedService</c>). No
    /// copyrighted audio enters the repo — the files live only on the demo host. Self-hosters use the
    /// separate committed <c>docker-compose.yml</c>, which has no demo mount at all.
    /// </summary>
    private static void MountDemoMedia(Service api)
    {
        api.AddVolume(new Volume { Name = "demo-media", Type = "bind", Source = "${DEMO_MEDIA_DIRECTORY:-/srv/demo-media}", Target = "${DEMO_MEDIA_DIRECTORY:-/srv/demo-media}", ReadOnly = true });
        api.Environment["MusicEnricher__DemoMediaDirectory"] = "${DEMO_MEDIA_DIRECTORY:-/srv/demo-media}";
    }

    /// <summary>
    /// Wires the owner-only wishlist downloader for prod. Everything defaults OFF/empty so adding this
    /// is inert until the deploy env opts in (set <c>ENABLE_WISHLIST_DOWNLOADS=true</c> in Dokploy):
    /// <list type="bullet">
    /// <item><c>DownloadDirectory</c> is a managed named volume (the source mount is read-only), indexed
    /// by the scanner as an extra source root so downloads flow through the normal pipeline.</item>
    /// <item><c>EnableWishlistDownloads</c> gates the feature + manual trigger; <c>AutoDownloadWishlist</c>
    /// additionally lets the worker auto-fetch the owner's Pending items in the background.</item>
    /// <item>An optional YouTube cookies file (Netscape format) on the host clears YouTube's
    /// datacenter-IP bot check — point <c>YTDLP_COOKIES_HOST_PATH</c> at it and set
    /// <c>YTDLP_COOKIES_PATH=/data/cookies/youtube.txt</c>. Unset → binds <c>/dev/null</c> (the API
    /// only passes <c>--cookies</c> when the file exists), so it's harmless.</item>
    /// </list>
    /// yt-dlp + ffmpeg + deno are baked into the API image (Dockerfile.api-base).
    /// </summary>
    private static void ConfigureWishlistDownloads(this ComposeFile file, Service api)
    {
        api.Environment["MusicEnricher__EnableWishlistDownloads"] = "${ENABLE_WISHLIST_DOWNLOADS:-false}";
        api.Environment["MusicEnricher__AutoDownloadWishlist"] = "${AUTO_DOWNLOAD_WISHLIST:-false}";
        api.Environment["MusicEnricher__DownloadDirectory"] = "/data/downloads";
        api.Environment["MusicEnricher__YtDlpCookiesPath"] = "${YTDLP_COOKIES_PATH:-}";
        api.Environment["MusicEnricher__YtDlpExtraArgs"] = "${YTDLP_EXTRA_ARGS:-}";

        api.AddVolume(new Volume { Name = "musichoarder-downloads", Type = "volume", Source = "musichoarder-downloads", Target = "/data/downloads" });
        file.Volumes["musichoarder-downloads"] = new Volume { Name = "musichoarder-downloads", Driver = "local" };

        // Optional cookies bind: harmless /dev/null when YTDLP_COOKIES_HOST_PATH is unset.
        api.AddVolume(new Volume { Name = "youtube-cookies", Type = "bind", Source = "${YTDLP_COOKIES_HOST_PATH:-/dev/null}", Target = "/data/cookies/youtube.txt", ReadOnly = true });
    }
}
