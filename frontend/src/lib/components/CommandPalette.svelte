<script lang="ts">
  import { goto } from '$app/navigation';
  import {
    Disc,
    Disc3,
    Mic2,
    Music,
    Music2,
    Library,
    Users,
    Inbox,
    Workflow,
    Gauge,
    ListMusic,
    Settings as SettingsIcon,
    FolderTree,
    TrendingUp,
    Loader2
  } from '@lucide/svelte';
  import * as Command from '$lib/components/ui/command';
  import {
    fetchSongs,
    buildAlbumsFromSongs,
    buildArtistGroups,
    artistLabelForSong,
    type ApiSong,
    type AlbumSummary,
    type GroupSummary
  } from '$lib/api-client';
  import { isBuiltSong } from '$lib/album-sections';
  import { commandPalette } from '$lib/stores/command-palette.svelte';

  // Cap each result group so typing stays snappy on large libraries.
  const MAX_PER_GROUP = 8;

  type NavCommand = { label: string; href: string; icon: typeof Library; keywords: string };

  // Order + labels mirror the sidebar / section tab bars (section-nav.ts +
  // library-subnav.ts) so the flat Jump-to list reads exactly like the navs.
  const NAV_COMMANDS: NavCommand[] = [
    { label: 'Pipeline', href: '/pipeline', icon: Workflow, keywords: 'conveyor runs jobs history ingest overview dashboard' },
    { label: 'By folder', href: '/directories', icon: FolderTree, keywords: 'directories folders tree match' },
    { label: 'AI quality', href: '/quality', icon: Gauge, keywords: 'grade bitrate' },
    { label: 'Album matches', href: '/album-quality', icon: Disc, keywords: 'album quality matches reconcile tracklist' },
    { label: 'Performance over time', href: '/performance', icon: TrendingUp, keywords: 'timeline regression version stats trends' },
    { label: 'Inbox', href: '/inbox', icon: Inbox, keywords: 'review duplicates ai flagged provenance manual' },
    { label: 'Library', href: '/library', icon: Library, keywords: 'albums home' },
    { label: 'Artists', href: '/artists', icon: Users, keywords: 'performers' },
    { label: 'All tracks', href: '/tracks', icon: ListMusic, keywords: 'songs' },
    { label: 'Spotify', href: '/spotify', icon: Music2, keywords: 'playlists liked' },
    { label: 'Settings', href: '/settings', icon: SettingsIcon, keywords: 'config preferences' }
  ];

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
  let songs = $state<ApiSong[]>([]);
  let loaded = $state(false);
  let loading = $state(false);
  // Non-reactive guard so a failed fetch doesn't retry-loop while the palette
  // stays open; it resets when the palette closes so the next open retries.
  let attemptedThisOpen = false;

  // Lazy-load the full song list the first time the palette opens, then cache it
  // for the rest of the session. Mirrors how the browse pages call fetchSongs().
  $effect(() => {
    if (!commandPalette.open) {
      attemptedThisOpen = false;
      return;
    }
    if (loaded || loading || attemptedThisOpen) return;
    attemptedThisOpen = true;
    loading = true;
    void fetchSongs()
      .then((loadedSongs) => {
        songs = loadedSongs;
        loaded = true;
      })
      .catch(() => {
        // Leave loaded=false so the next open retries.
      })
      .finally(() => {
        loading = false;
      });
  });

  const albums = $derived<AlbumSummary[]>(loaded ? buildAlbumsFromSongs(songs) : []);
  const artists = $derived<GroupSummary[]>(loaded ? buildArtistGroups(songs) : []);

  const q = $derived(query.trim().toLowerCase());
  const hasQuery = $derived(q.length > 0);

  const navMatches = $derived(
    NAV_COMMANDS.filter(
      (c) => !hasQuery || c.label.toLowerCase().includes(q) || c.keywords.includes(q)
    )
  );

  // Artists whose tracks reached the destination library. An artist counts as
  // "library" if any of its tracks is built; everything else is source-only.
  const builtArtistKeys = $derived(
    new Set(songs.filter(isBuiltSong).map(artistLabelForSong))
  );

  const artistMatches = $derived(
    hasQuery ? artists.filter((a) => a.label.toLowerCase().includes(q)) : []
  );
  const libraryArtists = $derived(
    artistMatches.filter((a) => builtArtistKeys.has(a.key)).slice(0, MAX_PER_GROUP)
  );

  const albumMatches = $derived(
    hasQuery
      ? albums.filter((a) => a.title.toLowerCase().includes(q) || a.artist.toLowerCase().includes(q))
      : []
  );
  const libraryAlbums = $derived(
    albumMatches.filter((a) => a.songs.some(isBuiltSong)).slice(0, MAX_PER_GROUP)
  );

  const trackMatches = $derived.by(() => {
    if (!hasQuery) return [] as ApiSong[];
    return songs.filter((s) => {
      const title = (s.title ?? s.fileName).toLowerCase();
      const artist = (s.artist ?? s.albumArtist ?? '').toLowerCase();
      const album = (s.album ?? '').toLowerCase();
      return title.includes(q) || artist.includes(q) || album.includes(q);
    });
  });
  const libraryTracks = $derived(trackMatches.filter(isBuiltSong).slice(0, MAX_PER_GROUP));

  const hasLibraryResults = $derived(
    libraryArtists.length > 0 || libraryAlbums.length > 0 || libraryTracks.length > 0
  );

  function trackArtist(s: ApiSong): string {
    return s.artist ?? s.albumArtist ?? 'Unknown Artist';
  }

  function navigate(href: string) {
    commandPalette.setOpen(false);
    query = '';
    void goto(href);
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
    {#if loading && !loaded}
      <div class="text-muted-foreground flex items-center gap-2 px-3 py-6 text-sm">
        <Loader2 class="size-4 animate-spin" />
        Loading library…
      </div>
    {:else}
      {#if hasQuery && !hasLibraryResults && navMatches.length === 0}
        <Command.Empty>No results for “{query}”.</Command.Empty>
      {/if}

      {#if navMatches.length > 0}
        <Command.Group heading={hasQuery ? 'Pages' : 'Jump to'}>
          {#each navMatches as cmd (cmd.href)}
            <Command.Item value={`nav-${cmd.href}`} onSelect={() => navigate(cmd.href)}>
              <cmd.icon class="text-muted-foreground" />
              <span>{cmd.label}</span>
            </Command.Item>
          {/each}
        </Command.Group>
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
              <span class="text-muted-foreground min-w-0 truncate pl-3 text-right text-xs">{album.artist}</span>
            </Command.Item>
          {/each}
        </Command.Group>
      {/if}

      {#if libraryTracks.length > 0}
        <Command.Group heading="Tracks">
          {#each libraryTracks as track (track.id)}
            <Command.Item
              value={`lib-track-${track.id}`}
              onSelect={() => navigate(`/library?song=${track.id}`)}
            >
              <Music class="text-muted-foreground" />
              <span class="min-w-0 flex-1 truncate">{track.title ?? track.fileName}</span>
              <span class="text-muted-foreground min-w-0 truncate pl-3 text-right text-xs">
                {trackArtist(track)}{track.album ? ` · ${track.album}` : ''}
              </span>
            </Command.Item>
          {/each}
        </Command.Group>
      {/if}

    {/if}
  </Command.List>
</Command.Dialog>
