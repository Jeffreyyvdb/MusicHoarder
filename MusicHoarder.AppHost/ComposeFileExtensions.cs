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
            default:
                throw new NotSupportedException(
                    $"Deploy target '{target}' is not wired yet. Only Swarm (musichoarder.app) is " +
                    "implemented; Compose (preview) and SelfHost are added in follow-up changes.");
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

        // QualityGrading model/endpoint default to compose interpolation fallbacks (the deploy env
        // may leave QUALITY_GRADING_* unset). These mirror the documented defaults in the
        // build-from-source docker-compose.yml; encoding them here keeps `aspire publish` faithful so
        // the generated compose never drifts back to a bare ${...} with no fallback.
        api.Environment["QualityGrading__Model"] = "${QUALITY_GRADING_MODEL:-deepseek/deepseek-v4-flash}";
        api.Environment["QualityGrading__BaseUrl"] = "${QUALITY_GRADING_BASE_URL:-https://openrouter.ai/api/v1}";

        // Experimental AI lyrics transcription. Same pattern as QualityGrading: encode the Groq Whisper
        // defaults as compose `:-` fallbacks here so a clean `aspire publish` reproduces them and the
        // generated prod compose never drifts back to a bare ${...} (which is what hid the feature on
        // prod once before). Blank LYRICS_TRANSCRIPTION_API_KEY → the feature stays hidden in the UI.
        api.Environment["LyricsTranscription__BaseUrl"] = "${LYRICS_TRANSCRIPTION_BASE_URL:-https://api.groq.com/openai/v1}";
        api.Environment["LyricsTranscription__Model"] = "${LYRICS_TRANSCRIPTION_MODEL:-whisper-large-v3}";
        api.Environment["LyricsTranscription__LlmModel"] = "${LYRICS_TRANSCRIPTION_LLM_MODEL:-google/gemini-2.5-flash-lite}";

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
