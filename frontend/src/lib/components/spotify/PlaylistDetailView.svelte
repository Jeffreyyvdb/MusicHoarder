<script lang="ts">
  import type { SpotifyApiPlaylist, SpotifyApiTrack } from '$lib/api-client';
  import { fetchSpotifyPlaylistTracks } from '$lib/api-client';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { ArrowLeft, Search, Clock, AlertCircle, ListMusic, Play } from '@lucide/svelte';
  import { albumTint } from '$lib/album-tint';
  import { computeInitials, formatTotalDuration } from '$lib/formatters';
  import SpotifyTrackRow from './SpotifyTrackRow.svelte';
  import PaginationControls from './PaginationControls.svelte';
  import TrackListSkeleton from './TrackListSkeleton.svelte';

  type Props = { playlist: SpotifyApiPlaylist; onBack: () => void };
  const { playlist, onBack }: Props = $props();

  const limit = 50;

  let tracks = $state<SpotifyApiTrack[]>([]);
  let total = $state(0);
  let offset = $state(0);
  let isLoading = $state(true);
  let error = $state<string | null>(null);
  let searchQuery = $state('');
  let expandedMatchIds = $state(new Set<string>());

  async function loadTracks(newOffset: number) {
    isLoading = true;
    error = null;
    try {
      const result = await fetchSpotifyPlaylistTracks(playlist.spotifyId, newOffset, limit);
      tracks = result.items;
      total = result.total;
      offset = result.offset;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to load tracks';
    } finally {
      isLoading = false;
    }
  }

  $effect(() => {
    void loadTracks(0);
  });

  const filteredTracks = $derived(
    searchQuery
      ? tracks.filter(
          (t) =>
            t.title.toLowerCase().includes(searchQuery.toLowerCase()) ||
            t.artist.toLowerCase().includes(searchQuery.toLowerCase()) ||
            t.album.toLowerCase().includes(searchQuery.toLowerCase())
        )
      : tracks
  );

  function toggleExpand(id: string) {
    const next = new Set(expandedMatchIds);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    expandedMatchIds = next;
  }

  const tint = $derived(albumTint(playlist.ownerName ?? 'Spotify', playlist.name));
  const initials = $derived(computeInitials(playlist.name));

  // Total duration is summed from the *loaded page* — Spotify's playlist endpoint
  // doesn't return a total in the list response, so the figure reflects what's
  // visible and grows as the user paginates.
  const visibleDurationSeconds = $derived(
    Math.floor(tracks.reduce((acc, t) => acc + (t.durationMs ?? 0), 0) / 1000)
  );

  const heroBackground = $derived(
    `linear-gradient(180deg, ${tint.from} 0%, color-mix(in oklch, ${tint.from} 60%, transparent) 60%, transparent 100%),` +
      ` linear-gradient(135deg, color-mix(in oklch, ${tint.to} 40%, transparent), transparent)`
  );
</script>

