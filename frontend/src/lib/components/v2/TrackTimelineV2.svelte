<script lang="ts">
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import {
    ChevronRight,
    CircleCheck,
    Copy,
    Disc3,
    Ellipsis,
    Inbox,
    RefreshCw,
    Loader2,
    AlertTriangle,
    Search
  } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { tabMemory } from '$lib/stores/tab-memory.svelte';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import * as GroupedList from '$lib/components/ui/grouped-list';
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
    providerColor,
    decisionLabel,
    type TimelineEvent,
    type TimelineTint
  } from '$lib/review-helpers';
  import {
    formatDuration,
    formatFileSize,
    formatBitrate,
    formatTotalDuration
  } from '$lib/formatters';
  import { isAdmin } from '$lib/auth/capabilities';
  import { isBuiltSong } from '$lib/album-sections';
  import { cn } from '$lib/utils';

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

  // Sentence case, in words rather than the pipeline's status codes.
  const DECISION_WORDS: Record<string, string> = {
    PENDING: 'Awaiting review',
    ACCEPTED: 'Accepted by you',
    MATCHED: 'Matched',
    FAILED: 'No match',
    QUEUED: 'Queued'
  };
  const decision = $derived.by(() => {
    const code = decisionLabel(detail);
    if (DECISION_WORDS[code]) return DECISION_WORDS[code];
    // No enrichment detail: fall back to the song's own status.
    if (code === '—' && song) {
      const status = mapEnrichmentStatus(song.enrichmentStatus);
      return status === 'needsreview'
        ? DECISION_WORDS.PENDING
        : status === 'failed'
          ? DECISION_WORDS.FAILED
          : status === 'complete'
            ? DECISION_WORDS.MATCHED
            : DECISION_WORDS.QUEUED;
    }
    return code.charAt(0) + code.slice(1).toLowerCase();
  });
  const confidence = $derived.by(() => {
    const value = song?.matchConfidence ?? detail?.matchConfidence ?? null;
    const by = song?.matchedBy ?? detail?.matchedBy ?? null;
    if (value == null) return by ?? 'No winning match';
    return by ? `${value.toFixed(2)} · ${by}` : value.toFixed(2);
  });

  const contributed = $derived(contributedProviders(detail));
  const providerAttemptCount = $derived(detail?.providerAttempts.length ?? 0);
  // How many attempts actually produced a candidate the track could use.
  const contributingCount = $derived(
    detail?.providerAttempts.filter((a) => a.candidate != null).length ?? 0
  );

  // First stored timestamp to the last: how long the pipeline has spent on this song.
  function readableElapsed(ms: number | null): string {
    if (ms == null) return '—';
    if (ms < 1000) return `${ms} ms`;
    if (ms < 60_000) return `${(ms / 1000).toFixed(1)} sec`;
    return formatTotalDuration(ms / 1000);
  }
  const wallClock = $derived(song ? readableElapsed(elapsedMs(song, detail)) : '—');

  const sourcePath = $derived(song?.sourcePath ?? '');
  const destinationPath = $derived(song?.destinationPath ?? null);

  function bitrateChip(): string {
    if (!song) return '—';
    return formatBitrate(song.bitRate, song.extension);
  }

  // ── timeline (reuses buildTimeline; appends the AI grade as a real event) ────
  const baseTimeline = $derived<TimelineEvent[]>(song ? buildTimeline(song, detail) : []);

  // The quality grade is recorded with a real timestamp, so we can slot it into
  // the chronology. Per-event latency is NOT captured by the backend, so we never
  // synthesize a ms duration for grading — only its real `gradedAtUtc`.
  const timeline = $derived.by<TimelineEvent[]>(() => {
    const events = [...baseTimeline];
    if (grade?.graded && grade.gradedAtUtc) {
      const tint: TimelineTint =
        grade.verdict === 'Wrong' ? 'err' : grade.verdict === 'Questionable' ? 'warn' : 'ok';
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
    return [...events].sort((a, b) => new Date(a.time).getTime() - new Date(b.time).getTime());
  });

  // ── navigation ───────────────────────────────────────────────────────────────
  const isOwner = $derived(isAdmin(page.data.user));

  // Where "Open in library" goes. A built track opens on its album page. Only an admin's unbuilt
  // track goes to the Inbox, where its review lives — a member cannot open the Inbox (the route
  // guard bounces it to Overview), and a shared row carries no destination path at all: it is
  // built, just not showing where.
  const opensInbox = $derived(
    song !== null && isOwner && !isBuiltSong(song) && !song.destinationPath
  );
  const libraryHref = $derived.by(() => {
    if (!song) return '/library';
    // The queue named, as every link into the Inbox is (a tab-less ?song= means Tag review too,
    // but tab-memory would then hold two spellings of one page).
    if (opensInbox) return `/inbox?tab=review&song=${song.id}`;
    return `/library?album=${encodeURIComponent(albumKeyForSong(song))}&track=${song.id}`;
  });
  const libraryLabel = $derived(opensInbox ? 'Open in Inbox' : 'Open in library');

  // Back is the nav bar's: on a phone the tab stack's previous page (else Tracks); on desktop the
  // same target, passed explicitly because a desktop bar only shows Back when a page asks for it.
  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);
  const desktopBack = $derived(
    compact ? undefined : tabMemory.backTarget(page.url, page.data.user)
  );

  // Re-enrich is an admin endpoint; it used to be offered to everyone and fail silently.
  async function handleReenrich() {
    if (!song || reenriching) return;
    reenriching = true;
    try {
      await enrichSong(song.id, true);
      await loadAll(song.id);
    } catch (err) {
      toast.error('Could not re-enrich this track', {
        description: err instanceof Error ? err.message : undefined
      });
    } finally {
      reenriching = false;
    }
  }

  // ── soulseek quality upgrade (owner-only) ────────────────────────────────────
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
    if (u.status === 'Deferred')
      return u.error
        ? `Provider was unavailable — ${u.error}. Will retry automatically.`
        : 'Provider was unavailable. Will retry automatically.';
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

  // Per-track sync state badge (Push deployments only; null otherwise). Token fills; success is a
  // tint check glyph beside foreground text, never tint text.
  const syncBadge = $derived.by(() => {
    const ts = detail?.trackSync;
    if (!ts) return null;
    switch (ts.status) {
      case 'Synced':
        return { label: 'Synced', cls: 'bg-primary/12 text-foreground', ok: true };
      case 'Uploading':
        return { label: 'Uploading', cls: 'bg-secondary text-foreground', ok: false };
      case 'SkippedRemoteBetter':
        return { label: 'Remote has better', cls: 'bg-muted text-muted-foreground', ok: false };
      case 'Failed':
        return { label: 'Sync failed', cls: 'bg-destructive/10 text-destructive-text', ok: false };
      default:
        return { label: 'Sync pending', cls: 'bg-secondary text-foreground', ok: false };
    }
  });
  // The failure reason used to be a hover tooltip; it is a resting line under the badges now.
  const syncError = $derived(
    detail?.trackSync?.status === 'Failed' ? (detail.trackSync.lastError ?? null) : null
  );

  async function copy(label: string, value: string) {
    try {
      await navigator.clipboard.writeText(value);
      toast.success(`${label} copied`);
    } catch {
      toast.error('Could not copy to the clipboard');
    }
  }

  // The name the Back label and the browser-tab title use for this page.
  $effect(() => {
    if (heroTitle) tabMemory.setTitle(page.url, heroTitle);
  });
