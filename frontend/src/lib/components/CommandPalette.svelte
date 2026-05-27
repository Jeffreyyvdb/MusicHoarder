<script lang="ts">
  import { goto } from '$app/navigation';
  import {
    Disc3,
    Mic2,
    Music,
    Library,
    Users,
    CalendarRange,
    Gauge,
    ListMusic,
    History,
    Settings as SettingsIcon,
    FolderTree,
    LayoutDashboard,
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

  const NAV_COMMANDS: NavCommand[] = [
    { label: 'Library', href: '/library', icon: Library, keywords: 'albums home' },
    { label: 'Artists', href: '/artists', icon: Users, keywords: 'performers' },
    { label: 'Years', href: '/years', icon: CalendarRange, keywords: 'decades' },
    { label: 'Quality', href: '/quality', icon: Gauge, keywords: 'grade bitrate' },
    { label: 'Spotify', href: '/spotify', icon: ListMusic, keywords: 'playlists liked' },
    { label: 'Provenance & review', href: '/review', icon: History, keywords: 'review manual' },
    { label: 'Runs', href: '/runs', icon: History, keywords: 'jobs history' },
    { label: 'Directories', href: '/directories', icon: FolderTree, keywords: 'folders tree' },
    { label: 'Overview', href: '/overview', icon: LayoutDashboard, keywords: 'dashboard stats' },
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
  const sourceArtists = $derived(
    artistMatches.filter((a) => !builtArtistKeys.has(a.key)).slice(0, MAX_PER_GROUP)
  );

  const albumMatches = $derived(
    hasQuery
      ? albums.filter((a) => a.title.toLowerCase().includes(q) || a.artist.toLowerCase().includes(q))
      : []
  );
  const libraryAlbums = $derived(
    albumMatches.filter((a) => a.songs.some(isBuiltSong)).slice(0, MAX_PER_GROUP)
  );
  const sourceAlbums = $derived(
    albumMatches.filter((a) => !a.songs.some(isBuiltSong)).slice(0, MAX_PER_GROUP)
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
  const sourceTracks = $derived(
    trackMatches.filter((t) => !isBuiltSong(t)).slice(0, MAX_PER_GROUP)
  );

  const hasLibraryResults = $derived(
    libraryArtists.length > 0 || libraryAlbums.length > 0 || libraryTracks.length > 0
  );
  const hasSourceResults = $derived(
    sourceArtists.length > 0 || sourceAlbums.length > 0 || sourceTracks.length > 0
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
      {#if hasQuery && !hasLibraryResults && !hasSourceResults && navMatches.length === 0}
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
        <Command.Group heading="Library · Artists">
          {#each libraryArtists as artist (artist.key)}
            <Command.Item
              value={`lib-artist-${artist.key}`}
              onSelect={() => navigate(`/library?artist=${encodeURIComponent(artist.key)}`)}
            >
              <Mic2 class="text-muted-foreground" />
              <span class="truncate">{artist.label}</span>
              <span class="text-muted-foreground ml-auto text-xs">
                {artist.trackCount} {artist.trackCount === 1 ? 'track' : 'tracks'}
              </span>
            </Command.Item>
          {/each}
        </Command.Group>
      {/if}

      {#if libraryAlbums.length > 0}
        <Command.Group heading="Library · Albums">
          {#each libraryAlbums as album (album.key)}
            <Command.Item
              value={`lib-album-${album.key}`}
              onSelect={() => navigate(`/library?album=${encodeURIComponent(album.key)}`)}
            >
              <Disc3 class="text-muted-foreground" />
              <span class="truncate">{album.title}</span>
              <span class="text-muted-foreground ml-auto truncate pl-2 text-xs">{album.artist}</span>
            </Command.Item>
          {/each}
        </Command.Group>
      {/if}

      {#if libraryTracks.length > 0}
        <Command.Group heading="Library · Tracks">
          {#each libraryTracks as track (track.id)}
            <Command.Item
              value={`lib-track-${track.id}`}
              onSelect={() => navigate(`/library?song=${track.id}`)}
            >
              <Music class="text-muted-foreground" />
              <span class="truncate">{track.title ?? track.fileName}</span>
              <span class="text-muted-foreground ml-auto truncate pl-2 text-xs">
                {trackArtist(track)}{track.album ? ` · ${track.album}` : ''}
              </span>
            </Command.Item>
          {/each}
        </Command.Group>
      {/if}

      {#if hasLibraryResults && hasSourceResults}
        <Command.Separator />
      {/if}

      {#if sourceArtists.length > 0}
        <Command.Group heading="Source · Artists">
          {#each sourceArtists as artist (artist.key)}
            <Command.Item
              value={`src-artist-${artist.key}`}
              onSelect={() => navigate(`/library?artist=${encodeURIComponent(artist.key)}`)}
            >
              <Mic2 class="text-muted-foreground" />
              <span class="truncate">{artist.label}</span>
              <span class="text-muted-foreground ml-auto text-xs">
                {artist.trackCount} {artist.trackCount === 1 ? 'track' : 'tracks'}
              </span>
            </Command.Item>
          {/each}
        </Command.Group>
      {/if}

      {#if sourceAlbums.length > 0}
        <Command.Group heading="Source · Albums">
          {#each sourceAlbums as album (album.key)}
            <Command.Item
              value={`src-album-${album.key}`}
              onSelect={() => navigate(`/library?album=${encodeURIComponent(album.key)}`)}
            >
              <Disc3 class="text-muted-foreground" />
              <span class="truncate">{album.title}</span>
              <span class="text-muted-foreground ml-auto truncate pl-2 text-xs">{album.artist}</span>
            </Command.Item>
          {/each}
        </Command.Group>
      {/if}

      {#if sourceTracks.length > 0}
        <Command.Group heading="Source · Tracks">
          {#each sourceTracks as track (track.id)}
            <Command.Item
              value={`src-track-${track.id}`}
              onSelect={() => navigate(`/library?song=${track.id}`)}
            >
              <Music class="text-muted-foreground" />
              <span class="truncate">{track.title ?? track.fileName}</span>
              <span class="text-muted-foreground ml-auto truncate pl-2 text-xs">
                {trackArtist(track)}{track.album ? ` · ${track.album}` : ''}
              </span>
            </Command.Item>
          {/each}
        </Command.Group>
      {/if}
    {/if}
  </Command.List>
</Command.Dialog>
