<script lang="ts">
  import { goto } from '$app/navigation';
  import { ListVideo, Loader2, Plus, Search } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import { EmptyState } from '$lib/components/ui/empty-state';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { SearchField } from '$lib/components/ui/search-field';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import PlaylistCover from '$lib/components/v2/PlaylistCover.svelte';
  import PlaylistNameSheet from '$lib/components/v2/PlaylistNameSheet.svelte';
  import type { LibraryPlaylist } from '$lib/api-client';
  import { playlistSubtitle } from '$lib/playlists';
  import { playlistsStore } from '$lib/stores/playlists.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';

  // Listen → Playlists: the ones made here first, then the ones that follow a playlist collected
  // from Spotify, Deezer or YouTube (under Add). Cards like the Albums grid; a card opens the
  // playlist's page. The library is loaded too, for the covers (a playlist is song ids).
  $effect(() => {
    playlistsStore.revalidate();
    songsStore.revalidate();
  });

  let query = $state('');
  let newOpen = $state(false);

  const matches = $derived.by(() => {
    const q = query.trim().toLocaleLowerCase();
    const all = playlistsStore.playlists;
    return q ? all.filter((p) => p.name.toLocaleLowerCase().includes(q)) : all;
  });
  const own = $derived(matches.filter((p) => !p.source));
  const synced = $derived(matches.filter((p) => p.source));
  const total = $derived(playlistsStore.playlists.length);
  const meta = $derived(
    !playlistsStore.hasLoaded
      ? undefined
      : query.trim()
        ? `${matches.length} of ${total}`
        : `${total} ${total === 1 ? 'playlist' : 'playlists'}`
  );

  async function createPlaylist(name: string) {
    const playlist = await playlistsStore.create(name);
    void goto(`/playlists/${playlist.id}`);
  }
</script>

{#snippet searchField()}
  <SearchField bind:value={query} label="Search playlists" />
{/snippet}

{#snippet grid(list: LibraryPlaylist[])}
  <div
    class="grid grid-cols-2 gap-x-4 gap-y-5 md:grid-cols-4 md:gap-x-5 md:gap-y-6 lg:grid-cols-5 xl:grid-cols-6"
  >
    {#each list as playlist (playlist.id)}
      <a
        href="/playlists/{playlist.id}"
        class="group focus-visible:ring-ring rounded-lg p-1 transition-transform outline-none hover:-translate-y-0.5 focus-visible:ring-2 active:scale-[0.97]"
        aria-label="Open playlist {playlist.name}"
      >
        <div class="flex flex-col gap-2">
          <PlaylistCover
            {playlist}
            class="aspect-square !h-auto !w-full shadow-[0_2px_10px_rgba(0,0,0,0.12)] dark:shadow-[0_4px_14px_rgba(0,0,0,0.5)]"
          />
          <div class="min-w-0 px-0.5">
            <p
              class="text-subheadline truncate font-semibold md:text-[12.5px] md:leading-snug md:font-medium"
            >
              {playlist.name}
            </p>
            <p class="text-footnote text-muted-foreground truncate md:text-[11.5px]">
              {playlistSubtitle(playlist)}
            </p>
          </div>
        </div>
      </a>
    {/each}
  </div>
{/snippet}

<ScrollArea class="min-h-0 flex-1">
  <PageToolbarV2
    title="Playlists"
    {meta}
    metaFrom="lg"
    search={total > 0 ? searchField : undefined}
  >
    {#snippet actions()}
      <Button
        variant="ghost"
        size="icon"
        aria-label="New playlist"
        title="New playlist"
        onclick={() => (newOpen = true)}
      >
        <Plus />
      </Button>
    {/snippet}
  </PageToolbarV2>

  <div class="flex flex-col gap-7 px-3 pt-2 pb-6 md:px-6">
    {#if playlistsStore.isLoading && !playlistsStore.hasLoaded}
      <p
        class="text-subheadline text-muted-foreground flex items-center justify-center gap-2 py-16"
      >
        <Loader2 class="size-4 animate-spin" /> Loading playlists…
      </p>
    {:else if playlistsStore.error && !playlistsStore.hasLoaded}
      <div class="flex flex-col items-center gap-3 px-6 py-16 text-center">
        <p class="text-destructive-text text-subheadline md:text-sm">{playlistsStore.error}</p>
        <Button variant="gray" class="rounded-full" onclick={() => void playlistsStore.load()}
          >Retry</Button
        >
      </div>
    {:else if total === 0}
      <EmptyState
        icon={ListVideo}
        title="No playlists yet"
        hint="Make one here, or pick Add to playlist… from any track’s ⋯ menu. Spotify, Deezer and YouTube playlists you collect under Add show up here too."
        action={{ label: 'New Playlist', onclick: () => (newOpen = true) }}
      />
    {:else if matches.length === 0}
      <EmptyState
        icon={Search}
        title={`No results for “${query.trim()}”`}
        action={{ label: 'Clear search', onclick: () => (query = '') }}
      />
    {:else}
      {#if own.length > 0}
        <section aria-labelledby={synced.length > 0 ? 'playlists-own' : undefined}>
          {#if synced.length > 0}
            <h2 id="playlists-own" class="text-title-2 mb-2 px-1 md:text-lg">Made here</h2>
          {/if}
          {@render grid(own)}
        </section>
      {/if}
      {#if synced.length > 0}
        <section aria-labelledby="playlists-synced">
          <h2 id="playlists-synced" class="text-title-2 mb-0.5 px-1 md:text-lg">Synced</h2>
          <p class="text-footnote text-muted-foreground mb-2 px-1 md:text-xs">
            Collected from Spotify, Deezer and YouTube. Tracks you add here play after their own.
          </p>
          {@render grid(synced)}
        </section>
      {/if}
    {/if}
  </div>
</ScrollArea>

<PlaylistNameSheet
  bind:open={newOpen}
  title="New Playlist"
  actionLabel="Create"
  onsubmit={createPlaylist}
/>
