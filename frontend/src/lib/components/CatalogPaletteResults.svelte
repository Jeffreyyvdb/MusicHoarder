<script lang="ts">
  import { CircleCheck, Pause, Play, Plus, RotateCw, TriangleAlert } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import * as Command from '#lib/components/ui/command/index.ts';
  import { Spinner } from '#lib/components/ui/spinner/index.ts';
  import Cover from '#lib/components/file-browser/Cover.svelte';
  import { importTrack, searchCatalogTracks, type CatalogTrack } from '#lib/api-client.ts';
  import { messageOf } from '#lib/artist-discography.ts';
  import { buildOwnedIndex, isInLibrary } from '#lib/catalog-match.ts';
  import { catalogPreview, previewAvailable } from '#lib/catalog-preview.svelte.ts';
  import { isRetry, wishlistLabel } from '#lib/catalog-status.ts';
  import { formatDuration } from '#lib/formatters.ts';
  import { songsStore } from '#lib/stores/songs.svelte.ts';
  import { cn } from '#lib/utils.ts';

  // The palette's "From the catalog" group: one search for the query it was asked about, then a
  // row per track. Choosing a row previews it; its Add button (or ⌘↵ on the row) queues the
  // download. Where the page cannot play audio (the app shell) choosing a row adds it.

  let {
    query,
    onhighlight
  }: {
    query: string;
    /** The row to highlight once the search answers: the row the palette highlighted is gone. */
    onhighlight: (value: string) => void;
  } = $props();

  const VALUE_PREFIX = 'catalog-track-';
  const canPreview = previewAvailable();

  type Search =
    | { status: 'loading' }
    | { status: 'ready'; tracks: CatalogTrack[] }
    | { status: 'error'; message: string };

  let search = $state.raw<Search>({ status: 'loading' });
  let attempt = $state(0);

  $effect(() => {
    const q = query;
    void attempt;
    const controller = new AbortController();
    search = { status: 'loading' };
    searchCatalogTracks(q, controller.signal).then(
      (tracks) => {
        search = { status: 'ready', tracks };
        if (tracks.length > 0) onhighlight(VALUE_PREFIX + tracks[0].deezerId);
      },
      (err: unknown) => {
        if (controller.signal.aborted) return;
        search = { status: 'error', message: messageOf(err, 'Search is unavailable right now.') };
        onhighlight('catalog-retry');
      }
    );
    return () => controller.abort();
  });

  const owned = $derived(buildOwnedIndex(songsStore.builtSongs));

  let adding = $state.raw<ReadonlySet<string>>(new Set());
  let queued = $state.raw<ReadonlySet<string>>(new Set());

  function canAdd(track: CatalogTrack): boolean {
    return (
      !adding.has(track.deezerId) &&
      !queued.has(track.deezerId) &&
      wishlistLabel(track.wishlistStatus) === null
    );
  }

  async function add(track: CatalogTrack) {
    if (!canAdd(track)) return;
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
      toast.success(`Added ${track.title}`, { description: 'It downloads in the background.' });
    } catch (err) {
      toast.error(messageOf(err, 'Could not add that song.'));
    } finally {
      adding = new Set([...adding].filter((id) => id !== track.deezerId));
    }
  }

  /** ⌘↵ from the palette: add the highlighted row. False when the value is not one of these rows. */
  export function addByValue(value: string): boolean {
    if (search.status !== 'ready' || !value.startsWith(VALUE_PREFIX)) return false;
    const track = search.tracks.find((t) => VALUE_PREFIX + t.deezerId === value);
    if (!track) return false;
    void add(track);
    return true;
  }

  function choose(track: CatalogTrack) {
    if (canPreview && track.hasPreview) {
      catalogPreview.toggle({ deezerId: track.deezerId, title: track.title, artist: track.artist });
    } else {
      void add(track);
    }
  }

  function previewIcon(id: string): 'play' | 'pause' | 'loading' | 'error' {
    if (catalogPreview.track?.deezerId !== id) return 'play';
    const state = catalogPreview.state;
    if (state === 'playing') return 'pause';
    if (state === 'loading') return 'loading';
    if (state === 'error') return 'error';
    return 'play';
  }

  const secondLine =
    'text-muted-foreground text-footnote truncate md:text-xs flex items-center gap-1 min-w-0';
</script>

