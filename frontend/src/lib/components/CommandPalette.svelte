<script lang="ts">
  import { untrack } from 'svelte';
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import {
    CircleUser,
    Clock,
    Disc3,
    Mic2,
    Music,
    Loader2,
    Settings,
    type LucideIcon
  } from '@lucide/svelte';
  import * as Command from '$lib/components/ui/command';
  import * as Dialog from '$lib/components/ui/dialog';
  import { navGroupsFor, tabsFor } from '$lib/nav';
  import { isAdmin, isDemo } from '$lib/auth/capabilities';
  import {
    buildArtistGroups,
    type ApiSong,
    type AlbumSummary,
    type GroupSummary
  } from '$lib/api-client';
  import { commandPalette } from '$lib/stores/command-palette.svelte';
  import { holdAppInert } from '$lib/actions/inert-app';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { indexForSearch, rankBySearch, searchTerms } from '$lib/search/match';
  import { cn } from '$lib/utils';

  // Cap each result group so typing stays snappy on large libraries.
  const MAX_PER_GROUP = 8;

  type NavCommand = {
    label: string;
    href: string;
    icon: LucideIcon;
    keywords: string;
    group: string;
  };

  // Every nav destination, flattened from the shared role-filtered groups so the "Pages" results
  // read exactly like the sidebar and can never miss (or over-offer) a route — the hand-kept
  // list this replaced had no entry for Overview, Liked songs, Wishlist, Playlist sync, Stats
  // or History. The group name joins the haystack, so typing "manage" surfaces everything
  // under Manage; a member only gets Listen destinations.
  const NAV_COMMANDS = $derived.by<NavCommand[]>(() => {
    const groups = navGroupsFor(page.data.user);
    const commands: NavCommand[] = groups.flatMap((group) =>
      group.items.map((item) => ({
        label: item.label,
        href: item.href,
        icon: item.icon,
        keywords: `${group.label} ${item.keywords ?? ''}`.toLowerCase(),
        group: group.label
      }))
    );
    // Settings and the account for everyone. A member's nav has no Settings item (it lives under
    // Manage), yet their passkeys, phone pairing and sign-out are there — and on a phone the only
    // other way in is the avatar, so the palette must find them too.
    if (!commands.some((c) => c.href === '/settings')) {
      commands.push({
        label: 'Settings',
        href: '/settings?tab=account',
        icon: Settings,
        keywords: 'settings preferences config',
        group: 'Account'
      });
    }
    commands.push({
      label: 'Account',
      href: '/settings?tab=account',
      icon: CircleUser,
      keywords: 'account profile passkeys sign out log out switch accounts pair phone device',
      group: 'Account'
    });
    return commands;
  });

  // bits-ui's Dialog binds cleanly to a plain local $state; the shared store
  // (driven by the global shortcut + header badge) is mirrored into it.
  let dialogOpen = $state(false);
  $effect(() => {
    dialogOpen = commandPalette.open;
  });

  // The page behind the palette leaves the accessibility tree while it is open (inert-app.ts).
  $effect(() => (dialogOpen ? holdAppInert() : undefined));

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
  const builtSongs = $derived(songsStore.builtSongs);

  // The same cards the Library page shows — one result per album even when it is split across
  // destination folders on disk, and the same `?album=` keys, because it is literally the same list.
  const albums = $derived<AlbumSummary[]>(songsStore.albums);
  const artists = $derived<GroupSummary[]>(buildArtistGroups(builtSongs));

  // Per-entity folded haystacks, rebuilt only when the dataset changes — one normalization pass
  // per row instead of three per keystroke. Fields are passed most-significant first, which is what
  // ranks a title hit over an album hit (`$lib/search/match`).
  const trackIndex = $derived(
    indexForSearch(builtSongs, (s) => [
      s.title ?? s.fileName,
      s.artist ?? s.albumArtist ?? '',
      s.album ?? ''
    ])
  );
  const albumIndex = $derived(indexForSearch(albums, (a) => [a.title, a.artist]));
  const artistIndex = $derived(indexForSearch(artists, (a) => [a.label]));
  const navIndex = $derived(indexForSearch(NAV_COMMANDS, (c) => [c.label, c.keywords]));

  // A query is its terms: every term has to be found, in any field and in any order, over text
  // folded to letters and digits. That is what makes "best friend with fall out" find
  // "Best Friend (with Fall Out Boy)" — the old contiguous-substring match was stopped by the
  // bracket — and what lets "juice best friend" match a title plus its artist.
  const terms = $derived(searchTerms(query));
  const hasQuery = $derived(terms.length > 0);

  // Each group is ranked before it is capped, so the eight results shown are the eight *best*
  // ones. They used to be the first eight in library order, which is how a search for
  // "best friend" filled up with other people's songs and dropped the one being looked for.
  const navMatches = $derived(rankBySearch(navIndex, terms, MAX_PER_GROUP * 2));

  const libraryArtists = $derived(rankBySearch(artistIndex, terms, MAX_PER_GROUP));
  const libraryAlbums = $derived(rankBySearch(albumIndex, terms, MAX_PER_GROUP));
  const libraryTracks = $derived(rankBySearch(trackIndex, terms, MAX_PER_GROUP));

  const hasLibraryResults = $derived(
    libraryArtists.length > 0 || libraryAlbums.length > 0 || libraryTracks.length > 0
  );

  // Albums and tracks carry a second line. A phone stacks it under the title (the iOS search
  // result), so neither truncates the other; a desktop keeps one row with it right-aligned.
  const twoLine = 'flex min-w-0 flex-1 flex-col md:flex-row md:items-center md:gap-3';
  const secondLine =
    'text-muted-foreground text-footnote truncate md:ml-auto md:min-w-0 md:text-right md:text-xs';

  function trackArtist(s: ApiSong): string {
    return s.artist ?? s.albumArtist ?? 'Unknown Artist';
  }

  // ── before anything is typed ────────────────────────────────────────────────
  // Suggestions, not the whole map: recent searches, recently played tracks and a short "Go to"
  // (the tab roots and Settings). Listing every destination here only repeated the tab bar and the
  // hubs; typing still finds any page ("Pages" below).
  const RECENT_KEY = 'mh:palette-recent';
  const MAX_RECENT = 5;
  function readRecent(): string[] {
    try {
      const raw = typeof localStorage === 'undefined' ? null : localStorage.getItem(RECENT_KEY);
      const parsed: unknown = raw ? JSON.parse(raw) : [];
      return Array.isArray(parsed)
        ? parsed.filter((q) => typeof q === 'string').slice(0, MAX_RECENT)
        : [];
    } catch {
      return [];
    }
  }
  // Per viewer and per browser — a convenience, so a blocked storage just starts empty.
  let recentSearches = $state<string[]>(readRecent());
  function rememberSearch() {
    const term = query.trim();
    if (!term) return;
    recentSearches = [
      term,
      ...recentSearches.filter((q) => q.toLowerCase() !== term.toLowerCase())
    ].slice(0, MAX_RECENT);
    try {
      localStorage.setItem(RECENT_KEY, JSON.stringify(recentSearches));
    } catch {
      /* not remembered */
    }
  }

  const recentlyPlayed = $derived.by(() => {
    if (hasQuery) return [];
    return builtSongs
      .filter((s) => s.lastPlayedAtUtc)
      .sort((a, b) => (b.lastPlayedAtUtc ?? '').localeCompare(a.lastPlayedAtUtc ?? ''))
      .slice(0, MAX_RECENT);
  });

  const goTo = $derived.by<{ label: string; href: string; icon: LucideIcon }[]>(() => {
    const user = page.data.user;
    const settingsHref = isAdmin(user) || isDemo(user) ? '/settings' : '/settings?tab=account';
    return [
      ...tabsFor(user).map((tab) => ({ label: tab.label, href: tab.root, icon: tab.icon })),
      { label: 'Settings', href: settingsHref, icon: Settings }
    ];
  });

  function dismiss() {
    commandPalette.setOpen(false);
    query = '';
  }

  function navigate(href: string) {
    rememberSearch();
    dismiss();
    void goto(href);
  }

  // Tracks open the global song-detail overlay in place. It's mounted in the app
  // shell alongside this palette, and it resolves against the same rows we just
  // searched, so it paints immediately — no route change, no album drilldown, and
  // none of the requests either of those fan out.
  function openTrack(song: ApiSong) {
    rememberSearch();
    dismiss();
    songDetail.open(song.id);
  }
