<script lang="ts">
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import {
    ExternalLink,
    FolderDown,
    ListVideo,
    Loader2,
    Pencil,
    Play,
    Shuffle,
    Trash2
  } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import { EmptyState } from '$lib/components/ui/empty-state';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import PlaylistCover from '$lib/components/v2/PlaylistCover.svelte';
  import PlaylistNameSheet from '$lib/components/v2/PlaylistNameSheet.svelte';
  import TrackRowMenu, { activateTrack } from '$lib/components/v2/TrackRowMenu.svelte';
  import TrackRowText from '$lib/components/v2/TrackRowText.svelte';
  import { longpress, type LongPressPoint } from '$lib/actions/long-press';
  import { coverUrlForSong, toPlayerSong, type ApiSong } from '$lib/api-client';
  import { formatDuration, formatTotalDuration } from '$lib/formatters';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { missingLabel, playlistKindLabel, trackCountLabel, sourceLabel } from '$lib/playlists';
  import { playerStore } from '$lib/stores/player.svelte';
  import { playlistSongs, playlistsStore } from '$lib/stores/playlists.svelte';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { tabMemory } from '$lib/stores/tab-memory.svelte';
  import { artistOf, titleOf } from '$lib/track-list-view.svelte';
  import { cn, shuffle } from '$lib/utils';

  // A playlist's page (/playlists/[id]): the album page's shape — cover, name, Play / Shuffle — over
  // the songs in play order. A synced playlist lists its own tracks first, then "Added here"; only
  // the added ones can be removed here (the rest belong to the remote playlist).
  type Props = { id: number };
  const { id }: Props = $props();

  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);

  $effect(() => {
    playlistsStore.revalidate();
    songsStore.revalidate();
    songsStore.startLive();
    return () => songsStore.stopLive();
  });

  const playlist = $derived(playlistsStore.byId.get(id) ?? null);
  const songs = $derived(playlist ? playlistSongs(playlist, songsStore.songsById) : []);
  const added = $derived(new Set(playlist?.addedSongIds ?? []));
  // Where "Added here" starts in a synced playlist (songs after it were added in MusicHoarder).
  const firstAddedIndex = $derived(playlist?.source ? songs.findIndex((s) => added.has(s.id)) : -1);
  const totalSeconds = $derived(songs.reduce((sum, s) => sum + (s.durationSeconds ?? 0), 0));
  const heroMeta = $derived.by(() => {
    if (!playlist) return '';
    const parts = [songs.length === 0 ? 'Empty' : trackCountLabel(songs.length)];
    if (totalSeconds > 0) parts.push(formatTotalDuration(totalSeconds));
    return parts.join(' · ');
  });
  const missing = $derived(playlist ? missingLabel(playlist) : null);

  $effect(() => {
    if (playlist) tabMemory.setTitle(page.url, playlist.name);
  });
  const desktopBack = $derived(
    compact ? undefined : tabMemory.backTarget(page.url, page.data.user)
  );

  // Long playlists (a synced Liked Songs runs to thousands) render in pages as you scroll, rather
  // than mounting every row and its menu up front.
  const PAGE = 150;
  let shown = $state(PAGE);
  let sentinel = $state<HTMLElement | null>(null);
  $effect(() => {
    if (!sentinel) return;
    const observer = new IntersectionObserver(
      (entries) => {
        if (entries.some((e) => e.isIntersecting)) shown += PAGE;
      },
      { rootMargin: '600px' }
    );
    observer.observe(sentinel);
    return () => observer.disconnect();
  });
  const visible = $derived(songs.slice(0, shown));

  // ── playback ────────────────────────────────────────────────────────────────
  function fallbackArtist(s: ApiSong): string {
    return (s.albumArtist ?? s.artist ?? '').trim() || 'Unknown Artist';
  }
  /** Plays `list` from `from`. Never pauses: every caller is labelled Play or Shuffle, or is a tap. */
  function playQueue(list: ApiSong[], from = 0) {
    void playerStore.startQueue(
      list.map((s) => toPlayerSong(s, fallbackArtist(s))),
      from
    );
  }
  function activate(song: ApiSong, index: number) {
    if (compact) {
      activateTrack(song.id, () => playQueue(songs, index));
      return;
    }
    if (songDetail.isOpen && songDetail.target?.songId === song.id) songDetail.close();
    else songDetail.open(song.id);
  }

  type RowMenu = { openAt: (point: LongPressPoint) => void };
  const menus: Record<number, RowMenu | undefined> = {};

  // ── editing ─────────────────────────────────────────────────────────────────
  let renameOpen = $state(false);
  let deleteOpen = $state(false);
  let heroTitleEl = $state<HTMLElement | null>(null);

  async function removeSong(song: ApiSong) {
    if (!playlist) return;
    try {
      await playlistsStore.removeSong(playlist.id, song.id);
      toast.success(`Removed from ${playlist.name}`, { description: titleOf(song) });
    } catch (err) {
      toast.error('Could not remove the track', {
        description: err instanceof Error ? err.message : undefined
      });
    }
  }

  async function rename(name: string) {
    if (!playlist) return;
    await playlistsStore.rename(playlist.id, name);
  }

  async function setExport(on: boolean) {
    if (!playlist) return;
    try {
      await playlistsStore.setExport(playlist.id, on);
      toast.success(on ? 'Exporting to your library' : 'No longer exported', {
        description: on
          ? 'Written as an .m3u8 in the library’s Playlists folder within a minute, for Navidrome, Plex and Jellyfin.'
          : 'Its .m3u8 was removed from the library’s Playlists folder.'
      });
    } catch (err) {
      toast.error('Could not change the export', {
        description: err instanceof Error ? err.message : undefined
      });
    }
  }

  async function deleteIt() {
    if (!playlist) return;
    const name = playlist.name;
    try {
      await playlistsStore.remove(playlist.id);
      toast.success(`Deleted ${name}`);
      // Back to where the playlist was opened from (normally the list), not forward onto it.
      void tabMemory.goBack(
        tabMemory.backTarget(page.url, page.data.user) ?? { label: 'Playlists', href: '/playlists' }
      );
    } catch (err) {
      toast.error('Could not delete the playlist', {
        description: err instanceof Error ? err.message : undefined
      });
    }
  }
