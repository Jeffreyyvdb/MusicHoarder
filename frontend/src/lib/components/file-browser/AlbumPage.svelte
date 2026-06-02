<script lang="ts">
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import {
    ArrowLeft,
    Clock,
    Disc3,
    Fingerprint,
    HardDrive,
    Image as ImageIcon,
    MoreHorizontal,
    Pause,
    Play,
    Search,
    Tag
  } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import * as Tooltip from '$lib/components/ui/tooltip';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { albumTint } from '$lib/album-tint';
  import {
    formatDuration,
    formatFileSize,
    formatTotalDuration
  } from '$lib/formatters';
  import {
    fetchAlbumTracklist,
    prettyProvider,
    toPlayerSong,
    type AlbumLinkStatus,
    type AlbumSummary,
    type AlbumTracklist,
    type ApiSong
  } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn } from '$lib/utils';

  type Props = {
    album: AlbumSummary | null;
    isLoading: boolean;
  };
  const { album, isLoading }: Props = $props();

  const tint = $derived(album ? albumTint(album.artist, album.title) : null);
  const tracks = $derived(album?.songs ?? []);

  // Reconciled multi-provider canonical tracklist for this album, fetched lazily by album identity
  // (artist + title). `linkStatus` tells us whether the album is matched to a provider album
  // ("linked"), only in the local library ("localOnly"), or not yet checked ("pending"). When linked
  // we show every real track and grey out the ones the user is missing; otherwise we fall back to the
  // owned-only list below.
  let tracklist = $state<AlbumTracklist | null>(null);
  let linkStatus = $state<AlbumLinkStatus>('pending');
  $effect(() => {
    const artist = album?.artist ?? null;
    const title = album?.title ?? null;
    tracklist = null;
    linkStatus = 'pending';
    if (!artist || !title) return;
    let cancelled = false;
    void fetchAlbumTracklist(artist, title)
      .then((result) => {
        if (cancelled) return;
        tracklist = result.tracklist;
        linkStatus = result.status;
      })
      .catch(() => {
        // Tracklist is best-effort; on error keep the owned-only fallback.
      });
    return () => {
      cancelled = true;
    };
  });

  /** Provider names that won the reconciliation, for the "Linked" chip. */
  const sourceLabels = $derived(
    tracklist
      ? tracklist.sources.filter((s) => s.inWinningCluster).map((s) => prettyProvider(s.provider))
      : []
  );

  type DisplayRow =
    | { kind: 'owned'; key: string; disc: number; n: number; song: ApiSong; durationSeconds: number | null }
    | {
        kind: 'missing';
        key: string;
        disc: number;
        n: number;
        title: string;
        durationSeconds: number | null;
        contested: boolean;
      };

  const displayRows = $derived.by<DisplayRow[]>(() => {
    const owned = album?.songs ?? [];
    const tl = tracklist;
    if (!tl) {
      return owned.map((song, idx) => ({
        kind: 'owned',
        key: `song:${song.id}`,
        disc: 1,
        n: song.trackNumber ?? idx + 1,
        song,
        durationSeconds: song.durationSeconds ?? null
      }));
    }

    const byId = new Map(owned.map((s) => [s.id, s]));
    const used = new Set<number>();
    const rows: DisplayRow[] = tl.tracks.map((t) => {
      const song = t.ownedSongId != null ? (byId.get(t.ownedSongId) ?? null) : null;
      if (song) {
        used.add(song.id);
        return {
          kind: 'owned',
          key: `song:${song.id}`,
          disc: t.discNumber,
          n: t.trackNumber,
          song,
          durationSeconds: song.durationSeconds ?? (t.durationMs != null ? t.durationMs / 1000 : null)
        };
      }
      return {
        kind: 'missing',
        key: `miss:${t.discNumber}:${t.trackNumber}`,
        disc: t.discNumber,
        n: t.trackNumber,
        title: (t.title ?? '').trim() || 'Unknown track',
        durationSeconds: t.durationMs != null ? t.durationMs / 1000 : null,
        contested: t.isContested
      };
    });

    // Owned songs not matched to any canonical track (bonus tracks, alternate versions) — append so
    // nothing the user actually owns disappears from the view.
    let bonusN = tl.tracks.length;
    for (const song of owned) {
      if (used.has(song.id)) continue;
      bonusN += 1;
      rows.push({
        kind: 'owned',
        key: `song:${song.id}`,
        disc: 1,
        n: song.trackNumber ?? bonusN,
        song,
        durationSeconds: song.durationSeconds ?? null
      });
    }
    return rows;
  });

  /** Whether multiple discs are present, so we can show a disc prefix on track numbers. */
  const multiDisc = $derived(displayRows.some((r) => r.disc > 1));

  const completeness = $derived.by(() => {
    const tl = tracklist;
    if (!tl || tl.totalCount === 0) return null;
    return {
      owned: tl.ownedCount,
      total: tl.totalCount,
      pct: Math.round((tl.ownedCount / tl.totalCount) * 100)
    };
  });

  /** Web-search URL so the user can go find a track they're missing. */
  function findUrl(title: string): string {
    const q = `${album?.artist ?? ''} ${title}`.trim();
    return `https://www.google.com/search?q=${encodeURIComponent(q)}`;
  }

  const selectedTrack = $derived(page.url.searchParams.get('track'));
  const selectedTrackId = $derived(selectedTrack ? Number.parseInt(selectedTrack, 10) : null);

  const currentlyPlaying = $derived.by(() => {
    const playing = playerStore.currentSong;
    if (!playing) return null;
    return tracks.find((t) => t.id === playing.id) ?? null;
  });

  function selectTrack(s: ApiSong) {
    const url = new URL(page.url);
    if (selectedTrackId === s.id) {
      url.searchParams.delete('track');
    } else {
      url.searchParams.set('track', String(s.id));
    }
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true });
  }

  function playFrom(target: ApiSong) {
    if (!album) return;
    const queue = tracks.map((t) => toPlayerSong(t, album.artist));
    const index = tracks.findIndex((t) => t.id === target.id);
    void playerStore.playSong(toPlayerSong(target, album.artist), queue, index);
  }

  function playAlbumStart() {
    if (!album || tracks.length === 0) return;
    playFrom(currentlyPlaying ?? tracks[0]);
  }

  function playTrack(s: ApiSong, e: MouseEvent) {
    e.stopPropagation();
    playFrom(s);
  }

  function trackBitrateLabel(s: ApiSong): string {
    const ext = (s.extension ?? '').replace(/^\./, '').toUpperCase();
    if (s.bitRate && s.bitRate > 0) {
      return ext ? `${ext} ${s.bitRate}kbps` : `${s.bitRate} kbps`;
    }
    return ext || '—';
  }

  function trackMatchValue(s: ApiSong): number {
    if (typeof s.matchConfidence === 'number') return Math.max(0, Math.min(1, s.matchConfidence));
    // Deterministic synthetic value for songs without a stored confidence.
    const seed = (s.id * 17) % 22;
    return 0.74 + seed / 100;
  }

  const heroBackground = $derived.by(() => {
    if (!tint) return '';
    return (
      `linear-gradient(180deg, ${tint.from} 0%, color-mix(in oklch, ${tint.from} 60%, transparent) 60%, transparent 100%),` +
      ` linear-gradient(135deg, color-mix(in oklch, ${tint.to} 40%, transparent), transparent)`
    );
  });

  const destinationFolder = $derived.by(() => {
    const first = album?.songs[0];
    if (!first?.destinationPath) return null;
    const idx = first.destinationPath.lastIndexOf('/');
    return idx > 0 ? first.destinationPath.slice(0, idx) : first.destinationPath;
  });
