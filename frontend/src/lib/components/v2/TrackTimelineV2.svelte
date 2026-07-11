<script lang="ts">
  import {
    ArrowLeft,
    ChevronRight,
    ExternalLink,
    RefreshCw,
    Loader2,
    AlertTriangle,
    Sparkles,
    FolderOpen,
    Search
  } from '@lucide/svelte';
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import TimelineList from '$lib/components/v2/TimelineList.svelte';
  import {
    albumKeyForSong,
    enrichSong,
    coverUrlForSong,
    fetchEnrichmentDetail,
    fetchSongQualityGrade,
    fetchSongs,
    mapEnrichmentStatus,
    soulseek,
    type ApiSong,
    type EnrichmentDetail,
    type SongQualityGradeView
  } from '$lib/api-client';
  import {
    buildTimeline,
    contributedProviders,
    elapsedMs,
    formatElapsed,
    providerColor,
    decisionLabel,
    type TimelineEvent,
    type TimelineTint
  } from '$lib/review-helpers';
  import { formatDuration, formatFileSize, formatBitrate } from '$lib/formatters';

  type Props = { songId: number };
  const { songId }: Props = $props();

  // ── data layer (reuses the existing api-client) ────────────────────────────
  let song = $state<ApiSong | null>(null);
  let detail = $state<EnrichmentDetail | null>(null);
  let grade = $state<SongQualityGradeView | null>(null);
  let loaded = $state(false);
  let loadError = $state<string | null>(null);
  let reenriching = $state(false);

  async function loadAll(id: number) {
    loaded = false;
    loadError = null;
    // The API has no single-song GET; resolve from the full list (the same source
    // every other v2 surface reads), then layer the enrichment + quality detail.
    const [songsRes, detailRes, gradeRes] = await Promise.allSettled([
      fetchSongs(),
      fetchEnrichmentDetail(id),
      fetchSongQualityGrade(id)
    ]);

    if (songsRes.status === 'fulfilled') {
      song = songsRes.value.find((s) => s.id === id) ?? null;
      if (!song) loadError = 'This track is not in the library index.';
    } else {
      loadError = 'Could not load the track.';
    }
    // Detail / grade are optional — a song may not be enriched or graded yet.
    detail = detailRes.status === 'fulfilled' ? detailRes.value : null;
    grade = gradeRes.status === 'fulfilled' ? gradeRes.value : null;
    loaded = true;
  }

  $effect(() => {
    void loadAll(songId);
  });

  // ── derived display values (all from REAL data) ─────────────────────────────
  const heroTitle = $derived.by(() => {
    if (!song) return '';
    return (song.title ?? detail?.current?.title ?? song.fileName).trim() || song.fileName;
  });
  const heroArtist = $derived.by(() => {
    if (!song) return '';
    return (song.albumArtist ?? song.artist ?? detail?.current?.artist ?? '').trim();
  });
  const heroAlbum = $derived.by(() => (song?.album ?? detail?.current?.album ?? '').trim());
  const heroYear = $derived(song?.year ?? detail?.current?.year ?? null);

  const statusLabel = $derived(song ? mapEnrichmentStatus(song.enrichmentStatus) : 'pending');
  const decision = $derived(decisionLabel(detail));

  const contributed = $derived(contributedProviders(detail));
  const providerAttemptCount = $derived(detail?.providerAttempts.length ?? 0);
  // How many attempts actually produced a candidate the track could use.
  const contributingCount = $derived(
    detail?.providerAttempts.filter((a) => a.candidate != null).length ?? 0
  );

  const wallClock = $derived(song ? formatElapsed(elapsedMs(song, detail)) : '—');

  const sourcePath = $derived(song?.sourcePath ?? '');
  const destinationPath = $derived(song?.destinationPath ?? null);

  function bitrateChip(): string {
    if (!song) return '—';
    return formatBitrate(song.bitRate, song.extension);
  }

  // ── timeline (reuses buildTimeline; appends the AI grade as a real event) ────
  const baseTimeline = $derived<TimelineEvent[]>(
    song ? buildTimeline(song, detail) : []
  );

  // The quality grade is recorded with a real timestamp, so we can slot it into
  // the chronology. Per-event latency is NOT captured by the backend, so we never
  // synthesize a ms duration for grading — only its real `gradedAtUtc`.
  const timeline = $derived.by<TimelineEvent[]>(() => {
    const events = [...baseTimeline];
    if (grade?.graded && grade.gradedAtUtc) {
      const tint: TimelineTint =
        grade.verdict === 'Wrong'
          ? 'err'
          : grade.verdict === 'Questionable'
            ? 'warn'
            : 'ok';
      events.push({
        key: 'ai-grade',
        time: grade.gradedAtUtc,
        stage: 'AI GRADE',
        tint,
        provider: grade.model
          ? { label: grade.model, color: providerColor('Spotify'), pct: grade.score ?? null }
          : null,
        description: `Quality grade · ${grade.verdict ?? 'graded'}${
          grade.score != null ? ` (${grade.score}/100)` : ''
        }${grade.summary ? ` — ${grade.summary}` : ''}`,
        deltaMs: null
      });
    }
    // Re-sort so the grade lands in chronological order with everything else.
    return [...events].sort(
      (a, b) => new Date(a.time).getTime() - new Date(b.time).getTime()
    );
  });

  // ── navigation ───────────────────────────────────────────────────────────────
  const libraryHref = $derived.by(() => {
    if (!song) return '/library';
    if (song.destinationPath) {
      return `/library?album=${encodeURIComponent(albumKeyForSong(song))}&track=${song.id}`;
    }
    // Not yet built — the review surface is where its provenance lives.
    return `/inbox?song=${song.id}`;
  });

  function goBack() {
    if (typeof history !== 'undefined' && history.length > 1) history.back();
    else void goto('/library');
  }

  async function handleReenrich() {
    if (!song || reenriching) return;
    reenriching = true;
    try {
      await enrichSong(song.id, true);
      await loadAll(song.id);
    } finally {
      reenriching = false;
    }
  }

  // ── soulseek quality upgrade (owner-only) ────────────────────────────────────
  const isOwner = $derived(
    (page.data.user as { role?: 'Owner' | 'Demo' } | undefined)?.role === 'Owner'
  );
  let soulseekConfigured = $state(false);
  let requestingUpgrade = $state(false);
  let upgradeRequestError = $state<string | null>(null);

  $effect(() => {
    if (!isOwner) return;
    let cancelled = false;
    void soulseek
      .getStatus()
      .then((s) => {
        if (!cancelled) soulseekConfigured = s.configured;
      })
      .catch(() => {
        // Endpoint unavailable — keep the action hidden.
      });
    return () => {
      cancelled = true;
    };
  });

  // Label for the disabled button while an upgrade is in flight.
  const upgradeActiveLabel = $derived.by(() => {
    const u = detail?.upgrade;
    if (!u?.active) return null;
    switch (u.status) {
      case 'Searching':
        return 'Searching…';
      case 'Downloading':
        return 'Downloading…';
      case 'AwaitingIngest':
        return 'Awaiting ingest…';
      default:
        return 'Queued…';
    }
  });

  // Terminal failure note — shown muted, the button stays enabled for a retry.
  const upgradeTerminalNote = $derived.by(() => {
    const u = detail?.upgrade;
    if (!u || u.active) return null;
    if (u.status === 'NotFound')
      return u.error ? `No better copy found — ${u.error}` : 'No better copy found on Soulseek.';
    if (u.status === 'Failed') return u.error ? `Upgrade failed — ${u.error}` : 'Upgrade failed.';
    return null;
  });

  async function handleFindBetterQuality() {
    if (!song || requestingUpgrade || detail?.upgrade?.active) return;
    requestingUpgrade = true;
    upgradeRequestError = null;
    try {
      await soulseek.requestUpgrade({ songId: song.id });
      await loadAll(song.id);
    } catch (err) {
      upgradeRequestError = err instanceof Error ? err.message : 'Could not queue the upgrade.';
    } finally {
      requestingUpgrade = false;
    }
  }

  // Per-track sync state badge (Push deployments only; null otherwise).
  const syncBadge = $derived.by(() => {
    const ts = detail?.trackSync;
    if (!ts) return null;
    switch (ts.status) {
      case 'Synced':
        return { label: 'Synced', cls: 'border-[#1DB954]/40 bg-[#1DB954]/10 text-[#1DB954]' };
      case 'Uploading':
        return { label: 'Uploading', cls: 'border-border bg-card text-foreground' };
      case 'SkippedRemoteBetter':
        return { label: 'Remote has better', cls: 'border-border bg-muted text-muted-foreground' };
      case 'Failed':
        return {
          label: 'Sync failed',
          cls: 'border-destructive/40 bg-destructive/10 text-destructive'
        };
      default:
        return { label: 'Sync pending', cls: 'border-border bg-card text-foreground' };
    }
  });
