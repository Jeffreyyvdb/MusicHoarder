<script lang="ts">
  import { CircleCheck, Pause, Play, RotateCw, TriangleAlert, X } from '@lucide/svelte';
  import * as Command from '#lib/components/ui/command/index.ts';
  import { Spinner } from '#lib/components/ui/spinner/index.ts';
  import Cover from '#lib/components/file-browser/Cover.svelte';
  import { searchCatalogTracks, type CatalogTrack } from '#lib/api-client.ts';
  import { messageOf } from '#lib/artist-discography.ts';
  import { catalogAdds } from '#lib/catalog-adds.svelte.ts';
  import { buildOwnedIndex, isInLibrary } from '#lib/catalog-match.ts';
  import { catalogPreview, previewAvailable } from '#lib/catalog-preview.svelte.ts';
  import { formatDuration } from '#lib/formatters.ts';
  import { songsStore } from '#lib/stores/songs.svelte.ts';
  import { cn } from '#lib/utils.ts';

  // The palette's "From the catalog" group: one search for the query it was asked about, then a
  // row per track. Choosing a row previews it, and the preview bar under the list adds it; ⌘↵ on a
  // row adds it at once. A row holds no button of its own: an option's content is flattened for a
  // screen reader, and the list takes Enter from anything inside it. Where the page cannot play
  // audio (the app shell), or the track has no clip, choosing a row adds it.

  let {
    query,
    onhighlight,
    onclear
  }: {
    query: string;
    /** The row to highlight once the search answers: the row the palette highlighted is gone. */
    onhighlight: (value: string) => void;
    /** Empties the palette's field, offered when the catalog has nothing for the query. */
    onclear: () => void;
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
        if (controller.signal.aborted) return;
        search = { status: 'ready', tracks };
        catalogAdds.remember(tracks);
        onhighlight(tracks.length > 0 ? VALUE_PREFIX + tracks[0].deezerId : 'catalog-clear');
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

  /** ⌘↵ from the palette: add the highlighted row. False when the value is not one of these rows. */
  export function addByValue(value: string): boolean {
    if (search.status !== 'ready' || !value.startsWith(VALUE_PREFIX)) return false;
    const track = search.tracks.find((t) => VALUE_PREFIX + t.deezerId === value);
    if (!track) return false;
    void catalogAdds.add(track);
    return true;
  }

  function choose(track: CatalogTrack) {
    if (canPreview && track.hasPreview) {
      catalogPreview.toggle({ deezerId: track.deezerId, title: track.title, artist: track.artist });
    } else {
      void catalogAdds.add(track);
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
    <Command.Item value="catalog-clear" onSelect={onclear}>
      <X class="text-muted-foreground" />
      <span class="min-w-0 flex-1 truncate">Clear search</span>
    </Command.Item>
  {:else}
    <!-- Bounded by the server: a catalog search answers at most 25 tracks. -->
    {#each search.tracks as track (track.deezerId)}
      {@const label = catalogAdds.labelOf(track)}
      {@const previews = canPreview && track.hasPreview}
      {@const icon = previewIcon(track.deezerId)}
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
          {#if previews}
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
            {#if isInLibrary(owned, track)}
              <span class="shrink-0">· In library</span>
            {/if}
          </span>
        </span>
        {#if catalogAdds.isAdding(track.deezerId)}
          <span class="flex shrink-0 items-center pl-2" role="status">
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
        {:else if !previews}
          <span class="text-primary text-footnote shrink-0 pl-2 md:text-xs">Add</span>
        {/if}
      </Command.Item>
    {/each}
  {/if}
</Command.Group>
