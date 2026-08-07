<script lang="ts">
  import { untrack } from 'svelte';
  import { goto } from '$app/navigation';
  import { Disc3, Mic2, Music, Library, Loader2 } from '@lucide/svelte';
  import * as Command from '$lib/components/ui/command';
  import { NAV_GROUPS } from '$lib/nav';
  import {
    buildAlbumsFromSongs,
    mergeAlbumsByName,
    buildArtistGroups,
    type ApiSong,
    type AlbumSummary,
    type GroupSummary
  } from '$lib/api-client';
  import { isBuiltSong } from '$lib/album-sections';
  import { commandPalette } from '$lib/stores/command-palette.svelte';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';

  // Cap each result group so typing stays snappy on large libraries.
  const MAX_PER_GROUP = 8;

  type NavCommand = {
    label: string;
    href: string;
    icon: typeof Library;
    keywords: string;
    group: string;
  };

  // Every nav destination, flattened from the shared groups so the Jump-to list reads exactly
  // like the sidebar and can never miss a route — the hand-kept list this replaced had no
  // entry for Overview, Liked songs, Wishlist, Playlist sync, Stats or History. The group
  // name joins the haystack, so typing "manage" surfaces everything under Manage.
  const NAV_COMMANDS: NavCommand[] = NAV_GROUPS.flatMap((group) =>
    group.items.map((item) => ({
      label: item.label,
      href: item.href,
      icon: item.icon,
      keywords: `${group.label} ${item.keywords ?? ''}`.toLowerCase(),
      group: group.label
    }))
  );

  // bits-ui's Dialog binds cleanly to a plain local $state; the shared store
  // (driven by the global shortcut + header badge) is mirrored into it.
  let dialogOpen = $state(false);
  $effect(() => {
    dialogOpen = commandPalette.open;
  });

  function handleOpenChange(value: boolean) {
    dialogOpen = value;
    commandPalette.setOpen(value);
  }

  let query = $state('');

  // The palette reads the shared songs store rather than fetching its own copy:
  // one full-library download per session, already warm on any page that has
  // shown the library, and the exact rows the detail panel resolves against —
  // so picking a track can open it with no further request.
  const songs = $derived(songsStore.songs);
  const loading = $derived(songsStore.isLoading && songs.length === 0);

  // Warm the store the first time the palette opens (a no-op once loaded).
  // untrack: ensureLoaded reads the same isLoading flag the fetch writes, and a
  // tracked read here would re-fire this effect on its own write.
  $effect(() => {
    if (commandPalette.open) untrack(() => songsStore.ensureLoaded());
  });

  // Only built songs can be opened/browsed from here, so scope every index to
  // them once instead of filtering per keystroke. Everything below is `$derived`
  // and therefore lazy — none of it runs while the dialog is closed.
  const builtSongs = $derived(songs.filter(isBuiltSong));

  // Merged by name so searching an album split across destination folders offers one result.
  // Built from the same set as the Library page, so the `?album=` keys line up.
  const albums = $derived<AlbumSummary[]>(mergeAlbumsByName(buildAlbumsFromSongs(builtSongs)));
  const artists = $derived<GroupSummary[]>(buildArtistGroups(builtSongs));

  // Per-entity lowercase haystacks, rebuilt only when the dataset changes. Without
  // these every keystroke re-lowercased three fields per song across the library.
  type Indexed<T> = { value: T; haystack: string };

  const trackIndex = $derived<Indexed<ApiSong>[]>(
    builtSongs.map((s) => ({
      value: s,
      haystack: [s.title ?? s.fileName, s.artist ?? s.albumArtist ?? '', s.album ?? '']
        .join(' ')
        .toLowerCase()
    }))
  );
  const albumIndex = $derived<Indexed<AlbumSummary>[]>(
    albums.map((a) => ({ value: a, haystack: [a.title, a.artist].join(' ').toLowerCase() }))
  );
  const artistIndex = $derived<Indexed<GroupSummary>[]>(
    artists.map((a) => ({ value: a, haystack: a.label.toLowerCase() }))
  );

  const q = $derived(query.trim().toLowerCase());
  const hasQuery = $derived(q.length > 0);

  const navMatches = $derived(
    NAV_COMMANDS.filter(
      (c) => !hasQuery || c.label.toLowerCase().includes(q) || c.keywords.includes(q)
    )
  );

  /** First `MAX_PER_GROUP` hits, stopping early — the groups are capped anyway. */
  function topMatches<T>(index: Indexed<T>[], needle: string): T[] {
    const out: T[] = [];
    for (const entry of index) {
      if (!entry.haystack.includes(needle)) continue;
      out.push(entry.value);
      if (out.length >= MAX_PER_GROUP) break;
    }
    return out;
  }

  const libraryArtists = $derived(hasQuery ? topMatches(artistIndex, q) : []);
  const libraryAlbums = $derived(hasQuery ? topMatches(albumIndex, q) : []);
  const libraryTracks = $derived(hasQuery ? topMatches(trackIndex, q) : []);

  const hasLibraryResults = $derived(
    libraryArtists.length > 0 || libraryAlbums.length > 0 || libraryTracks.length > 0
  );

  function trackArtist(s: ApiSong): string {
    return s.artist ?? s.albumArtist ?? 'Unknown Artist';
  }

  function dismiss() {
    commandPalette.setOpen(false);
    query = '';
  }

  function navigate(href: string) {
    dismiss();
    void goto(href);
  }

  // Tracks open the global song-detail overlay in place. It's mounted in the app
  // shell alongside this palette, and it resolves against the same rows we just
  // searched, so it paints immediately — no route change, no album drilldown, and
  // none of the requests either of those fan out.
  function openTrack(song: ApiSong) {
    dismiss();
    songDetail.open(song.id);
  }