</script>

<!-- The page is one scroller with the nav bar as its first child, so the large title scrolls away
     and the bar collapses. Back is the bar's (it replaced a trailing "← Back" pill). It is an info
     page on the grouped background — Match, File, Identifiers as inset sections, the way Now
     Playing's Info reads — rather than bordered cards, stat tiles and a paragraph of values. -->
<ScrollArea class="min-h-0 flex-1">
  <PageToolbarV2
    title="Timeline"
    meta={song ? `${song.artist ?? ''} — ${song.title ?? song.fileName}` : undefined}
    back={desktopBack}
    grouped
  >
    {#snippet actions()}
      {#if isOwner}
        <!-- The one prominent action. -->
        {#if compact}
          <Button
            size="icon"
            onclick={handleReenrich}
            disabled={reenriching || !song}
            aria-label="Re-enrich"
          >
            {#if reenriching}<Loader2 class="animate-spin" />{:else}<RefreshCw />{/if}
          </Button>
        {:else}
          <Button
            size="sm"
            class="h-8 rounded-full px-3"
            onclick={handleReenrich}
            disabled={reenriching || !song}
          >
            {#if reenriching}<Loader2 class="animate-spin" />{:else}<RefreshCw />{/if}
            Re-enrich
          </Button>
        {/if}
        <DropdownMenu.Root>
          <DropdownMenu.Trigger>
            {#snippet child({ props })}
              <Button {...props} variant="ghost" size="icon" aria-label="More">
                <Ellipsis />
              </Button>
            {/snippet}
          </DropdownMenu.Trigger>
          <DropdownMenu.Content align="end" class="w-64 pointer-coarse:w-72">
            {#if soulseekConfigured}
              <DropdownMenu.Item
                onSelect={handleFindBetterQuality}
                disabled={requestingUpgrade || !song || detail?.upgrade?.active === true}
              >
                {#if requestingUpgrade || detail?.upgrade?.active}
                  <Loader2 class="animate-spin" />
                {:else}
                  <Search />
                {/if}
                {upgradeActiveLabel ?? 'Find better quality'}
              </DropdownMenu.Item>
            {/if}
            <DropdownMenu.Item onSelect={() => void goto(libraryHref)}>
              {#if opensInbox}<Inbox />{:else}<Disc3 />{/if}
              {libraryLabel}
            </DropdownMenu.Item>
          </DropdownMenu.Content>
        </DropdownMenu.Root>
      {:else}
        <!-- Without admin actions a one-item menu would be a detour: the link is the button. An
             album glyph, not an "external link" one: it goes to the track's album, in the app. -->
        <Button variant="ghost" size="icon" href={libraryHref} aria-label={libraryLabel}>
          <Disc3 />
        </Button>
      {/if}
    {/snippet}
  </PageToolbarV2>

  <div class="mx-auto flex max-w-3xl flex-col gap-8 pt-2 pb-8 md:px-7 md:pt-5">
    <!-- The quality upgrade: its progress while one runs (the ⋯ item that shows it is out of
         sight at rest), then its outcome, only when there is one to report. -->
    {#if isOwner && soulseekConfigured && upgradeActiveLabel && !upgradeRequestError}
      <p
        class="text-footnote text-muted-foreground md:text-nav-xs inline-flex items-center gap-1.5 px-4 md:px-0"
        role="status"
      >
        <Loader2 class="size-3.5 shrink-0 animate-spin" aria-hidden="true" />
        Better quality: {upgradeActiveLabel}
      </p>
    {:else if isOwner && soulseekConfigured && (upgradeRequestError || upgradeTerminalNote)}
      <p class="text-footnote text-muted-foreground md:text-nav-xs px-4 md:px-0">
        {upgradeRequestError ?? upgradeTerminalNote}
      </p>
    {/if}

    {#if !loaded}
      <div class="flex flex-col items-center gap-3 px-4 md:flex-row md:items-end md:gap-6 md:px-0">
        <Skeleton class="size-40 rounded-[10px] md:size-32" />
        <div class="flex w-full flex-col items-center gap-2 md:items-start">
          <Skeleton class="h-7 w-56" />
          <Skeleton class="h-4 w-40" />
        </div>
      </div>
      <Skeleton class="mx-4 h-40 rounded-xl md:mx-0" />
      <Skeleton class="mx-4 h-28 rounded-xl md:mx-0" />
    {:else if loadError || !song}
      <div class="flex flex-col items-center gap-3 px-6 py-10 text-center">
        <AlertTriangle class="text-muted-foreground size-7" aria-hidden="true" />
        <h2 class="text-headline">Track not found</h2>
        <p class="text-muted-foreground text-subheadline max-w-sm">
          {loadError ?? 'We could not load this track.'}
        </p>
        <a
          href="/library"
          class="text-primary text-subheadline relative inline-flex min-h-11 items-center gap-0.5 font-medium hover:underline"
        >
          Back to library <ChevronRight class="size-3.5" aria-hidden="true" />
        </a>
      </div>
    {:else}
      <!-- The album page's hero: the art, the title, one subtitle line. Centred on a phone,
           side by side on a desktop. -->
      <section
        class="flex flex-col items-center gap-4 px-4 text-center md:flex-row md:items-end md:gap-6 md:px-0 md:text-left"
      >
        <Cover
          artist={heroArtist || 'Unknown'}
          title={heroAlbum || heroTitle}
          coverUrl={coverUrlForSong(song)}
          size={160}
          corner={10}
          caption={false}
          dprCap={3}
          class="shadow-[0_8px_24px_rgb(0_0_0/0.18)] md:size-32!"
        />
        <div class="min-w-0 md:pb-1">
          <h2 class="text-title-2 text-balance break-words">{heroTitle}</h2>
          <p class="text-muted-foreground text-subheadline mt-1">
            {[heroArtist, heroAlbum, heroYear != null ? String(heroYear) : null]
              .filter(Boolean)
              .join(' · ') || '—'}
          </p>
        </div>
      </section>

      <GroupedList.Section headingLevel={3} header="Match">
        <GroupedList.Row label="Decision" value={decision} />
        <GroupedList.Row label="Confidence" value={confidence} />
        <GroupedList.Row
          label="Providers"
          value={`${providerAttemptCount} (${contributingCount} contributed)`}
        />
        {#if grade?.graded && grade.score != null}
          <GroupedList.Row
            label="AI grade"
            value={`${grade.score}/100${grade.verdict ? ` · ${grade.verdict}` : ''}`}
          />
        {/if}
        <GroupedList.Row label="Processing time" value={wallClock} />
        {#if syncBadge}
          <GroupedList.Row label="Sync" sublabel={syncError ?? undefined}>
            {#snippet trailing()}
              <span
                class={cn(
                  'text-body inline-flex items-center gap-1.5 md:text-sm',
                  syncBadge.label === 'Sync failed' ? 'text-destructive-text' : 'text-muted-foreground'
                )}
              >
                {#if syncBadge.ok}<CircleCheck class="text-primary size-4" aria-hidden="true" />{/if}
                {syncBadge.label}
              </span>
            {/snippet}
          </GroupedList.Row>
        {/if}
      </GroupedList.Section>

      <GroupedList.Section headingLevel={3} header="File">
        <GroupedList.Row label="Duration" value={formatDuration(song.durationSeconds)} />
        <GroupedList.Row label="Format" value={bitrateChip()} />
        <GroupedList.Row label="Size" value={formatFileSize(song.fileSizeBytes)} />
      </GroupedList.Section>

      {#if song.fingerprint || song.musicBrainzId}
        <GroupedList.Section
          headingLevel={3}
          header="Identifiers"
          footer="Tap an identifier to copy it."
        >
          {#if song.fingerprint}
            <!-- The chromaprint is long: the row shows its head and copies the whole thing. -->
            {@render copyRow('Fingerprint', `${song.fingerprint.slice(0, 22)}…`, song.fingerprint)}
          {/if}
          {#if song.musicBrainzId}
            {@render copyRow('MusicBrainz recording', song.musicBrainzId, song.musicBrainzId)}
          {/if}
        </GroupedList.Section>
      {/if}

      <!-- Source → Destination paths. Admin only: a shared track publishes neither path, so for
           anyone else this could only claim, wrongly, that the track was never built. -->
      {#if isOwner}
        <GroupedList.Section
          headingLevel={3}
          header="Location"
          footer={destinationPath
            ? undefined
            : 'Not written to the library yet — it lands there once it clears review and the build runs.'}
        >
          {#if sourcePath}
            {@render copyRow('Source path', sourcePath, sourcePath)}
          {/if}
          {#if destinationPath}
            {@render copyRow('Library path', destinationPath, destinationPath)}
          {:else}
            <GroupedList.Row label="Library path" value="Not built yet" />
          {/if}
        </GroupedList.Section>
      {/if}

      <GroupedList.Section
        headingLevel={3}
        header="Providers that contributed"
        footer={contributed.length === 0
          ? 'Nothing matched. Re-enrich, or open the track in review to set the fields yourself.'
          : `${contributed.length} of ${providerAttemptCount} ${providerAttemptCount === 1 ? 'provider' : 'providers'} returned data this track used.`}
      >
        {#if contributed.length === 0}
          <GroupedList.Row label="None" disabled />
        {:else}
          {#each contributed as c (c.label)}
            <GroupedList.Row label={c.label}>
              {#snippet leading()}
                <span class="size-2.5 rounded-full" style="background: {c.color}" aria-hidden="true"
                ></span>
              {/snippet}
            </GroupedList.Row>
          {/each}
        {/if}
      </GroupedList.Section>

      <section aria-labelledby="timeline-heading" class="flex flex-col">
        <h3
          id="timeline-heading"
          class="text-footnote text-muted-foreground px-8 pb-1.5 md:px-4"
        >
          Timeline
        </h3>
        {#if timeline.length === 0}
          <p class="bg-card text-muted-foreground text-subheadline mx-4 rounded-xl px-4 py-6 text-center md:mx-0">
            No pipeline events recorded for this track yet.
          </p>
        {:else}
          <div class="mx-4 md:mx-0">
            <TimelineList events={timeline} />
          </div>
          <p class="text-footnote text-muted-foreground px-8 pt-1.5 md:px-4">
            {timeline.length}
            {timeline.length === 1 ? 'event' : 'events'} · {wallClock} end to end. Timestamps are
            real; how long each step took is not recorded yet.
          </p>
        {/if}
      </section>
    {/if}
  </div>
</ScrollArea>

{#snippet copyRow(label: string, shown: string, full: string)}
  <!-- Stacked, so a long id or path reads whole; the row is the copy button. -->
  <GroupedList.Row onclick={() => copy(label, full)} label={label} aria-label={`Copy ${label}`}>
    <span class="text-subheadline text-muted-foreground font-mono break-all md:text-xs">{shown}</span>
    {#snippet trailing()}
      <Copy class="text-muted-foreground size-4" aria-hidden="true" />
    {/snippet}
  </GroupedList.Row>
{/snippet}
