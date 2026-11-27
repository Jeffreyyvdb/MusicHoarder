<script lang="ts">
  import { untrack } from 'svelte';
  import {
    Check,
    CircleCheck,
    Pause,
    Play,
    Plus,
    RotateCw,
    TriangleAlert,
    X
  } from '@lucide/svelte';
  import Headphones from '@lucide/svelte/icons/headphones';
  import { page } from '$app/state';
  import { toast } from 'svelte-sonner';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { EmptyState } from '#lib/components/ui/empty-state/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { SearchField } from '#lib/components/ui/search-field/index.ts';
  import { Spinner } from '#lib/components/ui/spinner/index.ts';
  import Cover from '#lib/components/file-browser/Cover.svelte';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import { importTrack, type CatalogTrack } from '#lib/api-client.ts';
  import { messageOf } from '#lib/artist-discography.ts';
  import { buildOwnedIndex, isInLibrary } from '#lib/catalog-match.ts';
  import { catalogPreview, previewAvailable } from '#lib/catalog-preview.svelte.ts';
  import { createCatalogSearch } from '#lib/catalog-search.svelte.ts';
  import { formatDuration } from '#lib/formatters.ts';
  import { IsMobile } from '#lib/hooks/is-mobile.svelte.ts';
  import { replaceUrl } from '#lib/navigation/replace-url.ts';
  import { songsStore } from '#lib/stores/songs.svelte.ts';
  import { localPlayback } from '#lib/stores/player.svelte.ts';
  import { tabMemory } from '#lib/stores/tab-memory.svelte.ts';
  import { cn } from '#lib/utils.ts';

  // Find music: search the Deezer catalog, hear a result, queue its download. The preview is its
  // own small player (#lib/catalog-preview.svelte.ts), never the library's.

  const search = createCatalogSearch();
  const isMobile = new IsMobile();
  const canPreview = previewAvailable();

  let query = $state(untrack(() => page.url.searchParams.get('q') ?? ''));
  let searchInput = $state<HTMLInputElement | null>(null);

  $effect(() => {
    search.set(query);
    const q = query.trim();
    untrack(() => {
      const url = new URL(page.url.href);
      if ((url.searchParams.get('q') ?? '') === q) return;
      if (q) url.searchParams.set('q', q);
      else url.searchParams.delete('q');
      void replaceUrl(url);
    });
  });

  $effect(() => {
    if (!isMobile.current) searchInput?.focus();
  });

  $effect(() => {
    tabMemory.setTitle(page.url, 'Find music');
  });

  $effect(() => {
    void songsStore.revalidate();
  });

  // The main player starting a song ends the preview, and leaving the page releases the audio.
  $effect(() => {
    if (localPlayback.isPlaying) catalogPreview.yieldToMainPlayer();
  });
  $effect(() => () => {
    search.cancel();
    catalogPreview.release();
  });

  const owned = $derived(buildOwnedIndex(songsStore.builtSongs));

  let adding = $state.raw<ReadonlySet<string>>(new Set());
  let queued = $state.raw<ReadonlySet<string>>(new Set());

  async function add(track: CatalogTrack) {
    adding = new Set([...adding, track.deezerId]);
    try {
      await importTrack({
        deezerTrackId: track.deezerId,
        title: track.title,
        artist: track.artist,
        album: track.album,
        durationMs: track.durationMs,
        coverUrl: track.coverUrl
      });
      queued = new Set([...queued, track.deezerId]);
    } catch (err) {
      toast.error(messageOf(err, 'Could not add that song.'));
    } finally {
      adding = new Set([...adding].filter((id) => id !== track.deezerId));
    }
  }

  const WISHLIST_LABELS: Record<string, string> = {
    Pending: 'In wishlist',
    Downloading: 'Downloading',
    Downloaded: 'Downloaded',
    SkippedOwned: 'In library',
    NotFound: 'Not found'
  };

  /** What the Add control shows: a word once the song is queued or known, else the plus. */
  function addState(track: CatalogTrack): { label: string | null; disabled: boolean } {
    if (adding.has(track.deezerId)) return { label: null, disabled: true };
    if (queued.has(track.deezerId)) return { label: 'Queued', disabled: true };
    const status = track.wishlistStatus;
    if (status != null && status !== 'Failed') {
      return { label: WISHLIST_LABELS[status] ?? 'In wishlist', disabled: true };
    }
    return { label: null, disabled: false };
  }

  const current = $derived(catalogPreview.track);
  const fullState = $derived(catalogPreview.full);
  const progress = $derived(
    catalogPreview.duration > 0 ? catalogPreview.position / catalogPreview.duration : 0
  );

  function previewIcon(id: string): 'play' | 'pause' | 'loading' | 'error' {
    if (current?.deezerId !== id) return 'play';
    const s = catalogPreview.state;
    if (s === 'playing') return 'pause';
    if (s === 'loading') return 'loading';
    if (s === 'error') return 'error';
    return 'play';
  }

  function toggle(track: CatalogTrack) {
    catalogPreview.toggle({ deezerId: track.deezerId, title: track.title, artist: track.artist });
  }

  function clearSearch() {
    query = '';
    searchInput?.focus();
  }

  const meta = $derived(
    search.status === 'ready' && search.results.length > 0
      ? `${search.results.length} result${search.results.length === 1 ? '' : 's'}`
      : undefined
  );