</script>

<Command.Dialog
  bind:open={dialogOpen}
  onOpenChange={handleOpenChange}
  shouldFilter={false}
  title="Search everywhere"
  description="Search tracks, albums, artists, and jump to any page."
  class="sm:max-w-2xl"
>
  <Command.Input bind:value={query} placeholder="Search tracks, albums, artists, pages…" />
  <Command.List class="max-h-[60vh]">
    <!-- The page commands are local, so they stay usable while the library
         dataset is still in flight — only the library groups wait. -->
    {#if hasQuery && !hasLibraryResults && navMatches.length === 0 && !loading}
      <Command.Empty>No results for “{query}”.</Command.Empty>
    {/if}

    {#if navMatches.length > 0}
      <Command.Group heading={hasQuery ? 'Pages' : 'Jump to'}>
        {#each navMatches as cmd (cmd.href)}
          <Command.Item value={`nav-${cmd.href}`} onSelect={() => navigate(cmd.href)}>
            <cmd.icon class="text-muted-foreground" />
            <span>{cmd.label}</span>
            <!-- The group disambiguates the two "Artists" and the two "Albums" — one of each
                 is a library view, the other an Inbox review queue. -->
            <span class="text-muted-foreground ml-auto text-xs">{cmd.group}</span>
          </Command.Item>
        {/each}
      </Command.Group>
    {/if}

    {#if loading}
      <div class="text-muted-foreground flex items-center gap-2 px-3 py-6 text-sm">
        <Loader2 class="size-4 animate-spin" />
        Loading library…
      </div>
    {/if}

    {#if libraryArtists.length > 0}
      <Command.Group heading="Artists">
        {#each libraryArtists as artist (artist.key)}
          <Command.Item
            value={`lib-artist-${artist.key}`}
            onSelect={() => navigate(`/library?artist=${encodeURIComponent(artist.key)}`)}
          >
            <Mic2 class="text-muted-foreground" />
            <span class="min-w-0 flex-1 truncate">{artist.label}</span>
            <span class="text-muted-foreground shrink-0 pl-3 text-xs">
              {artist.trackCount} {artist.trackCount === 1 ? 'track' : 'tracks'}
            </span>
          </Command.Item>
        {/each}
      </Command.Group>
    {/if}

    {#if libraryAlbums.length > 0}
      <Command.Group heading="Albums">
        {#each libraryAlbums as album (album.key)}
          <Command.Item
            value={`lib-album-${album.key}`}
            onSelect={() => navigate(`/library?album=${encodeURIComponent(album.key)}`)}
          >
            <Disc3 class="text-muted-foreground" />
            <span class="min-w-0 flex-1 truncate">{album.title}</span>
            <span class="text-muted-foreground min-w-0 truncate pl-3 text-right text-xs"
              >{album.artist}</span
            >
          </Command.Item>
        {/each}
      </Command.Group>
    {/if}

    {#if libraryTracks.length > 0}
      <Command.Group heading="Tracks">
        {#each libraryTracks as track (track.id)}
          <Command.Item value={`lib-track-${track.id}`} onSelect={() => openTrack(track)}>
            <Music class="text-muted-foreground" />
            <span class="min-w-0 flex-1 truncate">{track.title ?? track.fileName}</span>
            <span class="text-muted-foreground min-w-0 truncate pl-3 text-right text-xs">
              {trackArtist(track)}{track.album ? ` · ${track.album}` : ''}
            </span>
          </Command.Item>
        {/each}
      </Command.Group>
    {/if}
  </Command.List>
</Command.Dialog>