<Command.Group heading="From the catalog">
  {#if search.status === 'loading'}
    <Command.Item value="catalog-loading" disabled>
      <Spinner class="size-4" />
      <span class="text-muted-foreground">Searching for “{query}”…</span>
    </Command.Item>
  {:else if search.status === 'error'}
    <Command.Item value="catalog-retry" onSelect={() => (attempt += 1)}>
      <TriangleAlert class="text-warning-text" />
      <span class="min-w-0 flex-1 truncate">{search.message}</span>
      <span class="text-muted-foreground text-footnote shrink-0 pl-3 md:text-xs">Try again</span>
    </Command.Item>
  {:else if search.tracks.length === 0}
    <Command.Item value="catalog-none" disabled>
      <span class="text-muted-foreground">Nothing in the catalog for “{query}”.</span>
    </Command.Item>
  {:else}
    <!-- Bounded by the server: a catalog search answers at most 25 tracks. -->
    {#each search.tracks as track (track.deezerId)}
      {@const label = queued.has(track.deezerId) ? 'Queued' : wishlistLabel(track.wishlistStatus)}
      {@const icon = previewIcon(track.deezerId)}
      {@const inLibrary = isInLibrary(owned, track)}
      <Command.Item value={VALUE_PREFIX + track.deezerId} onSelect={() => choose(track)}>
        <span class="relative shrink-0">
          <Cover
            artist={track.artist}
            title={track.title}
            coverUrl={track.coverUrl}
            size={40}
            corner={4}
            caption={false}
          />
          {#if canPreview && track.hasPreview}
            <!-- What choosing the row does, drawn on the cover: play, pause or loading. -->
            <span
              class="absolute inset-0 flex items-center justify-center rounded-[4px] bg-black/40 text-white"
              aria-hidden="true"
            >
              {#if icon === 'pause'}
                <Pause class="size-4" />
              {:else if icon === 'loading'}
                <Spinner class="size-4" />
              {:else if icon === 'error'}
                <RotateCw class="size-4" />
              {:else}
                <Play class="size-4" />
              {/if}
            </span>
          {/if}
        </span>
        <span class="flex min-w-0 flex-1 flex-col">
          <span
            class={cn(
              'flex min-w-0 items-center gap-1.5',
              catalogPreview.track?.deezerId === track.deezerId && 'text-primary'
            )}
          >
            <span class="truncate">{track.title}</span>
            {#if track.explicit}
              <span
                class="text-caption-2 text-muted-foreground border-border shrink-0 rounded-[3px] border px-1 leading-4"
                aria-label="Explicit">E</span
              >
            {/if}
          </span>
          <span class={secondLine}>
            <span class="truncate"
              >{track.artist}{track.album ? ` · ${track.album}` : ''} · {formatDuration(
                track.durationMs / 1000
              )}</span
            >
            {#if inLibrary}
              <span class="shrink-0">· In library</span>
            {/if}
          </span>
        </span>
        {#if adding.has(track.deezerId)}
          <span class="flex size-11 shrink-0 items-center justify-center md:size-8" role="status">
            <Spinner class="size-4" />
            <span class="sr-only">Adding {track.title}</span>
          </span>
        {:else if label}
          <span
            class="text-muted-foreground text-footnote flex shrink-0 items-center gap-1 pl-2 md:text-xs"
          >
            <CircleCheck class="text-primary size-4" aria-hidden="true" />
            {label}
          </span>
        {:else}
          <!-- A button inside the row, so a tap adds without previewing; the keyboard's way is ⌘↵. -->
          <button
            type="button"
            class={cn(
              'focus-visible:ring-ring hover:bg-accent flex size-11 shrink-0 items-center justify-center rounded-full focus-visible:ring-2 focus-visible:outline-hidden md:size-8',
              inLibrary ? 'text-muted-foreground' : 'text-primary'
            )}
            aria-label={`${isRetry(track.wishlistStatus) ? 'Retry adding' : 'Add'} ${track.title} by ${track.artist} to your library`}
            onpointerdown={(e) => e.stopPropagation()}
            onclick={(e) => {
              e.stopPropagation();
              void add(track);
            }}
          >
            <Plus class="size-5 md:size-4" aria-hidden="true" />
          </button>
        {/if}
      </Command.Item>
    {/each}
  {/if}
</Command.Group>