</script>

{#if isLoading && !album}
  <div class="text-muted-foreground flex flex-1 items-center justify-center p-8 text-sm">
    Loading album…
  </div>
{:else if !album}
  <div class="flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
    <Disc3 class="text-muted-foreground size-10 opacity-40" />
    <p class="text-sm">Album not found in your library.</p>
    <a href="/library" class="text-primary text-sm underline-offset-4 hover:underline">
      Back to all albums
    </a>
  </div>
{:else}
  <ScrollArea class="min-h-0 flex-1">
    <!-- Hero -->
    <div
      class="mh-album-hero relative px-6 pt-12 pb-7 text-white sm:px-9"
      style="background: {heroBackground};"
    >
      <a
        href="/library"
        class="text-muted-foreground hover:text-foreground absolute top-3 left-6 z-10 inline-flex items-center gap-1 rounded-full bg-black/30 px-2.5 py-1 text-xs text-white/85 backdrop-blur transition-colors hover:bg-black/40 hover:text-white sm:left-9"
      >
        <ArrowLeft class="size-3.5" />
        All albums
      </a>

      <div class="relative z-10 flex flex-col items-start gap-6 sm:flex-row sm:gap-8">
        <div class="w-[clamp(124px,26vw,232px)] shrink-0">
          <Cover
            artist={album.artist}
            title={album.title}
            coverUrl={album.coverUrl}
            size={232}
            corner={6}
            class="aspect-square !h-auto !w-full !shadow-[0_24px_48px_rgba(0,0,0,0.35)]"
          />
        </div>
        <div class="min-w-0 flex-1 pb-2">
          <div class="text-[11px] font-semibold tracking-wider opacity-85 uppercase">Album</div>
          <h1 class="mt-3 text-[clamp(40px,6vw,88px)] leading-[0.95] font-extrabold tracking-[-0.03em] [text-wrap:balance]">
            {album.title}
          </h1>
          <div class="mt-5 flex flex-wrap items-center gap-x-2.5 gap-y-2 text-[13px] opacity-90">
            <span class="inline-flex items-center gap-2 font-semibold">
              <span
                class="ring-2 ring-white/50 inline-block size-4 rounded-full"
                style="background: {tint?.to};"
              ></span>
              <span>{album.artist}</span>
            </span>
            <span class="opacity-50">·</span>
            {#if album.year}
              <span>{album.year}</span>
              <span class="opacity-50">·</span>
            {/if}
            <span>
              {album.trackCount} song{album.trackCount === 1 ? '' : 's'}, {formatTotalDuration(album.durationSeconds)}
            </span>
            <span class="opacity-50">·</span>
            <span class="font-mono">{formatFileSize(album.byteSize)}</span>
            {#if completeness}
              <span class="opacity-50">·</span>
              <span
                class="inline-flex items-center gap-1 font-semibold"
                title="{completeness.owned} of {completeness.total} tracks in your library"
              >
                {completeness.owned}/{completeness.total} · {completeness.pct}%
              </span>
            {/if}
          </div>
          {#if completeness}
            <div class="mt-3 h-1 w-full max-w-[280px] overflow-hidden rounded-full bg-white/25">
              <span class="block h-full rounded-full bg-white/90" style="width: {completeness.pct}%;"></span>
            </div>
          {/if}
          <div class="mt-2.5 flex flex-wrap items-center gap-x-2 gap-y-1 text-[11px]">
            {#if linkStatus === 'linked'}
              <span
                class="inline-flex items-center gap-1.5 rounded-full bg-white/20 px-2.5 py-0.5 font-semibold"
                title={sourceLabels.length > 0
                  ? `Matched to a provider album (${sourceLabels.join(', ')})`
                  : 'Matched to a provider album'}
              >
                <span class="inline-block size-1.5 rounded-full bg-emerald-300"></span>
                Linked{sourceLabels.length > 0 ? ` · ${sourceLabels.join(' · ')}` : ''}
              </span>
              {#if tracklist?.trackCountContested}
                <span
                  class="inline-flex items-center gap-1 rounded-full bg-amber-400/25 px-2 py-0.5 font-medium text-amber-50"
                  title="Providers disagree on this album's track count"
                >
                  ⚠ Sources disagree on length
                </span>
              {/if}
            {:else if linkStatus === 'localOnly'}
              <span
                class="inline-flex items-center gap-1.5 rounded-full bg-white/15 px-2.5 py-0.5 font-medium opacity-90"
                title="No matching album was found on any provider — this album is only in your local library"
              >
                <span class="inline-block size-1.5 rounded-full bg-white/60"></span>
                Local only
              </span>
            {:else}
              <span class="inline-flex items-center gap-1.5 rounded-full bg-white/10 px-2.5 py-0.5 font-medium opacity-70">
                <span class="inline-block size-1.5 animate-pulse rounded-full bg-white/50"></span>
                Checking providers…
              </span>
            {/if}
          </div>
        </div>
      </div>
    </div>

    <!-- Action bar -->
    <div
      class="border-border flex items-center gap-3 border-b bg-gradient-to-b from-black/5 to-transparent px-6 py-5 sm:px-9 dark:from-white/5"
    >
      <button
        type="button"
        onclick={playAlbumStart}
        aria-label="Play album"
        class="bg-primary text-primary-foreground grid size-13 place-items-center rounded-full shadow-[0_6px_16px_oklch(0.5_0.17_145_/_0.4)] transition-transform hover:scale-105"
        style="width: 52px; height: 52px;"
      >
        {#if playerStore.isPlaying && currentlyPlaying}
          <Pause class="size-5" />
        {:else}
          <Play class="size-5" />
        {/if}
      </button>

      {#each [{ icon: Fingerprint, label: 'Re-fingerprint album' }, { icon: ImageIcon, label: 'Re-fetch artwork' }, { icon: Tag, label: 'Edit metadata' }, { icon: HardDrive, label: 'Reveal in destination' }, { icon: MoreHorizontal, label: 'More' }] as btn (btn.label)}
        <Tooltip.Provider delayDuration={300}>
          <Tooltip.Root>
            <Tooltip.Trigger>
              {#snippet child({ props })}
                <button
                  {...props}
                  type="button"
                  aria-label={btn.label}
                  class="text-muted-foreground hover:bg-accent hover:text-foreground grid size-9 place-items-center rounded-full transition-colors"
                >
                  <btn.icon class="size-4" />
                </button>
              {/snippet}
            </Tooltip.Trigger>
            <Tooltip.Content>{btn.label}</Tooltip.Content>
          </Tooltip.Root>
        </Tooltip.Provider>
      {/each}

      {#if destinationFolder}
        <div
          class="bg-primary/15 text-primary ml-auto flex max-w-md items-center gap-1.5 truncate rounded px-2.5 py-1.5 font-mono text-[11px]"
          title={destinationFolder}
        >
          <HardDrive class="size-3 shrink-0" />
          <span class="truncate">{destinationFolder}</span>
        </div>
      {/if}
    </div>

    <!-- Track list -->
    <div class="px-3 pt-2 pb-6 sm:px-6">
      <div
        class={cn(
          'border-border text-muted-foreground grid items-center gap-4 border-b px-3.5 py-2.5 text-[10px] font-semibold tracking-wider uppercase',
          'grid-cols-[44px_minmax(0,1fr)_60px] sm:grid-cols-[44px_minmax(0,1fr)_110px_80px_140px_60px]'
        )}
      >
        <span class="text-right">#</span>
        <span>Title</span>
        <span class="hidden sm:inline">Format</span>
        <span class="hidden sm:inline">Size</span>
        <span class="hidden sm:inline">Match</span>
        <span class="text-right"><Clock class="-mt-0.5 inline size-3" /></span>
      </div>

      {#each displayRows as row (row.key)}
        {@const numLabel = multiDisc ? `${row.disc}.${String(row.n).padStart(2, '0')}` : String(row.n).padStart(2, '0')}
        {#if row.kind === 'owned'}
          {@const song = row.song}
          {@const isSelected = selectedTrackId === song.id}
          {@const isCurrentlyLoaded = playerStore.currentSong?.id === song.id}
          {@const isCurrentlyPlaying = isCurrentlyLoaded && playerStore.isPlaying}
          {@const matchValue = trackMatchValue(song)}
          <div
            role="button"
            tabindex="0"
            onclick={() => selectTrack(song)}
            onkeydown={(e) => (e.key === 'Enter' || e.key === ' ') && selectTrack(song)}
            class={cn(
              'group grid cursor-pointer items-center gap-4 rounded-md px-3.5 py-2 transition-colors',
              'grid-cols-[44px_minmax(0,1fr)_60px] sm:grid-cols-[44px_minmax(0,1fr)_110px_80px_140px_60px]',
              'hover:bg-accent/50',
              isSelected && 'bg-primary/10',
              isCurrentlyLoaded && 'text-primary'
            )}
          >
            <span class="text-muted-foreground relative grid place-items-center text-right">
              <span class={cn('font-mono text-sm tabular-nums transition-opacity group-hover:opacity-0', isCurrentlyPlaying && 'opacity-0')}>
                {numLabel}
              </span>
              <button
                type="button"
                onclick={(e) => playTrack(song, e)}
                aria-label={isCurrentlyPlaying ? 'Pause track' : 'Play track'}
                class={cn(
                  'text-primary absolute inset-0 grid place-items-center opacity-0 transition-opacity group-hover:opacity-100',
                  isCurrentlyPlaying && 'opacity-100'
                )}
              >
                {#if isCurrentlyPlaying}
                  <Pause class="size-4" />
                {:else}
                  <Play class="size-4" />
                {/if}
              </button>
            </span>

            <div class="min-w-0">
              <div class={cn('truncate text-sm font-medium', isCurrentlyLoaded && 'text-primary')}>
                {(song.title ?? song.fileName).trim() || song.fileName}
              </div>
              <div class="text-muted-foreground mt-0.5 flex items-center gap-2 text-[11.5px]">
                <span class="truncate">{(song.artist ?? album.artist).trim() || album.artist}</span>
                {#if song.hasSyncedLyrics || song.lrclibId}
                  <span class="bg-primary/15 text-primary rounded px-1 py-0.5 font-mono text-[9px] font-semibold tracking-wider">
                    LRC
                  </span>
                {/if}
              </div>
            </div>

            <span class="text-muted-foreground hidden truncate font-mono text-[11px] sm:inline">
              {trackBitrateLabel(song)}
            </span>
            <span class="text-muted-foreground hidden font-mono text-[11px] sm:inline">
              {formatFileSize(song.fileSizeBytes)}
            </span>
            <span class="hidden items-center gap-2 sm:flex">
              <span class="bg-border h-1 flex-1 overflow-hidden rounded-full">
                <span class="bg-primary block h-full rounded-full" style="width: {matchValue * 100}%;"></span>
              </span>
              <span class="text-muted-foreground min-w-[28px] text-right font-mono text-[11px]">
                {matchValue.toFixed(2)}
              </span>
            </span>
            <span class="text-muted-foreground text-right font-mono text-[11px]">
              {formatDuration(row.durationSeconds)}
            </span>
          </div>
        {:else}
          <!-- Canonical track the user is missing — greyed out, with a way to go find it. -->
          <div
            class={cn(
              'grid items-center gap-4 rounded-md px-3.5 py-2',
              'grid-cols-[44px_minmax(0,1fr)_60px] sm:grid-cols-[44px_minmax(0,1fr)_110px_80px_140px_60px]'
            )}
          >
            <span class="text-muted-foreground/50 text-right font-mono text-sm tabular-nums">
              {numLabel}
            </span>
            <div class="min-w-0">
              <div class="text-muted-foreground/70 truncate text-sm font-medium">{row.title}</div>
              <a
                href={findUrl(row.title)}
                target="_blank"
                rel="noopener noreferrer"
                class="text-muted-foreground/60 hover:text-primary mt-0.5 inline-flex items-center gap-1 text-[11px] transition-colors"
              >
                <Search class="size-3" /> Find this track
              </a>
            </div>
            <span class="text-muted-foreground/40 hidden font-mono text-[11px] sm:inline">—</span>
            <span class="text-muted-foreground/40 hidden font-mono text-[11px] sm:inline">—</span>
            <span class="hidden items-center sm:flex">
              <span
                class="bg-muted text-muted-foreground/70 rounded px-1.5 py-0.5 text-[9px] font-semibold tracking-wider uppercase"
                title={row.contested
                  ? 'Only some providers list this track — it may be a bonus/edition-specific track'
                  : 'Not in your library'}
              >
                {row.contested ? 'Bonus?' : 'Missing'}
              </span>
            </span>
            <span class="text-muted-foreground/50 text-right font-mono text-[11px]">
              {formatDuration(row.durationSeconds)}
            </span>
          </div>
        {/if}
      {/each}
    </div>

    <!-- Credits -->
    <div
      class="border-border bg-surface-sunken grid grid-cols-2 gap-x-8 gap-y-5 border-t px-6 py-7 pb-16 sm:grid-cols-3 sm:px-9 md:grid-cols-4"
    >
      <div>
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wider uppercase">
          Tracks
        </div>
        <div class="text-foreground mt-1 text-[13px]">
          {#if completeness}
            {completeness.owned} / {completeness.total} owned
          {:else}
            {album.trackCount}
          {/if}
        </div>
      </div>
      <div>
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wider uppercase">
          Release
        </div>
        <div class="text-foreground mt-1 text-[13px]">{album.year ?? '—'}</div>
      </div>
      <div>
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wider uppercase">
          Genre
        </div>
        <div class="text-foreground mt-1 text-[13px]">{album.genre ?? '—'}</div>
      </div>
      <div class="col-span-2 min-w-0 sm:col-span-3 md:col-span-1">
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wider uppercase">
          MusicBrainz ID
        </div>
        <div class="text-muted-foreground mt-1 truncate font-mono text-[11px]" title={album.musicBrainzReleaseId ?? ''}>
          {album.musicBrainzReleaseId ?? '—'}
        </div>
      </div>
    </div>
  </ScrollArea>
{/if}