<div class="flex min-h-0 flex-1 flex-col overflow-hidden">
  <ScrollArea class="min-h-0 flex-1">
    <button
      type="button"
      onclick={onBack}
      class="absolute top-3 left-3 z-10 inline-flex items-center gap-1 rounded-full bg-black/30 px-2.5 py-1 text-xs text-white/85 backdrop-blur transition-colors hover:bg-black/40 hover:text-white sm:left-6"
    >
      <ArrowLeft class="size-3.5" />
      Back
    </button>

    <!-- Hero -->
    <div
      class="relative px-6 pt-12 pb-7 text-white sm:px-9"
      style="background: {heroBackground};"
    >
      <div class="relative z-10 flex flex-col items-end gap-6 sm:flex-row sm:gap-8">
        <div
          class="relative grid shrink-0 place-items-center overflow-hidden shadow-[0_24px_48px_rgba(0,0,0,0.35)]"
          style="width: 232px; height: 232px; border-radius: 6px; background: linear-gradient(135deg, {tint.from} 0%, {tint.to} 100%);"
        >
          <div class="mh-cover-grain pointer-events-none absolute inset-0"></div>
          {#if playlist.imageUrl}
            <img
              src={playlist.imageUrl}
              alt=""
              loading="lazy"
              crossorigin="anonymous"
              class="absolute inset-0 size-full object-cover"
            />
          {:else}
            <div
              class="relative z-[2] font-bold tracking-[-0.04em] text-white/95 [text-shadow:_0_1px_2px_rgba(0,0,0,0.2)]"
              style="font-size: 64px;"
            >
              {initials}
            </div>
            <div
              class="absolute right-[8%] bottom-[7%] left-[8%] z-[2] truncate text-center font-mono text-[10px] font-medium tracking-[0.08em] text-white/75 uppercase"
            >
              {playlist.ownerName ?? 'Spotify'}
            </div>
          {/if}
        </div>

        <div class="min-w-0 flex-1 pb-2">
          <div class="text-[11px] font-semibold tracking-wider opacity-85 uppercase">Playlist</div>
          <h1
            class="mt-3 text-[clamp(36px,5.5vw,80px)] leading-[0.95] font-extrabold tracking-[-0.03em] [text-wrap:balance]"
          >
            {playlist.name}
          </h1>
          {#if playlist.description}
            <p class="mt-3 max-w-2xl text-sm leading-snug text-white/80 [text-wrap:pretty]">
              {playlist.description}
            </p>
          {/if}
          <div class="mt-5 flex flex-wrap items-center gap-x-2.5 gap-y-2 text-[13px] opacity-90">
            {#if playlist.ownerName}
              <span class="inline-flex items-center gap-2 font-semibold">
                <span
                  class="ring-2 ring-white/50 inline-block size-4 rounded-full"
                  style="background: {tint.to};"
                ></span>
                <span>{playlist.ownerName}</span>
              </span>
              <span class="opacity-50">·</span>
            {/if}
            <span>
              {playlist.trackCount} song{playlist.trackCount === 1 ? '' : 's'}
            </span>
            {#if visibleDurationSeconds > 0}
              <span class="opacity-50">·</span>
              <span>
                {formatTotalDuration(visibleDurationSeconds)}{tracks.length < playlist.trackCount
                  ? '+'
                  : ''}
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
        aria-label="Play playlist"
        disabled
        title="Playback for Spotify playlists is not wired up yet"
        class="bg-[#1DB954] text-white grid place-items-center rounded-full shadow-[0_6px_16px_rgba(29,185,84,0.4)] transition-transform hover:scale-105 disabled:cursor-not-allowed disabled:opacity-60 disabled:hover:scale-100"
        style="width: 52px; height: 52px;"
      >
        <Play class="size-5 fill-current" />
      </button>

      <div class="text-muted-foreground ml-auto flex items-center gap-3 text-xs">
        <span class="bg-[#1DB954]/15 text-[#1DB954] rounded px-2.5 py-1 font-mono">
          SPOTIFY
        </span>
      </div>
    </div>

    <!-- Filter -->
    <div class="border-border flex items-center gap-3 border-b px-4 py-3 md:px-6">
      <div class="relative max-w-md flex-1">
        <Search class="text-muted-foreground absolute top-1/2 left-3 size-4 -translate-y-1/2" />
        <Input
          placeholder="Filter tracks..."
          bind:value={searchQuery}
          class="bg-secondary border-0 pl-9"
        />
      </div>
      <span class="text-muted-foreground shrink-0 text-sm">{total} tracks</span>
    </div>

    {#if error}
      <div class="flex flex-col items-center justify-center py-12 text-center">
        <AlertCircle class="text-destructive mb-3 size-10" />
        <p class="text-muted-foreground">{error}</p>
        <Button variant="outline" size="sm" class="mt-4" onclick={() => loadTracks(offset)}>
          Retry
        </Button>
      </div>
    {:else if isLoading}
      <TrackListSkeleton />
    {:else}
      <div
        class="text-muted-foreground border-border/50 hidden items-center gap-3 border-b px-6 py-2 text-xs md:flex"
      >
        <span class="w-8 text-right">#</span>
        <span class="size-10"></span>
        <span class="flex-1">Title</span>
        <span class="hidden max-w-[200px] md:block">Album</span>
        <span class="w-12 text-right"><Clock class="inline size-3.5" /></span>
        <span class="w-[120px] shrink-0 text-right">Library</span>
      </div>
      <div class="flex flex-col gap-2 p-2 md:px-4">
        {#each filteredTracks as track, i (`${track.spotifyId}-${i}`)}
          <SpotifyTrackRow
            {track}
            index={offset + i}
            showDateAdded={false}
            expanded={expandedMatchIds.has(track.spotifyId)}
            onToggleExpand={() => toggleExpand(track.spotifyId)}
          />
        {/each}
        {#if filteredTracks.length === 0}
          <div class="flex flex-col items-center justify-center py-12 text-center">
            <ListMusic class="text-muted-foreground mb-3 size-10" />
            <p class="text-muted-foreground">No tracks found</p>
          </div>
        {/if}
      </div>
      <PaginationControls {offset} {limit} {total} onPageChange={loadTracks} {isLoading} />
    {/if}
  </ScrollArea>
</div>

<style>
  .mh-cover-grain {
    background:
      radial-gradient(circle at 30% 20%, rgba(255, 255, 255, 0.25), transparent 50%),
      radial-gradient(circle at 70% 80%, rgba(0, 0, 0, 0.2), transparent 50%);
  }
</style>