</script>

{#snippet playButtons()}
  {#if compact}
    <div class="flex w-full gap-3">
      <Button
        variant="gray"
        size="pill"
        class="text-primary flex-1"
        onclick={() => playQueue(songs)}
      >
        <Play fill="currentColor" /> Play
      </Button>
      <Button
        variant="gray"
        size="pill"
        class="text-primary flex-1"
        onclick={() => playQueue(shuffle(songs))}
      >
        <Shuffle /> Shuffle
      </Button>
    </div>
  {:else}
    <div class="flex shrink-0 gap-2">
      <Button size="sm" class="h-8 gap-1.5 rounded-full px-3" onclick={() => playQueue(songs)}>
        <Play class="size-4" fill="currentColor" /> Play
      </Button>
      <Button
        variant="gray"
        size="sm"
        class="h-8 gap-1.5 rounded-full px-3"
        onclick={() => playQueue(shuffle(songs))}
      >
        <Shuffle class="size-4" /> Shuffle
      </Button>
    </div>
  {/if}
{/snippet}

{#snippet moreItems()}
  {#if playlist}
    {#if !playlist.source}
      <DropdownMenu.Item onSelect={() => (renameOpen = true)}>
        <Pencil /> Rename…
      </DropdownMenu.Item>
    {/if}
    {#if playlist.source?.url}
      <DropdownMenu.Item
        onSelect={() => window.open(playlist.source?.url ?? '', '_blank', 'noopener')}
      >
        <ExternalLink /> Open in {sourceLabel(playlist.source.type)}
      </DropdownMenu.Item>
    {/if}
    {#if playlistsStore.canExport}
      <DropdownMenu.CheckboxItem
        checked={playlist.exportToLibrary}
        onCheckedChange={(on) => void setExport(on)}
      >
        <FolderDown /> Export to library
      </DropdownMenu.CheckboxItem>
    {/if}
    {#if !playlist.source}
      <DropdownMenu.Separator />
      <DropdownMenu.Item variant="destructive" onSelect={() => (deleteOpen = true)}>
        <Trash2 /> Delete playlist…
      </DropdownMenu.Item>
    {/if}
  {/if}
{/snippet}

{#snippet songRow(song: ApiSong, index: number)}
  {@const isLoaded = playerStore.currentSong?.id === song.id}
  <li
    use:longpress={{ onlongpress: (p) => menus[song.id]?.openAt(p) }}
    oncontextmenu={(e) => {
      e.preventDefault();
      menus[song.id]?.openAt({ x: e.clientX, y: e.clientY });
    }}
    class={cn(
      'group has-[[data-row-main]:active]:bg-accent md:hover:bg-accent relative flex min-h-16 items-center pr-1 transition-colors duration-100 md:min-h-14 md:rounded-lg',
      index < songs.length - 1 &&
        index + 1 !== firstAddedIndex &&
        "after:bg-separator after:absolute after:right-0 after:bottom-0 after:left-[76px] after:h-(--hairline) after:content-[''] md:after:left-[68px]"
    )}
  >
    <button
      type="button"
      data-row-main=""
      onclick={() => activate(song, index)}
      aria-current={isLoaded ? 'true' : undefined}
      class="focus-visible:ring-ring flex min-h-16 min-w-0 flex-1 items-center gap-3 pl-4 text-left outline-none focus-visible:ring-2 focus-visible:ring-inset md:min-h-14 md:pl-2"
    >
      <span class="relative shrink-0">
        <Cover
          artist={fallbackArtist(song)}
          title={song.album ?? titleOf(song)}
          coverUrl={coverUrlForSong(song)}
          size={48}
          corner={6}
          caption={false}
          dprCap={3}
          class="md:size-10!"
        />
        {#if isLoaded}
          <span
            class="absolute inset-0 grid place-items-center rounded-[6px] bg-black/45 text-white"
            aria-hidden="true"
          >
            <span class={cn('mh-eq', playerStore.isPlaying && 'is-playing')}>
              <i></i><i></i><i></i>
            </span>
          </span>
        {/if}
      </span>
      <TrackRowText
        {song}
        loaded={isLoaded}
        opensPlayer={isLoaded && compact}
        secondary={`${artistOf(song)}${song.album ? ` · ${song.album}` : ''}`}
      />
      <span class="text-muted-foreground hidden text-xs tabular-nums md:inline">
        {formatDuration(song.durationSeconds)}
      </span>
    </button>
    <TrackRowMenu
      bind:this={() => menus[song.id], (m) => (menus[song.id] = m)}
      {song}
      onplay={() => playQueue(songs, index)}
      onremove={added.has(song.id) ? () => void removeSong(song) : undefined}
    />
  </li>
{/snippet}

{#if !playlist}
  <ScrollArea class="min-h-0 flex-1">
    <PageToolbarV2 title="Playlist" back={desktopBack} />
    {#if playlistsStore.isLoading || !playlistsStore.hasLoaded}
      <p
        class="text-subheadline text-muted-foreground flex items-center justify-center gap-2 py-16"
      >
        <Loader2 class="size-4 animate-spin" /> Loading playlist…
      </p>
    {:else if playlistsStore.error}
      <div class="flex flex-col items-center gap-3 px-6 py-16 text-center">
        <p class="text-destructive-text text-subheadline md:text-sm">{playlistsStore.error}</p>
        <Button variant="gray" class="rounded-full" onclick={() => void playlistsStore.load()}
          >Retry</Button
        >
      </div>
    {:else}
      <EmptyState
        icon={ListVideo}
        title="Playlist not found"
        hint="It may have been deleted, or it belongs to another account."
        action={{ label: 'All playlists', onclick: () => void goto('/playlists') }}
      />
    {/if}
  </ScrollArea>
{:else}
  <ScrollArea class="min-h-0 flex-1">
    <!-- No large title: the hero carries the name, and the bar's inline title fades in once it
         scrolls under the bar (the album page's pattern). -->
    <PageToolbarV2
      title={playlist.name}
      largeTitle={false}
      collapseAfter={heroTitleEl}
      back={desktopBack}
      more={moreItems}
    />

    <div
      class="flex flex-col items-center px-4 pt-2 text-center md:flex-row md:items-end md:gap-7 md:px-7 md:text-left"
    >
      <div class="w-60 shrink-0 md:w-[232px]">
        <PlaylistCover
          {playlist}
          size={240}
          corner={10}
          dprCap={3}
          class="aspect-square !h-auto !w-full !shadow-[0_12px_32px_rgb(0_0_0/0.28)]"
        />
      </div>
      <div
        class="mt-4 flex w-full min-w-0 flex-1 flex-col items-center gap-0.5 md:mt-0 md:w-auto md:items-start"
      >
        <h2
          bind:this={heroTitleEl}
          class="text-title-2 max-w-full text-balance md:text-[28px] md:leading-tight"
        >
          {playlist.name}
        </h2>
        {#if playlist.source?.url}
          <a
            href={playlist.source.url}
            target="_blank"
            rel="noopener noreferrer"
            class="text-title-3 text-primary relative flex max-w-full font-normal after:absolute after:inset-x-0 after:-inset-y-2.5 after:content-[''] hover:underline md:text-lg"
          >
            <span class="block truncate">{playlistKindLabel(playlist)}</span>
          </a>
        {:else}
          <p class="text-title-3 text-muted-foreground font-normal md:text-lg">
            {playlistKindLabel(playlist)}
          </p>
        {/if}
        <p class="text-footnote text-muted-foreground mt-1">{heroMeta}</p>
        {#if missing}
          <p class="text-footnote text-muted-foreground">{missing}</p>
        {/if}
        {#if songs.length > 0}
          <div class="mt-4 w-full md:w-auto">
            {@render playButtons()}
          </div>
        {/if}
      </div>
    </div>

    <div class="pt-4 pb-6 md:px-5">
      {#if songs.length === 0}
        {#if songsStore.isLoading && songsStore.songs.length === 0}
          <p
            class="text-subheadline text-muted-foreground flex items-center justify-center gap-2 py-12"
          >
            <Loader2 class="size-4 animate-spin" /> Loading tracks…
          </p>
        {:else if playlist.source}
          <EmptyState
            icon={ListVideo}
            title="Nothing here yet"
            hint={playlist.missingCount > 0
              ? 'None of its tracks are in your library yet. They play here as the wishlist brings them in.'
              : 'Tracks you add from any track’s ⋯ menu play here too.'}
          />
        {:else}
          <EmptyState
            icon={ListVideo}
            title="This playlist is empty"
            hint="Pick Add to playlist… from any track’s ⋯ menu, in Tracks, an album or Now Playing."
          />
        {/if}
      {:else}
        <ul aria-label="Tracks">
          {#each visible as song, index (song.id)}
            {#if index === firstAddedIndex && index > 0}
              <li class="px-4 pt-5 pb-1 md:px-2" role="presentation">
                <h3
                  class="text-footnote text-muted-foreground font-semibold tracking-wide uppercase md:text-xs"
                >
                  Added here
                </h3>
              </li>
            {/if}
            {@render songRow(song, index)}
          {/each}
        </ul>
        {#if visible.length < songs.length}
          <div bind:this={sentinel} class="h-px" aria-hidden="true"></div>
        {/if}
      {/if}
    </div>
  </ScrollArea>

  <PlaylistNameSheet
    bind:open={renameOpen}
    title="Rename Playlist"
    actionLabel="Save"
    initialName={playlist.name}
    onsubmit={rename}
  />

  <AlertDialog.Root bind:open={deleteOpen}>
    <AlertDialog.Content>
      <AlertDialog.Header>
        <AlertDialog.Title>Delete “{playlist.name}”?</AlertDialog.Title>
        <AlertDialog.Description>
          The playlist goes, and its file in your library’s Playlists folder with it. The tracks
          stay in your library.
        </AlertDialog.Description>
      </AlertDialog.Header>
      <AlertDialog.Footer>
        <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
        <AlertDialog.Action variant="destructive" onclick={() => void deleteIt()}>
          Delete playlist
        </AlertDialog.Action>
      </AlertDialog.Footer>
    </AlertDialog.Content>
  </AlertDialog.Root>
{/if}