</script>

<!-- Header -->
<header
  class="border-border flex shrink-0 flex-col gap-3 border-b px-4 py-4 sm:flex-row sm:items-end sm:justify-between sm:gap-4 sm:px-7 sm:py-5"
>
  <div class="min-w-0">
    <div class="text-muted-foreground font-mono text-[10px] tracking-[0.12em] uppercase">
      Track · enrichment timeline
    </div>
    <h1 class="mt-1 text-2xl font-semibold tracking-tight">Provenance</h1>
    <p class="text-muted-foreground mt-1 max-w-2xl text-xs">
      Everything we know about this track — where the original came from, every provider that
      touched it, and where it lives now.
    </p>
  </div>
  <div class="flex flex-wrap items-center gap-2 sm:shrink-0">
    <button
      type="button"
      onclick={goBack}
      class="border-border bg-card hover:bg-muted text-foreground inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-[12.5px] font-medium transition-colors"
    >
      <ArrowLeft class="size-3.5" /> Back
    </button>
    <button
      type="button"
      onclick={handleReenrich}
      disabled={reenriching || !song}
      class="border-border bg-card hover:bg-muted text-foreground inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-[12.5px] font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50"
    >
      {#if reenriching}
        <Loader2 class="size-3.5 animate-spin" />
      {:else}
        <RefreshCw class="size-3.5" />
      {/if}
      Re-enrich
    </button>
    {#if isOwner && soulseekConfigured}
      <button
        type="button"
        onclick={handleFindBetterQuality}
        disabled={requestingUpgrade || !song || detail?.upgrade?.active === true}
        class="border-border bg-card hover:bg-muted text-foreground inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-[12.5px] font-medium transition-colors disabled:cursor-not-allowed disabled:opacity-50"
      >
        {#if requestingUpgrade || detail?.upgrade?.active}
          <Loader2 class="size-3.5 animate-spin" />
        {:else}
          <Search class="size-3.5" />
        {/if}
        {upgradeActiveLabel ?? 'Find better quality'}
      </button>
    {/if}
    <a
      href={libraryHref}
      class="border-border bg-card hover:bg-muted text-foreground inline-flex items-center gap-1.5 rounded-md border px-2.5 py-1.5 text-[12.5px] font-medium transition-colors"
    >
      <ExternalLink class="size-3.5" /> Open in library
    </a>
    {#if isOwner && soulseekConfigured && (upgradeRequestError || upgradeTerminalNote)}
      <span class="text-muted-foreground w-full text-[11px] sm:text-right">
        {upgradeRequestError ?? upgradeTerminalNote}
      </span>
    {/if}
  </div>
</header>

<ScrollArea class="min-h-0 flex-1">
  <div class="mx-auto flex max-w-4xl flex-col gap-5 px-4 py-5 sm:gap-6 sm:px-7 sm:py-6">
    {#if !loaded}
      <!-- Hero skeleton -->
      <div class="border-border bg-card flex items-center gap-5 rounded-lg border p-5">
        <Skeleton class="size-24 rounded-lg" />
        <div class="flex-1 space-y-2">
          <Skeleton class="h-3 w-16" />
          <Skeleton class="h-7 w-64" />
          <Skeleton class="h-4 w-80" />
        </div>
      </div>
      <Skeleton class="h-24 w-full" />
      <Skeleton class="h-40 w-full" />
    {:else if loadError || !song}
      <div class="border-border bg-card flex flex-col items-center gap-3 rounded-lg border p-10 text-center">
        <span class="bg-muted text-muted-foreground grid size-12 place-items-center rounded-full">
          <AlertTriangle class="size-6" />
        </span>
        <div class="text-[15px] font-semibold">Track not found</div>
        <p class="text-muted-foreground max-w-sm text-[12.5px]">
          {loadError ?? 'We could not load this track.'}
        </p>
        <a
          href="/library"
          class="text-primary mt-1 inline-flex items-center gap-0.5 text-[12.5px] font-medium hover:underline"
        >
          Back to library <ChevronRight class="size-3.5" />
        </a>
      </div>
    {:else}
      <!-- Hero -->
      <section
        class="border-border bg-card flex flex-col items-start gap-4 rounded-lg border p-4 sm:flex-row sm:items-center sm:gap-5 sm:p-5"
      >
        <Cover
          artist={heroArtist || 'Unknown'}
          title={heroAlbum || heroTitle}
          coverUrl={coverUrlForSong(song)}
          size={96}
          corner={10}
          caption={false}
        />
        <div class="min-w-0 flex-1">
          <div class="text-muted-foreground font-mono text-[10px] tracking-[0.1em] uppercase">Track</div>
          <h2 class="mt-0.5 truncate text-2xl font-semibold tracking-tight">{heroTitle}</h2>
          <div class="text-muted-foreground mt-0.5 truncate text-[13px]">
            {[heroArtist, heroAlbum, heroYear != null ? String(heroYear) : null]
              .filter(Boolean)
              .join(' · ') || '—'}
          </div>
          <div class="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1.5 text-[11.5px] sm:gap-x-5">
            <span class="text-muted-foreground">
              Duration <b class="text-foreground font-mono">{formatDuration(song.durationSeconds)}</b>
            </span>
            <span class="text-muted-foreground">
              Format <b class="text-foreground font-mono">{bitrateChip()}</b>
            </span>
            <span class="text-muted-foreground">
              Size <b class="text-foreground font-mono">{formatFileSize(song.fileSizeBytes)}</b>
            </span>
            {#if song.fingerprint}
              <span class="text-muted-foreground">
                Fingerprint <b class="text-foreground font-mono">{song.fingerprint.slice(0, 16)}…</b>
              </span>
            {/if}
            {#if song.musicBrainzId}
              <span class="text-muted-foreground">
                MBID <b class="text-foreground font-mono">{song.musicBrainzId.slice(0, 8)}…</b>
              </span>
            {/if}
            {#if grade?.graded && grade.score != null}
              <span class="text-muted-foreground inline-flex items-center gap-1">
                <Sparkles class="size-3" />
                AI grade <b class="font-mono" style="color: oklch(0.74 0.15 150)">{grade.score}/100</b>
              </span>
            {/if}
            {#if syncBadge}
              <span
                class="inline-flex items-center rounded-full border px-2.5 py-0.5 text-[11px] font-medium {syncBadge.cls}"
                title={detail?.trackSync?.status === 'Failed'
                  ? (detail?.trackSync?.lastError ?? undefined)
                  : undefined}
              >
                {syncBadge.label}
              </span>
            {/if}
          </div>
        </div>
      </section>

      <!-- KPI strip -->
      <div class="grid grid-cols-2 gap-3 sm:grid-cols-4">
        <div class="border-border bg-card rounded-lg border p-3.5">
          <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">Decision</div>
          <div class="text-primary mt-0.5 font-mono text-base font-semibold">{decision}</div>
          <div class="text-muted-foreground mt-0.5 text-[11px]">{statusLabel}</div>
        </div>
        <div class="border-border bg-card rounded-lg border p-3.5">
          <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">Providers</div>
          <div class="mt-0.5 font-mono text-base font-semibold tabular-nums">{providerAttemptCount}</div>
          <div class="text-muted-foreground mt-0.5 text-[11px]">{contributingCount} contributed data</div>
        </div>
        <div class="border-border bg-card rounded-lg border p-3.5">
          <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">Match conf.</div>
          <div class="mt-0.5 font-mono text-base font-semibold tabular-nums">
            {song.matchConfidence != null
              ? song.matchConfidence.toFixed(2)
              : detail?.matchConfidence != null
                ? detail.matchConfidence.toFixed(2)
                : '—'}
          </div>
          <div class="text-muted-foreground mt-0.5 truncate text-[11px]">
            {song.matchedBy ?? detail?.matchedBy ?? 'no winner'}
          </div>
        </div>
        <div class="border-border bg-card rounded-lg border p-3.5">
          <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">Wall clock</div>
          <div class="mt-0.5 font-mono text-base font-semibold tabular-nums">{wallClock}</div>
          <div class="text-muted-foreground mt-0.5 text-[11px]">scan → now</div>
        </div>
      </div>

      <!-- Source → Destination paths -->
      <section>
        <div class="mb-2.5 flex items-baseline gap-2">
          <span class="text-[13px] font-semibold">Where it came from, where it lives now</span>
        </div>
        <div class="grid grid-cols-1 items-stretch gap-3 sm:grid-cols-[1fr_auto_1fr]">
          <div class="border-border bg-muted/30 rounded-lg border p-3.5">
            <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">
              Source · raw
            </div>
            <div class="mt-1.5 font-mono text-[11.5px] break-all">{sourcePath || '—'}</div>
          </div>
          <div class="text-muted-foreground hidden items-center justify-center sm:flex">
            <ChevronRight class="size-5" />
          </div>
          {#if destinationPath}
            <div
              class="rounded-lg border p-3.5"
              style="background: oklch(0.62 0.13 145 / 0.08); border-color: oklch(0.62 0.13 145 / 0.3)"
            >
              <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">
                Destination · clean
              </div>
              <div class="text-primary mt-1.5 font-mono text-[11.5px] break-all">{destinationPath}</div>
            </div>
          {:else}
            <div class="border-border bg-card rounded-lg border border-dashed p-3.5">
              <div class="text-muted-foreground flex items-center gap-1.5 text-[10px] font-semibold tracking-wide uppercase">
                <FolderOpen class="size-3.5" /> Destination
              </div>
              <div class="text-muted-foreground mt-1.5 text-[11.5px]">
                Not written to the library yet — it lands here once it clears review and the build runs.
              </div>
            </div>
          {/if}
        </div>
      </section>

      <!-- Contributing providers -->
      <section>
        <div class="mb-2.5 flex items-baseline gap-2">
          <span class="text-[13px] font-semibold">Providers that contributed</span>
          <span class="text-muted-foreground text-[11.5px]">
            {#if contributed.length === 0}
              No provider returned usable data for this track.
            {:else}
              {contributed.length} of {providerAttemptCount} provider{providerAttemptCount === 1 ? '' : 's'} returned data this track used.
            {/if}
          </span>
        </div>
        {#if contributed.length > 0}
          <div class="flex flex-wrap gap-2">
            {#each contributed as c (c.label)}
              <span
                class="border-border bg-card inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[12px]"
              >
                <span class="size-2 rounded-full" style="background: {c.color}"></span>
                {c.label}
              </span>
            {/each}
          </div>
        {:else}
          <div class="border-border bg-card text-muted-foreground rounded-lg border border-dashed px-3.5 py-3 text-[12px]">
            Nothing matched. Re-enrich, or open the track in review to override the fields manually.
          </div>
        {/if}
      </section>

      <!-- Full timeline -->
      <section>
        <div class="mb-3 flex items-baseline gap-2">
          <span class="text-[13px] font-semibold">Full timeline</span>
          <span class="text-muted-foreground text-[11.5px]">
            {timeline.length} event{timeline.length === 1 ? '' : 's'} · {wallClock} end-to-end
          </span>
        </div>

        {#if timeline.length === 0}
          <div class="border-border bg-card text-muted-foreground rounded-lg border border-dashed px-3.5 py-6 text-center text-[12px]">
            No pipeline events recorded for this track yet.
          </div>
        {:else}
          <TimelineList events={timeline} />
          <p class="text-muted-foreground/70 mt-2 text-[11px]">
            Timestamps are real. Per-event processing latency isn't captured by the pipeline yet —
            <span class="bg-muted text-muted-foreground rounded px-1 py-px font-mono text-[9px] tracking-wide uppercase">soon</span>.
          </p>
        {/if}
      </section>
    {/if}
  </div>
</ScrollArea>
