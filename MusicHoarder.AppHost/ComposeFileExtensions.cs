using Aspire.Hosting.Docker.Resources;
using Aspire.Hosting.Docker.Resources.ComposeNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes;
using Aspire.Hosting.Docker.Resources.ServiceNodes.Swarm;

/// <summary>
/// Post-processing applied to the Aspire-generated Docker Compose file
/// (<c>aspire publish</c> → <c>aspire-output/docker-compose.yaml</c>) that Dokploy deploys.
/// Kept out of <c>AppHost.cs</c> so the composition root stays focused on resource wiring.
/// </summary>
internal static class ComposeFileExtensions
{
    /// <summary>
    /// Applies every MusicHoarder-specific tweak the published compose needs: persistent storage,
    /// the host music-library bind mounts, zero-downtime rollout config, and the normalizations that
    /// make the file deployable as a Docker Stack (Swarm) under Dokploy.
    /// </summary>
    public static void ConfigureMusicHoarderDeployment(this ComposeFile file)
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

        // ── Zero-downtime deploys ──────────────────────────────────────────────────────────────
        // Dokploy "Compose" deploys (`docker compose up`) stop the old container before the new one
        // is ready → a 502 window every release. Running the stack as a Docker Stack (swarm) instead
        // lets `update_config: { order: start-first }` keep the old task serving until the new one
        // passes its healthcheck, then swap. The Aspire-published images carry no Docker HEALTHCHECK
        // (the ones in the root Dockerfile / frontend/Dockerfile are only used by the
        // build-from-source compose), so the probes are defined here — without one swarm treats
        // "process started" as "healthy" and tears down the old task before EF migrations finish /
        // Kestrel is listening. These blocks are inert under plain `docker compose up`.
        //
        // The api listens on ${API_PORT} (HTTP_PORTS), so probe that rather than a hardcoded port.
        // replicas:1 on the api (inside WithRollingUpdate) is mandatory so two cold starts never race
        // EF migrations: only the single new, post-migration task starts while the old already-
        // migrated task serves, and EF's __EFMigrationsHistory makes the run idempotent.
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
    /// Swarm rolling update for a request-path service: <c>start-first</c> keeps the old task serving
    /// until the new one passes its healthcheck, with rollback if it never goes healthy.
    /// </summary>
    public static Service WithRollingUpdate(this Service service)
    {
        service.Deploy = new Deploy
        {
            Mode = "replicated",
            Replicas = 1,
            UpdateConfig = new()
            {
                Order = "start-first",
                Parallelism = 1,
                Delay = "5s",
                FailureAction = "rollback",
                Monitor = "60s",
            },
            RestartPolicy = new() { Condition = "any", Delay = "5s" },
        };
        return service;
    }

    /// <summary>
    /// Swarm update policy for a single-writer stateful service (e.g. Postgres): <c>stop-first</c> so
    /// the old container releases its volume before the replacement claims it.
    /// </summary>
    public static Service WithStopFirstUpdate(this Service service)
    {
        service.Deploy = new Deploy
        {
            Mode = "replicated",
            Replicas = 1,
            UpdateConfig = new() { Order = "stop-first", Parallelism = 1 },
            RestartPolicy = new() { Condition = "any" },
        };
        return service;
    }

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
}