</script>

<!--
  Spotlight-shaped: a centred panel near the top on a desktop; on a phone a full-height page
  anchored under the status bar, the field at the top with Cancel beside it and the results filling
  the space down to the keyboard. That is a deliberate departure from iOS 26, whose search field
  sits above the keyboard: WebKit has no `interactive-widget`, so a bottom-docked field would need
  visual-viewport arithmetic to dodge the keyboard. No open animation on either (a keyboard-first,
  very frequent action).
-->
<!-- z-[75] (content and overlay): above Now Playing (60) and its nested sheets (70), below alerts
     (80). At the dialog default of z-50, ⌘K over Now Playing opened the palette invisibly UNDER it
     while its field still took the focus — typing and Space went into a hidden input. -->
<Dialog.Root bind:open={dialogOpen} onOpenChange={handleOpenChange}>
  <Dialog.Content
    showCloseButton={false}
    overlayClass="z-[75]"
    class={cn(
      'z-[75] gap-0 overflow-hidden p-0',
      // Desktop: the panel, high enough that the whole list fits under it.
      'md:top-[10vh] md:max-w-2xl md:translate-y-0',
      // Phone: the whole screen, a plain page rather than a floating card.
      'max-md:bg-background max-md:inset-0 max-md:h-dvh max-md:max-h-none max-md:w-full max-md:max-w-none max-md:translate-x-0 max-md:translate-y-0 max-md:rounded-none max-md:pt-[env(safe-area-inset-top)] max-md:shadow-none max-md:ring-0 max-md:data-closed:animate-none max-md:data-open:animate-none'
    )}
  >
    <Dialog.Title class="sr-only">Search everywhere</Dialog.Title>
    <Dialog.Description class="sr-only">
      Search tracks, albums, artists and pages.
    </Dialog.Description>
    <Command.Root
      shouldFilter={false}
      class="max-md:bg-background max-md:rounded-none! max-md:p-0 md:max-h-[80vh]"
    >
      <div class="flex items-center gap-2 max-md:px-3 max-md:pt-2 max-md:pb-1">
        <div class="min-w-0 flex-1">
          <!-- "Search", as iOS words it: the scope ("tracks, albums, artists, pages") was cut off
               on a phone, and the description above already tells a screen reader. -->
          <Command.Input bind:value={query} placeholder="Search" />
        </div>
        <button
          type="button"
          onclick={dismiss}
          class="text-primary text-body focus-visible:ring-ring -mr-1 h-11 shrink-0 rounded-md px-2 outline-none focus-visible:ring-2 md:hidden"
        >
          Cancel
        </button>
      </div>
      <!-- md: top-[10vh] + this cap keeps the whole panel inside the viewport; the shadcn default
           (top-1/3 with a 60vh list) ran off the bottom of the page. On a phone the list takes the
           rest of the screen, with 44pt rows and body-size text.
           The checked-state indicator every command item renders is dead weight here — nothing
           in the palette is checkable — and its `ml-auto` fought the trailing meta column's own
           auto margin, leaving each row's group label at a different x. -->
      <Command.List
        class="max-md:[&_[data-slot=command-item]]:text-body max-md:[&_[data-command-group-heading]]:text-footnote max-md:max-h-none max-md:min-h-0 max-md:flex-1 max-md:overscroll-contain max-md:px-2 max-md:pb-[max(16px,env(safe-area-inset-bottom))] md:max-h-[65vh] [&_.cn-command-item-indicator]:hidden max-md:[&_[data-slot=command-item]]:min-h-11 max-md:[&_[data-slot=command-item]]:gap-3"
      >
        <!-- The page commands are local, so they stay usable while the library
             dataset is still in flight — only the library groups wait. -->
        {#if hasQuery && !hasLibraryResults && navMatches.length === 0 && !loading}
          <Command.Empty class="max-md:text-body text-muted-foreground"
            >No results for “{query}”.</Command.Empty
          >
        {/if}

        {#if !hasQuery}
          {#if recentSearches.length > 0}
            <Command.Group heading="Recent searches">
              {#each recentSearches as term (term)}
                <Command.Item value={`recent-${term}`} onSelect={() => (query = term)}>
                  <Clock class="text-muted-foreground" />
                  <span class="min-w-0 flex-1 truncate">{term}</span>
                </Command.Item>
              {/each}
            </Command.Group>
          {/if}
          {#if recentlyPlayed.length > 0}
            <Command.Group heading="Recently played">
              {#each recentlyPlayed as track (track.id)}
                <Command.Item value={`played-${track.id}`} onSelect={() => openTrack(track)}>
                  <Music class="text-muted-foreground" />
                  <span class={twoLine}>
                    <span class="truncate">{track.title ?? track.fileName}</span>
                    <span class={secondLine}>{trackArtist(track)}</span>
                  </span>
                </Command.Item>
              {/each}
            </Command.Group>
          {/if}
          <Command.Group heading="Go to">
            {#each goTo as dest (dest.href)}
              <Command.Item value={`goto-${dest.href}`} onSelect={() => navigate(dest.href)}>
                <dest.icon class="text-muted-foreground" />
                <span class="min-w-0 flex-1 truncate">{dest.label}</span>
              </Command.Item>
            {/each}
          </Command.Group>
        {/if}

        {#if hasQuery && navMatches.length > 0}
          <Command.Group heading="Pages">
            {#each navMatches as cmd (`${cmd.group}:${cmd.label}`)}
              <Command.Item
                value={`nav-${cmd.group}-${cmd.label}`}
                onSelect={() => navigate(cmd.href)}
              >
                <cmd.icon class="text-muted-foreground" />
                <span class="min-w-0 flex-1 truncate">{cmd.label}</span>
                <!-- The group disambiguates the two "Artists" and the two "Albums" — one of each
                     is a library view, the other an Inbox review queue. -->
                <span class="text-muted-foreground text-footnote shrink-0 pl-3 md:text-xs"
                  >{cmd.group}</span
                >
              </Command.Item>
            {/each}
          </Command.Group>
        {/if}

        {#if loading && hasQuery}
          <div
            class="text-muted-foreground max-md:text-body flex items-center gap-2 px-3 py-6 text-sm"
          >
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
                <span class="text-muted-foreground text-footnote shrink-0 pl-3 md:text-xs">
                  {artist.trackCount}
                  {artist.trackCount === 1 ? 'track' : 'tracks'}
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
                <span class={twoLine}>
                  <span class="truncate">{album.title}</span>
                  <span class={secondLine}>{album.artist}</span>
                </span>
              </Command.Item>
            {/each}
          </Command.Group>
        {/if}

        {#if libraryTracks.length > 0}
          <Command.Group heading="Tracks">
            {#each libraryTracks as track (track.id)}
              <Command.Item value={`lib-track-${track.id}`} onSelect={() => openTrack(track)}>
                <Music class="text-muted-foreground" />
                <span class={twoLine}>
                  <span class="truncate">{track.title ?? track.fileName}</span>
                  <span class={secondLine}>
                    {trackArtist(track)}{track.album ? ` · ${track.album}` : ''}
                  </span>
                </span>
              </Command.Item>
            {/each}
          </Command.Group>
        {/if}
      </Command.List>
    </Command.Root>
  </Dialog.Content>
</Dialog.Root>