</script>

{#snippet searchField()}
  <SearchField bind:value={query} bind:ref={searchInput} label="Search for a song" />
{/snippet}

{#snippet trailing(track: CatalogTrack)}
  {@const add_ = addState(track)}
  {#if canPreview && track.hasPreview}
    {@const icon = previewIcon(track.deezerId)}
    <Button
      variant="ghost"
      class="text-foreground size-11 shrink-0 rounded-full p-0"
      aria-label={icon === 'pause'
        ? `Pause preview of ${track.title}`
        : `Preview ${track.title} by ${track.artist}`}
      onclick={() => toggle(track)}
    >
      {#if icon === 'pause'}
        <Pause class="size-5" aria-hidden="true" />
      {:else if icon === 'loading'}
        <Spinner class="size-5" />
      {:else if icon === 'error'}
        <RotateCw class="text-warning-text size-5" aria-hidden="true" />
      {:else}
        <Play class="size-5" aria-hidden="true" />
      {/if}
    </Button>
  {/if}
  {#if adding.has(track.deezerId)}
    <span class="flex size-11 shrink-0 items-center justify-center" role="status">
      <Spinner class="size-5" />
      <span class="sr-only">Adding {track.title}</span>
    </span>
  {:else if add_.label}
    <span
      class="text-footnote text-muted-foreground flex h-11 shrink-0 items-center gap-1 px-1 md:text-xs"
    >
      <CircleCheck class="text-primary size-4" aria-hidden="true" />
      {add_.label}
    </span>
  {:else}
    <Button
      variant="ghost"
      class={cn(
        'size-11 shrink-0 rounded-full p-0',
        isInLibrary(owned, track) ? 'text-muted-foreground' : 'text-primary'
      )}
      aria-label={`${track.wishlistStatus === 'Failed' ? 'Retry adding' : 'Add'} ${track.title} by ${track.artist} to your library`}
      onclick={() => void add(track)}
    >
      <Plus class="size-5" aria-hidden="true" />
    </Button>
  {/if}
{/snippet}

{#snippet body(track: CatalogTrack, inLibrary: boolean)}
  <Cover
    artist={track.artist}
    title={track.title}
    coverUrl={track.coverUrl}
    size={48}
    corner={6}
    caption={false}
  />
  <span class="min-w-0 flex-1">
    <span class="flex items-center gap-1.5">
      <span
        class={cn(
          'text-body truncate md:text-sm',
          current?.deezerId === track.deezerId && 'text-primary'
        )}>{track.title}</span
      >
      {#if track.explicit}
        <span
          class="text-caption-2 text-muted-foreground border-border shrink-0 rounded-[3px] border px-1 leading-4"
          title="Explicit"
          aria-label="Explicit">E</span
        >
      {/if}
    </span>
    <span class="text-subheadline text-muted-foreground flex items-center gap-1 md:text-xs">
      <span class="truncate">
        {track.artist}{track.album ? ` · ${track.album}` : ''}
      </span>
      <span class="shrink-0">· {formatDuration(track.durationMs / 1000)}</span>
    </span>
    {#if inLibrary}
      <span class="text-footnote text-muted-foreground flex items-center gap-1 md:text-xs">
        <Check class="size-3.5" aria-hidden="true" /> In library
      </span>
    {/if}
  </span>
{/snippet}

<ScrollArea class="min-h-0 flex-1">
  <PageToolbarV2 title="Find music" {meta} metaFrom="lg" search={searchField} />

  <div class="mx-auto flex w-full max-w-3xl flex-col pt-2 md:px-7 md:pt-4">
    {#if search.status === 'idle'}
      <EmptyState
        icon={Headphones}
        title="Find music"
        hint="Search for a song to preview it and add it to your library"
      />
    {:else if search.status === 'error'}
      <EmptyState
        icon={TriangleAlert}
        title="Search is unavailable"
        hint={search.error ?? 'Try again in a moment.'}
        action={{ label: 'Try again', onclick: () => search.retry() }}
      />
    {:else if search.status === 'loading' && search.results.length === 0}
      <div class="flex justify-center py-12" role="status">
        <Spinner class="size-5" />
        <span class="sr-only">Searching</span>
      </div>
    {:else if search.results.length === 0}
      <EmptyState
        icon={Headphones}
        title="No songs found"
        hint="Check the spelling, or try fewer words."
        action={{ label: 'Clear search', onclick: clearSearch }}
      />
    {:else}
      <!-- Not virtualized: the server caps a search at 25 results. -->
      <ul class={cn('transition-opacity', search.status === 'loading' && 'opacity-60')}>
        {#each search.results as track (track.deezerId)}
          {@const inLibrary = isInLibrary(owned, track)}
          <li class="flex min-h-16 items-center gap-1 pr-2 pl-4 md:rounded-lg md:px-2">
            {#if canPreview && track.hasPreview}
              <button
                type="button"
                class="focus-visible:ring-ring flex min-h-16 min-w-0 flex-1 items-center gap-3 rounded-lg text-left outline-none focus-visible:ring-2"
                onclick={() => toggle(track)}
              >
                {@render body(track, inLibrary)}
              </button>
            {:else}
              <div class="flex min-h-16 min-w-0 flex-1 items-center gap-3">
                {@render body(track, inLibrary)}
              </div>
            {/if}
            {@render trailing(track)}
          </li>
        {/each}
      </ul>
    {/if}
  </div>

  {#if current}
    <div
      role="region"
      aria-label="Preview"
      class="bg-card border-border sticky bottom-(--mh-content-pad) z-10 mx-4 mt-4 flex flex-col gap-2 rounded-xl border p-3 md:mx-auto md:w-full md:max-w-3xl"
      data-elevated=""
    >
      <div class="flex items-center gap-2">
        <Button
          variant="tinted"
          class="size-11 shrink-0 rounded-full p-0"
          aria-label={catalogPreview.state === 'playing' ? 'Pause preview' : 'Play preview'}
          onclick={() => catalogPreview.togglePause()}
        >
          {#if catalogPreview.state === 'playing'}
            <Pause class="size-5" aria-hidden="true" />
          {:else if catalogPreview.state === 'loading'}
            <Spinner class="size-5" />
          {:else}
            <Play class="size-5" aria-hidden="true" />
          {/if}
        </Button>
        <div class="min-w-0 flex-1">
          <p class="text-subheadline truncate font-medium md:text-sm">{current.title}</p>
          <p class="text-footnote text-muted-foreground truncate md:text-xs">
            {#if catalogPreview.state === 'error'}
              Could not play this {catalogPreview.source === 'full' ? 'song' : 'preview'}.
            {:else}
              {current.artist} · {catalogPreview.source === 'full' ? 'Full song' : '30 s preview'}
            {/if}
          </p>
        </div>
        {#if catalogPreview.source === 'clip'}
          {#if fullState.status === 'ready'}
            <Button
              variant="gray"
              class="h-11 shrink-0 rounded-full px-4 md:h-8"
              onclick={() => catalogPreview.playFull()}
            >
              Full song
            </Button>
          {:else if fullState.status === 'resolving'}
            <span class="text-footnote text-muted-foreground shrink-0 md:text-xs">
              Finding the full song…
            </span>
          {/if}
        {/if}
        <Button
          variant="ghost"
          class="text-muted-foreground size-11 shrink-0 rounded-full p-0"
          aria-label="Close preview"
          onclick={() => catalogPreview.stop()}
        >
          <X class="size-5" aria-hidden="true" />
        </Button>
      </div>
      {#if catalogPreview.source === 'clip' && fullState.status === 'failed'}
        <p class="text-footnote text-muted-foreground md:text-xs">{fullState.reason}</p>
      {/if}
      <progress
        class="accent-primary h-1 w-full rounded-full"
        value={progress}
        max="1"
        aria-label="Preview position"
      ></progress>
    </div>
  {/if}
</ScrollArea>
