<script lang="ts">
  import {
    ChevronRight,
    CircleCheck,
    Clock,
    Copy,
    Disc3,
    Download,
    Ellipsis,
    Eye,
    EyeOff,
    HardDrive,
    Heart,
    History,
    Loader2,
    Pause,
    Play,
    RefreshCw,
    Search,
    Send,
    Share,
    Shuffle,
    Sparkles,
    TriangleAlert,
    Users,
    UsersRound
  } from '@lucide/svelte';
  import { untrack } from 'svelte';
  import { page } from '$app/state';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { Badge } from '$lib/components/ui/badge';
  import { Button } from '$lib/components/ui/button';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import AlbumTimelineDialog from '$lib/components/file-browser/AlbumTimelineDialog.svelte';
  import ProvenanceSheet from '$lib/components/file-browser/ProvenanceSheet.svelte';
  import ShareWithFriendDialog from '$lib/components/file-browser/ShareWithFriendDialog.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import TrackRowMenu, { activateTrack } from '$lib/components/v2/TrackRowMenu.svelte';
  import { longpress, type LongPressPoint } from '$lib/actions/long-press';
  import { albumTint, coverTint } from '$lib/album-tint';
  import { buildDisplayRows } from '$lib/album-rows';
  import {
    formatDuration,
    formatFamily,
    formatFileSize,
    formatReleaseDate,
    formatTotalDuration
  } from '$lib/formatters';
  import { SvelteSet } from 'svelte/reactivity';
  import {
    acquireCanonicalTrack,
    copyAlbumDossier,
    coverThumbUrl,
    fetchAlbumDetail,
    fetchSongProvenance,
    gradeAlbum,
    mapEnrichmentStatus,
    prettyProvider,
    rebuildAlbum,
    toPlayerSong,
    type AlbumLinkStatus,
    type AlbumQualityGradeView,
    type AlbumSummary,
    type AlbumTracklist,
    type ApiSong,
    type SongProvenance
  } from '$lib/api-client';
  import { PROVENANCE_ICON } from '$lib/provenance';
  import { VERDICT_DOT } from '$lib/quality-ui';
  import { findShareLink, shareLink, type ShareLink } from '$lib/share-links';
  import { sendTo, sendToAlbum } from '$lib/stores/send-to.svelte';
  import { toast } from 'svelte-sonner';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { albumViewPrefs } from '$lib/stores/album-view-prefs.svelte';
  import { playerStore, type StartQueueOptions } from '$lib/stores/player.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { tabMemory } from '$lib/stores/tab-memory.svelte';
  import { cn, shuffle } from '$lib/utils';
  import { can, isAdmin } from '$lib/auth/capabilities';

  type Props = {
    album: AlbumSummary | null;
    isLoading: boolean;
  };
  const { album, isLoading }: Props = $props();

  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);
  // A desktop bar only shows Back when the page asks for it; a drill-in does (it replaced the old
  // hard-coded "All albums" pill, and goes wherever you came from — an artist, a shelf, Tracks).
  const desktopBack = $derived(
    compact ? undefined : tabMemory.backTarget(page.url, page.data.user)
  );

  const tint = $derived(album ? albumTint(album.artist, album.title) : null);
  const tracks = $derived(album?.songs ?? []);

  // Back reads "Back to <this album>" from whatever is pushed on top of it.
  $effect(() => {
    if (album) tabMemory.setTitle(page.url, album.title);
  });

  /**
   * Who shared this album, or null when the current account owns it. Taken from the first track
   * that carries an attribution — an album is always one grantor's, since grant scoping never
   * mixes owners into a single album.
   */
  const sharedBy = $derived.by(() => {
    for (const track of tracks) {
      const grantor = songsStore.grantorOf(track);
      if (grantor) return grantor.displayName?.trim() || 'someone';
    }
    return null;
  });

  // Reconciled multi-provider canonical tracklist for this album, fetched lazily by album identity
  // (artist + title). `linkStatus` tells us whether the album is matched to a provider album
  // ("linked"), only in the local library ("localOnly"), or not yet checked ("pending"). When linked
  // we show every real track and grey out the ones the user is missing; otherwise we fall back to the
  // owned-only list below.
  let tracklist = $state<AlbumTracklist | null>(null);
  let linkStatus = $state<AlbumLinkStatus>('pending');
  // AI grade of whether this album was linked to the *correct* provider album.
  let albumGrade = $state<AlbumQualityGradeView | null>(null);
  let grading = $state(false);
  // True while the combined album-detail request is in flight, so the status row can say so
  // instead of letting the link state pop in.
  let loadingDetail = $state(false);
  // When true, collapse the tracklist to just the songs the user owns, hiding the greyed-out
  // canonical tracks they're missing. Toggle from the ⋯ menu or with the `H` key. Backed by
  // `albumViewPrefs` so the choice persists across albums (and reloads).
  const hideMissing = $derived(albumViewPrefs.hideMissing);
  // The ScrollArea viewport, so we can clamp scrollTop after the list shrinks (see below).
  let scrollViewport = $state<HTMLElement | null>(null);
  // The hero's album title: the nav bar's inline title fades in once it has scrolled under the bar.
  let heroTitleEl = $state<HTMLElement | null>(null);
  // What identifies the album we need the detail for. Kept apart from the fetching effect on
  // purpose: `album` is a *fresh object* after every songs-store refresh (the library rebuilds its
  // summaries from the song rows, and that store refetches on every pipeline progress tick), so an
  // effect reading `album` directly re-ran every few seconds on a page that never changed — which
  // blanked the status back to "Checking providers…" and re-requested the same detail. That was
  // the flicker.
  const detailParams = $derived.by(() => {
    if (!album) return null;
    const { artist, title } = album;
    if (!artist || !title) return null;
    // Built albums group by destination folder (AlbumSummary.key is that folder). Pass it so the
    // backend matches canonical tracks against exactly the songs this page lists — matching against
    // the wider tag-based set can annotate a track with an unbuilt duplicate the page doesn't show,
    // which renders as a false MISSING row while the built copy drops to the bonus tail.
    // A merged card spans several folders, so no single folder describes what's listed: fall back
    // to the tag-based set, which is the wider one and covers all of them.
    const singleFolder = album.folderKeys.length <= 1;
    const folder = singleFolder && album.songs.some((s) => s.destinationPath) ? album.key : null;
    return { artist, title, year: album.year ?? null, folder };
  });
  // A string `$derived` only notifies its readers when the value actually changes (`===`), so this
  // is the effect's whole dependency: it re-runs on real navigation and stays put through every
  // background refresh of the same album.
  const detailKey = $derived(
    detailParams
      ? [
          detailParams.artist,
          detailParams.title,
          detailParams.year ?? '',
          detailParams.folder ?? ''
        ].join('\u0000')
      : null
  );

  // One request on navigation fetches the tracklist + grade together, so link status and grade land
  // in the same tick (no two-step pop-in). Resets all three pieces up front so the next album starts
  // clean, and the cleanup guard drops a stale in-flight response.
  $effect(() => {
    const key = detailKey;
    tracklist = null;
    linkStatus = 'pending';
    albumGrade = null;
    // The canonical-tracklist endpoints are owner-only; a friend's album page just renders the
    // owned tracks without link badges/grades instead of firing a guaranteed 403.
    if (!key || !isOwner) {
      loadingDetail = false;
      return;
    }
    // Read untracked: `detailKey` above is the dependency, and these are the same values it encodes.
    const { artist, title, year, folder } = untrack(() => detailParams)!;
    let cancelled = false;
    loadingDetail = true;
    void fetchAlbumDetail(artist, title, year, folder)
      .then((d) => {
        if (cancelled) return;
        tracklist = d.tracklist;
        linkStatus = d.status;
        albumGrade = d.grade;
      })
      .catch(() => {
        // Best-effort: on error keep the owned-only fallback and unset grade.
      })
      .finally(() => {
        if (!cancelled) loadingDetail = false;
      });
    return () => {
      cancelled = true;
    };
  });

  /** Provider names that won the reconciliation, for the "Linked" status. */
  const sourceLabels = $derived(
    tracklist
      ? tracklist.sources.filter((s) => s.inWinningCluster).map((s) => prettyProvider(s.provider))
      : []
  );

  // ── how it got here ────────────────────────────────────────────────────────
  // Why these tracks are in the library at all — local files, a Spotify like, a playlist, or album
  // completion filling in around a track you had. Only your own albums: a shared album's history is
  // its owner's (the endpoint would answer empty anyway). Keyed on the id list as a string for the
  // same reason `detailKey` is: `album` is a fresh object on every songs-store refresh.
  const provenanceKey = $derived(
    album && !sharedBy
      ? album.songs
          .map((s) => s.id)
          .sort((a, b) => a - b)
          .join(',')
      : null
  );
  // Kept per album and not cleared on a refetch, so a track arriving mid-visit updates the row in
  // place instead of blinking it back to "Tracing…".
  let provenanceFor = $state<{ albumKey: string; data: SongProvenance } | null>(null);
  let provenanceError = $state<string | null>(null);
  let provenanceOpen = $state(false);
  const provenance = $derived(
    provenanceFor && provenanceFor.albumKey === album?.key ? provenanceFor.data : null
  );
  $effect(() => {
    const key = provenanceKey;
    provenanceError = null;
    if (!key) return;
    const albumKey = untrack(() => album?.key) ?? '';
    let cancelled = false;
    void fetchSongProvenance(key.split(',').map(Number))
      .then((data) => {
        if (!cancelled) provenanceFor = { albumKey, data };
      })
      .catch(() => {
        if (!cancelled) provenanceError = 'Could not work out how this album got here.';
      });
    return () => {
      cancelled = true;
    };
  });
  // Shown while it loads (so the tracklist does not jump when it lands), hidden if it fails or
  // there turned out to be nothing to say.
  const showProvenance = $derived(
    provenanceKey !== null && !provenanceError && (!provenance || Boolean(provenance.summary))
  );

  // Album provenance timeline, opened from the ⋯ menu.
  let timelineOpen = $state(false);
  // The admin status row's sheet: link state, completeness, the AI grade.
  let infoOpen = $state(false);

  async function gradeNow() {
    if (!album || grading) return;
    grading = true;
    try {
      const r = await gradeAlbum(album.artist, album.title);
      albumGrade = {
        graded: true,
        verdict: r.verdict ?? undefined,
        score: r.score ?? undefined,
        summary: r.summary,
        issues: r.issues,
        gradedAtUtc: r.gradedAtUtc ?? undefined
      };
    } catch {
      // grading endpoint returns 503 when unconfigured/disabled — leave the prior grade as-is.
      toast.error('Could not grade this album');
    } finally {
      grading = false;
    }
  }

  async function copyDossier() {
    if (!album) return;
    try {
      await copyAlbumDossier(album.artist, album.title);
      toast.success('Copied album dossier to clipboard — paste into an AI assistant');
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Copy failed');
    }
  }

  async function copyText(text: string, what: string) {
    try {
      await navigator.clipboard.writeText(text);
      toast.success(`Copied the ${what}`);
    } catch {
      toast.error(`Could not copy the ${what}`);
    }
  }

  /** Copies the destination folder path — the closest a web app gets to "reveal in Finder". */
  function revealInDestination() {
    if (destinationFolder) void copyText(destinationFolder, 'destination folder path');
  }

  // Re-tag: re-queue this album's already-built tracks so the next build re-copies and re-tags their
  // destination files in place with the current tag-writing logic (album-identity reconciliation).
  // No re-enrichment. The outcome is a toast; the menu item shows the state while it lasts.
  let retagState = $state<'idle' | 'loading' | 'done' | 'error'>('idle');
  let retagMessage = $state<string | null>(null);
  let retagTimer: ReturnType<typeof setTimeout> | null = null;

  async function retagAlbum() {
    if (!album || retagState === 'loading') return;
    if (retagTimer) clearTimeout(retagTimer);
    retagState = 'loading';
    retagMessage = null;
    try {
      const res = await rebuildAlbum(album.artist, album.title);
      if (res.ok) {
        retagState = 'done';
        retagMessage =
          res.requeued > 0
            ? `Re-tagging ${res.requeued} track${res.requeued === 1 ? '' : 's'}…`
            : 'Nothing to re-tag yet';
        toast.success(retagMessage);
      } else {
        retagState = 'error';
        retagMessage = res.message;
        toast.error(retagMessage);
      }
    } catch (err) {
      retagState = 'error';
      retagMessage = err instanceof Error ? err.message : 'Re-tag failed';
      toast.error(retagMessage);
    } finally {
      retagTimer = setTimeout(() => {
        retagState = 'idle';
        retagMessage = null;
      }, 4000);
    }
  }

  // Account-to-account sharing (grants) and public links are an owner power; friends/demo get
  // neither (and no ⋯ at all — every item in it is an admin action).
  const isOwner = $derived(isAdmin(page.data.user));
  const canShareWithPeople = $derived(isOwner && can(page.data.user, 'ManageOwnShares'));
  let shareWithFriendOpen = $state(false);

  // ── Share link… ────────────────────────────────────────────────────────────
  // Opening ⋯ only LOOKS for a link this album already has (share-links.ts explains why minting on
  // open was wrong: it published a public link whenever an admin opened the menu to re-tag or copy
  // a path). With one in hand the share sheet opens straight from the tap; without, Share link…
  // creates it and copies it as it lands.
  let knownShare = $state<{ key: string; link: ShareLink | null } | null>(null);

  function onMoreOpenChange(open: boolean) {
    const current = album;
    if (!open || !isOwner || !current || knownShare?.key === current.key) return;
    const key = current.key;
    void findShareLink(
      current.songs.map((s) => s.id),
      'album'
    ).then((link) => {
      if (album?.key === key) knownShare = { key, link };
    });
  }

  function shareAlbum() {
    const first = album?.songs[0];
    if (!album || !first) return;
    shareLink({
      known: knownShare?.key === album.key ? knownShare.link : null,
      songId: first.id,
      scope: 'album'
    });
    knownShare = null; // a link exists now; the next open finds it
  }

  // Send to…: the album, to people on this MusicHoarder, in their chats — carried by the same kind
  // of link as Share link…, keyed on the album's first track as that one is.
  function sendAlbum() {
    const first = album?.songs[0];
    if (!album || !first) return;
    sendTo.show(sendToAlbum(first, album.title, album.artist));
  }

  const displayRows = $derived(buildDisplayRows(album?.songs, tracklist));

  /** Whether multiple discs are present, so we can show a disc prefix on track numbers. */
  const multiDisc = $derived(displayRows.some((r) => r.disc > 1));

  /** Number of canonical tracks the user is missing — drives the hide/show toggle's visibility. */
  const missingCount = $derived(displayRows.filter((r) => r.kind === 'missing').length);

  /** Rows actually rendered: all of them, or only owned ones when `hideMissing` is on. */
  const visibleRows = $derived(
    hideMissing ? displayRows.filter((r) => r.kind === 'owned') : displayRows
  );

  // Toggling hideMissing shrinks the list; clamp scrollTop so the viewport can't sit
  // parked in blank space below the (now shorter) content. Runs after the DOM is patched,
  // so scrollHeight reflects the new list.
  $effect(() => {
    void hideMissing;
    const vp = scrollViewport;
    if (!vp) return;
    const max = vp.scrollHeight - vp.clientHeight;
    if (vp.scrollTop > max) vp.scrollTop = Math.max(0, max);
  });

  const completeness = $derived.by(() => {
    const tl = tracklist;
    if (!tl || tl.totalCount === 0) return null;
    return {
      owned: tl.ownedCount,
      total: tl.totalCount,
      pct: Math.round((tl.ownedCount / tl.totalCount) * 100)
    };
  });

  /** `H` toggles hiding missing tracks — page-scoped, ignored while typing or with modifiers. */
  function onKeydown(e: KeyboardEvent) {
    const el = e.target as HTMLElement | null;
    if (el && (el.tagName === 'INPUT' || el.tagName === 'TEXTAREA' || el.isContentEditable)) return;
    if (e.metaKey || e.ctrlKey || e.altKey) return;
    if (e.key.toLowerCase() === 'h' && missingCount > 0) {
      e.preventDefault();
      albumViewPrefs.toggleHideMissing();
    }
  }

  // ── acquiring a missing track ───────────────────────────────────────────────
  // The manual counterpart to the album-completion sweep: it works with that switched off, which is
  // how you'd try the feature the first time. The queued track is stamped AlbumFill like any other,
  // so it shows up in Tracks and on this page, marked as album fill rather than something you asked for.
  const acquiring = new SvelteSet<number>();
  const acquired = new SvelteSet<number>();

  async function onAcquire(canonicalTrackId: number) {
    if (acquiring.has(canonicalTrackId) || acquired.has(canonicalTrackId)) return;
    acquiring.add(canonicalTrackId);
    try {
      await acquireCanonicalTrack(canonicalTrackId);
      acquired.add(canonicalTrackId);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Could not queue that track');
    } finally {
      acquiring.delete(canonicalTrackId);
    }
  }

  /** Web-search URL so the user can go find a track they're missing. */
  function findUrl(title: string): string {
    const q = `${album?.artist ?? ''} ${title}`.trim();
    return `https://www.google.com/search?q=${encodeURIComponent(q)}`;
  }

  // Selection drives the global song-detail store directly (its open state is the
  // single source of truth — no URL round-trip), so the highlight always matches
  // the open panel.
  const selectedTrackId = $derived(songDetail.isOpen ? (songDetail.target?.songId ?? null) : null);

  const currentlyPlaying = $derived.by(() => {
    const playing = playerStore.currentSong;
    if (!playing) return null;
    return tracks.find((t) => t.id === playing.id) ?? null;
  });

  /** Desktop row click: toggle the detail overlay for that song, with this album as context. */
  function selectTrack(s: ApiSong) {
    if (songDetail.isOpen && songDetail.target?.songId === s.id) {
      songDetail.close();
    } else {
      songDetail.open(s.id, album?.key);
    }
  }

  /** Plays the album from `target`; never pauses (a tap, a menu's Play, the Play pill). */
  function playFrom(target: ApiSong, options?: StartQueueOptions) {
    if (!album) return;
    const queue = tracks.map((t) => toPlayerSong(t, album.artist));
    void playerStore.startQueue(
      queue,
      tracks.findIndex((t) => t.id === target.id),
      options
    );
  }

  /** A row tap: phones follow the tap rule (play from here / open the loaded song); desktop selects. */
  function onRowClick(s: ApiSong) {
    if (compact) activateTrack(s.id, () => playFrom(s), album?.key);
    else selectTrack(s);
  }

  // The pill reads Pause while one of this album's tracks is playing, so only then does it pause;
  // as Play it resumes that track — wherever it is loaded, the account's session on another device
  // included — else starts track 1.
  function playAlbumStart() {
    if (!album || tracks.length === 0) return;
    if (playerStore.isPlaying && currentlyPlaying) playerStore.pause();
    else if (currentlyPlaying) playFrom(currentlyPlaying, { resumeIfLoaded: true });
    else playFrom(tracks[0]);
  }

  // Shuffle never pauses, even when the shuffle happens to start on the song already playing.
  function playAlbumShuffle() {
    if (!album || tracks.length === 0) return;
    void playerStore.startQueue(shuffle(tracks.map((t) => toPlayerSong(t, album.artist))));
  }

  /** The desktop row's glyph shows Play/Pause for its own song, so it is the one that toggles. */
  function playTrack(s: ApiSong, e: MouseEvent) {
    e.stopPropagation();
    if (!album) return;
    const queue = tracks.map((t) => toPlayerSong(t, album.artist));
    void playerStore.playSong(
      toPlayerSong(s, album.artist),
      queue,
      tracks.findIndex((t) => t.id === s.id)
    );
  }

  /** Desktop rows are role=button; a key press on a control inside them is that control's. */
  function onRowKeydown(e: KeyboardEvent, s: ApiSong) {
    if (e.target !== e.currentTarget) return;
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      selectTrack(s);
    }
  }

  function trackBitrateLabel(s: ApiSong): string {
    const ext = (s.extension ?? '').replace(/^\./, '').toUpperCase();
    if (s.bitRate && s.bitRate > 0) {
      return ext ? `${ext} ${s.bitRate}kbps` : `${s.bitRate} kbps`;
    }
    return ext || '—';
  }

  /** Stored match confidence, or null when the pipeline never recorded one — never invented. */
  function trackMatchValue(s: ApiSong): number | null {
    if (typeof s.matchConfidence !== 'number') return null;
    return Math.max(0, Math.min(1, s.matchConfidence));
  }

  const destinationFolder = $derived.by(() => {
    const first = album?.songs[0];
    if (!first?.destinationPath) return null;
    const idx = first.destinationPath.lastIndexOf('/');
    return idx > 0 ? first.destinationPath.slice(0, idx) : first.destinationPath;
  });

  // ── hero text ───────────────────────────────────────────────────────────────
  /** "FLAC" when every track shares one family, nothing when they are mixed. */
  const formatLabel = $derived.by(() => {
    const families = new Set(tracks.map((s) => formatFamily(s.extension)));
    if (families.size !== 1) return null;
    const [family] = families;
    return family === 'OTHER' ? null : family;
  });
  const genreLabel = $derived(album?.genre?.split(';')[0]?.trim() || null);
  const heroMeta = $derived(
    [genreLabel, album?.year != null ? String(album.year) : null, formatLabel]
      .filter(Boolean)
      .join(' · ')
  );

  // The admin status row: one line that says whether the album is linked, to what, and how
  // complete it is; the sheet behind it holds the rest.
  const statusLine = $derived.by(() => {
    if (loadingDetail || linkStatus === 'pending') return 'Checking providers…';
    if (linkStatus === 'localOnly') return 'Local only';
    const parts = [sourceLabels.length > 0 ? `Linked · ${sourceLabels.join(', ')}` : 'Linked'];
    if (completeness) parts.push(`${completeness.owned}/${completeness.total}`);
    return parts.join(' · ');
  });
  const wrongMatch = $derived(albumGrade?.graded && albumGrade.verdict === 'Wrong');

  // A subtle wash of the album's own colour behind the art, the way Apple Music lets a record colour
  // its page. The colour is read off the cover itself (album-tint.ts `coverTint`: an 8×8 average
  // of a small thumbnail); the deterministic hash tint stands in when the image can't be read. It
  // fades IN from the page background rather than starting at the top edge: in the installed app
  // the status bar is painted the page colour, and a wash that began under it met a flat strip.
  const washThumb = $derived(album?.coverUrl ? coverThumbUrl(album.coverUrl, 64) : null);
  let coverWash = $state<{ url: string; color: string | null } | null>(null);
  $effect(() => {
    const url = washThumb;
    if (!url) return;
    let cancelled = false;
    void coverTint(url).then((color) => {
      if (!cancelled) coverWash = { url, color };
    });
    return () => {
      cancelled = true;
    };
  });
  // Nothing until the cover has been read (or there is none to read), so the page never shows the
  // hashed colour and then swaps it for the cover's; the layer fades in once the colour is known.
  const washReady = $derived(!washThumb || coverWash?.url === washThumb);
  const heroWash = $derived.by(() => {
    if (!tint || !washReady) return '';
    const fromCover = washThumb ? coverWash?.color : null;
    const from = fromCover ?? tint.from;
    const to = fromCover ?? tint.to;
    // The strength is a CSS variable (--album-wash, set per theme below): a dark tint at full
    // dark-mode strength turns a white page muddy, so light mode washes at a lighter mix.
    return `linear-gradient(180deg, transparent 0, color-mix(in oklch, ${from} var(--album-wash), transparent) 28%, color-mix(in oklch, ${to} 16%, transparent) 62%, transparent 100%)`;
  });

  // ── rows ───────────────────────────────────────────────────────────────────
  type RowMenu = { openAt: (point: LongPressPoint) => void };
  const menus: Record<number, RowMenu | undefined> = {};
  function onRowContextMenu(e: MouseEvent, id: number) {
    e.preventDefault();
    menus[id]?.openAt({ x: e.clientX, y: e.clientY });
  }

  const albumArtistLower = $derived(album?.artist.toLowerCase() ?? '');
  /** A track's own artist, only where it differs from the album's (features, compilations). */
  function trackArtist(s: ApiSong): string | null {
    const artist = (s.artist ?? '').trim();
    return artist && artist.toLowerCase() !== albumArtistLower ? artist : null;
  }

  // The desktop table's columns follow the table's own width (an @container), not the viewport's:
  // with the sidebar open an iPad or a half-width window is "desktop" with ~530px to spare, where
  // the fixed columns once squeezed the titles out entirely. Title, duration and ⋯ always show;
  // Format joins at @xl, Size and Match at @3xl. Header and rows share this template.
  const DESKTOP_GRID = [
    'grid-cols-[36px_minmax(0,1fr)_48px_32px]',
    '@xl:grid-cols-[44px_minmax(0,1fr)_110px_56px_32px]',
    '@3xl:grid-cols-[44px_minmax(0,1fr)_110px_80px_140px_60px_32px]'
  ].join(' ');
</script>

<svelte:window onkeydown={onKeydown} />

{#snippet albumMenu()}
  <!-- The page's own ⋯ (not the bar's `more`) so it can look up the album's share link as it
       opens. Three groups: share · files · details. Every item is an admin action. -->
  <DropdownMenu.Root onOpenChange={onMoreOpenChange}>
    <DropdownMenu.Trigger>
      {#snippet child({ props })}
        <Button {...props} variant="ghost" size="icon" aria-label="More">
          <Ellipsis />
        </Button>
      {/snippet}
    </DropdownMenu.Trigger>
    <DropdownMenu.Content align="end" class="w-64 pointer-coarse:w-72">
      <DropdownMenu.Group>
        <DropdownMenu.Item onSelect={shareAlbum}>
          <Share /> Share link…
        </DropdownMenu.Item>
        <DropdownMenu.Item onSelect={sendAlbum}>
          <Send /> Send to…
        </DropdownMenu.Item>
        {#if canShareWithPeople}
          <DropdownMenu.Item onSelect={() => (shareWithFriendOpen = true)}>
            <UsersRound /> Share with a friend…
          </DropdownMenu.Item>
        {/if}
      </DropdownMenu.Group>
      <DropdownMenu.Separator />
      <DropdownMenu.Group>
        {#if destinationFolder}
          <!-- What Re-tag does used to be a hover tooltip; it is the item's second line now. -->
          <DropdownMenu.Item onSelect={retagAlbum} disabled={retagState === 'loading'}>
            {#if retagState === 'loading'}
              <Loader2 class="animate-spin" />
            {:else}
              <RefreshCw />
            {/if}
            <span class="flex min-w-0 flex-col">
              <span>{retagState === 'loading' ? 'Re-tagging…' : 'Re-tag'}</span>
              <span class="text-muted-foreground pointer-coarse:text-footnote text-xs">
                Re-copy and re-tag the files in place
              </span>
            </span>
          </DropdownMenu.Item>
        {/if}
        {#if linkStatus === 'linked'}
          <DropdownMenu.Item onSelect={copyDossier}>
            <Copy /> Copy dossier
          </DropdownMenu.Item>
        {/if}
        {#if destinationFolder}
          <DropdownMenu.Item onSelect={revealInDestination}>
            <HardDrive /> Copy destination path
          </DropdownMenu.Item>
        {/if}
      </DropdownMenu.Group>
      <DropdownMenu.Separator />
      <DropdownMenu.Group>
        <DropdownMenu.Item onSelect={() => (timelineOpen = true)}>
          <History /> Album timeline
        </DropdownMenu.Item>
        {#if missingCount > 0}
          <DropdownMenu.Item onSelect={() => albumViewPrefs.toggleHideMissing()}>
            {#if hideMissing}
              <Eye /> Show {missingCount} missing
            {:else}
              <EyeOff /> Hide {missingCount} missing
            {/if}
            <DropdownMenu.Shortcut>H</DropdownMenu.Shortcut>
          </DropdownMenu.Item>
        {/if}
        {#if linkStatus === 'linked'}
          <DropdownMenu.Item onSelect={gradeNow} disabled={grading}>
            <Sparkles />
            {grading ? 'Grading…' : albumGrade?.graded ? 'Re-grade match' : 'Grade match'}
          </DropdownMenu.Item>
        {/if}
      </DropdownMenu.Group>
    </DropdownMenu.Content>
  </DropdownMenu.Root>
{/snippet}

{#snippet equalizer(playing: boolean, className = '')}
  <span
    class={cn('mh-eq pointer-events-none', playing && 'is-playing', className)}
    aria-hidden="true"
  >
    <i></i><i></i><i></i>
  </span>
{/snippet}

{#if !album}
  <ScrollArea class="min-h-0 flex-1">
    <PageToolbarV2 title="Album" largeTitle={false} back={desktopBack} />
    {#if isLoading}
      <div
        class="text-muted-foreground text-subheadline flex items-center justify-center p-8 md:text-sm"
      >
        Loading album…
      </div>
    {:else}
      <div class="flex flex-col items-center justify-center gap-3 px-8 py-16 text-center">
        <Disc3 class="text-muted-foreground-dim size-10" aria-hidden="true" />
        <p class="text-headline md:text-sm md:font-medium">Album not found in your library.</p>
        <Button variant="gray" href="/library" class="text-primary rounded-full"
          >Back to all albums</Button
        >
      </div>
    {/if}
  </ScrollArea>
{:else}
  <ScrollArea bind:viewportRef={scrollViewport} class="min-h-0 flex-1">
    <!-- No large title: the hero below carries the album's name. The bar's inline title (still
         the page's <h1> for VoiceOver) stays hidden until the hero's heading scrolls under the bar,
         as in Apple Music, so the name never shows twice at rest. -->
    <PageToolbarV2
      title={album.title}
      largeTitle={false}
      collapseAfter={heroTitleEl}
      back={desktopBack}
    >
      {#snippet actions()}
        {#if isOwner}
          {@render albumMenu()}
        {/if}
      {/snippet}
    </PageToolbarV2>

    <!-- Hero. Pulled up under the (transparent at rest) bar so the album's wash starts at the top
         of the page; the bar paints over it. -->
    <div class="album-wash relative isolate -mt-[49px] pt-[57px] md:-mt-12 md:pt-14">
      <div
        aria-hidden="true"
        class="mh-crossfade pointer-events-none absolute inset-0 -z-10 transition-opacity duration-300 ease-out"
        style:background={heroWash}
        style:opacity={heroWash ? 1 : 0}
      ></div>
      <div
        class="flex flex-col items-center px-4 text-center md:flex-row md:items-end md:gap-7 md:px-7 md:text-left"
      >
        <div class="w-60 shrink-0 md:w-[232px]">
          <Cover
            artist={album.artist}
            title={album.title}
            coverUrl={album.coverUrl}
            size={240}
            corner={10}
            dprCap={3}
            class="aspect-square !h-auto !w-full !shadow-[0_12px_32px_rgb(0_0_0/0.28)]"
          />
        </div>
        <div
          class="mt-4 flex w-full min-w-0 flex-1 flex-col items-center md:mt-0 md:w-auto md:items-start"
        >
          <h2
            bind:this={heroTitleEl}
            class="text-title-2 max-w-full text-balance md:text-[28px] md:leading-tight"
          >
            {album.title}
          </h2>
          <!-- A text link grows its hit area to 44pt with a pseudo-element, not with padding. The
               truncation lives on an inner span: `truncate` on the link itself is overflow:hidden,
               which clipped its own pseudo-element back to the 26px line. -->
          <a
            href="/library?artist={encodeURIComponent(album.artist)}"
            class="text-title-3 text-primary relative mt-0.5 flex max-w-full font-normal after:absolute after:inset-x-0 after:-inset-y-2.5 after:content-[''] hover:underline md:text-lg"
          >
            <span class="block truncate">{album.artist}</span>
          </a>
          {#if heroMeta}
            <p class="text-footnote text-muted-foreground mt-1">{heroMeta}</p>
          {/if}
          {#if album.folderKeys.length > 1}
            <p
              class="text-footnote text-muted-foreground mt-0.5"
              title={album.folderKeys.join('\n')}
            >
              {album.folderKeys.length} editions merged
            </p>
          {/if}
          {#if sharedBy}
            <p class="text-footnote text-muted-foreground mt-0.5 inline-flex items-center gap-1">
              <Users class="size-3.5" aria-hidden="true" /> Shared by {sharedBy}
            </p>
          {/if}

          <!-- Play / Shuffle. Phone: two equal gray capsules (tint glyph + label). md+: the same
               treatment as Tracks and an artist — Play the prominent button, Shuffle gray. Play
               resumes the album track that is playing, else starts track 1; its glyph shows the
               live state. -->
          {#if compact}
            <div class="mt-4 flex w-full gap-3">
              <Button variant="gray" size="pill" class="text-primary flex-1" onclick={playAlbumStart}>
                {#if playerStore.isPlaying && currentlyPlaying}
                  <Pause fill="currentColor" /> Pause
                {:else}
                  <Play fill="currentColor" /> Play
                {/if}
              </Button>
              <Button
                variant="gray"
                size="pill"
                class="text-primary flex-1"
                onclick={playAlbumShuffle}
              >
                <Shuffle /> Shuffle
              </Button>
            </div>
          {:else}
            <div class="mt-4 flex gap-2">
              <Button size="sm" class="h-8 gap-1.5 rounded-full px-3" onclick={playAlbumStart}>
                {#if playerStore.isPlaying && currentlyPlaying}
                  <Pause class="size-4" fill="currentColor" /> Pause
                {:else}
                  <Play class="size-4" fill="currentColor" /> Play
                {/if}
              </Button>
              <Button
                variant="gray"
                size="sm"
                class="h-8 gap-1.5 rounded-full px-3"
                onclick={playAlbumShuffle}
              >
                <Shuffle class="size-4" /> Shuffle
              </Button>
            </div>
          {/if}

          {#if isOwner}
            <!-- Admin status row → the album info sheet (link, completeness, AI grade). -->
            <button
              type="button"
              onclick={() => (infoOpen = true)}
              class="bg-secondary hover:bg-secondary-hover text-subheadline focus-visible:ring-ring mt-3 flex min-h-11 w-full max-w-full items-center gap-2 rounded-xl px-3 text-left outline-none focus-visible:ring-2 md:w-auto md:max-w-[min(100%,28rem)] md:text-sm"
            >
              <span
                aria-hidden="true"
                class={cn(
                  'size-2 shrink-0 rounded-full',
                  wrongMatch
                    ? 'bg-destructive'
                    : linkStatus === 'linked'
                      ? 'bg-primary'
                      : linkStatus === 'localOnly'
                        ? 'ring-muted-foreground ring-[1.5px] ring-inset'
                        : 'bg-muted-foreground-dim animate-pulse'
                )}
              ></span>
              <span class="min-w-0 flex-1 truncate">
                {statusLine}{#if wrongMatch}<span class="text-destructive-text">
                    · Likely wrong match</span
                  >{/if}
              </span>
              {#if tracklist?.trackCountContested}
                <TriangleAlert
                  class="text-warning-text size-4 shrink-0"
                  aria-label="Sources disagree on length"
                />
              {/if}
              <ChevronRight class="text-muted-foreground-dim size-4 shrink-0" aria-hidden="true" />
            </button>
          {/if}

          {#if showProvenance}
            <!-- How it got here → the provenance sheet: which tracks came from where, and for an
                 album fill, the tracks you already had that started it. -->
            <button
              type="button"
              onclick={() => (provenanceOpen = true)}
              disabled={!provenance}
              aria-label={provenance?.summary
                ? `How it got here: ${provenance.summary}`
                : undefined}
              class={cn(
                'bg-secondary hover:bg-secondary-hover text-subheadline focus-visible:ring-ring disabled:hover:bg-secondary flex min-h-11 w-full max-w-full items-center gap-2 rounded-xl px-3 text-left outline-none focus-visible:ring-2 md:w-auto md:max-w-[min(100%,28rem)] md:text-sm',
                isOwner ? 'mt-2' : 'mt-3'
              )}
            >
              {#if provenance?.primaryReason}
                {@const ReasonIcon = PROVENANCE_ICON[provenance.primaryReason]}
                <ReasonIcon class="text-muted-foreground size-4 shrink-0" aria-hidden="true" />
              {:else}
                <span
                  aria-hidden="true"
                  class="bg-muted-foreground-dim mx-1 size-2 shrink-0 animate-pulse rounded-full"
                ></span>
              {/if}
              <!-- Two lines rather than an ellipsis: the tail ("· 16 filled in") is the part
                   that answers the question. -->
              <span
                class={cn(
                  'line-clamp-2 min-w-0 flex-1 py-2',
                  !provenance && 'text-muted-foreground'
                )}
              >
                {provenance?.summary ?? 'Tracing how it got here…'}
              </span>
              <ChevronRight class="text-muted-foreground-dim size-4 shrink-0" aria-hidden="true" />
            </button>
          {/if}
        </div>
      </div>
    </div>

    <!-- Track list -->
    {#if compact}
      <ol class="mt-4" aria-label="Tracks">
        {#each visibleRows as row, ri (row.key)}
          {@const numLabel = multiDisc ? `${row.disc}.${row.n}` : String(row.n)}
          {@const separated = ri < visibleRows.length - 1}
          {#if row.kind === 'owned'}
            {@const song = row.song}
            {@const isLoaded = playerStore.currentSong?.id === song.id}
            {@const artist = trackArtist(song)}
            {@const needsReview = mapEnrichmentStatus(song.enrichmentStatus) === 'needsreview'}
            <li
              use:longpress={{ onlongpress: (p) => menus[song.id]?.openAt(p) }}
              oncontextmenu={(e) => onRowContextMenu(e, song.id)}
              class={cn(
                'has-[[data-row-main]:active]:bg-accent relative flex min-h-12 items-center pr-1 transition-colors duration-100',
                separated &&
                  "after:bg-separator after:absolute after:right-0 after:bottom-0 after:left-[60px] after:h-(--hairline) after:content-['']"
              )}
            >
              <button
                type="button"
                data-row-main=""
                onclick={() => onRowClick(song)}
                aria-current={isLoaded ? 'true' : undefined}
                class="focus-visible:ring-ring flex min-h-12 min-w-0 flex-1 items-center gap-3 py-2 pl-4 text-left outline-none focus-visible:ring-2 focus-visible:ring-inset"
              >
                <span
                  class="text-body text-muted-foreground grid w-8 shrink-0 place-items-center tabular-nums"
                >
                  {#if isLoaded}
                    {@render equalizer(playerStore.isPlaying, 'text-primary')}
                  {:else}
                    {numLabel}
                  {/if}
                </span>
                <span class="flex min-w-0 flex-1 flex-col">
                  <!-- The heart LEADS the title here — most album rows have no second line for it
                       to lead, as it does in Tracks (TrackRowText). -->
                  <span class="flex min-w-0 items-center gap-1.5">
                    {#if song.likedAtUtc}
                      <Heart
                        class="text-primary size-3.5 shrink-0"
                        fill="currentColor"
                        role="img"
                        aria-label="In favourites"
                      />
                    {/if}
                    <span class={cn('text-body truncate', isLoaded && 'text-primary')}>
                      {#if isLoaded}<span class="sr-only">Now playing, </span>{/if}{(
                        song.title ?? song.fileName
                      ).trim() || song.fileName}
                    </span>
                  </span>
                  {#if artist || needsReview}
                    <span
                      class="text-subheadline text-muted-foreground flex min-w-0 items-center gap-1.5"
                    >
                      {#if needsReview}<Badge variant="warning" class="shrink-0">Review</Badge>{/if}
                      {#if artist}<span class="truncate">{artist}</span>{/if}
                    </span>
                  {/if}
                  {#if isLoaded}<span class="sr-only">. Opens Now Playing</span>{/if}
                </span>
              </button>
              <TrackRowMenu
                bind:this={() => menus[song.id], (m) => (menus[song.id] = m)}
                {song}
                albumKey={album.key}
                showAlbum={false}
                onplay={() => playFrom(song)}
              />
            </li>
          {:else}
            <!-- A canonical track you're missing: said in words, not only by a dimmer colour. -->
            <li
              class={cn(
                'relative flex min-h-12 items-center gap-3 py-2 pr-1 pl-4',
                separated &&
                  "after:bg-separator after:absolute after:right-0 after:bottom-0 after:left-[60px] after:h-(--hairline) after:content-['']"
              )}
            >
              <span
                class="text-body text-muted-foreground-dim w-8 shrink-0 text-center tabular-nums"
                >{numLabel}</span
              >
              <span class="flex min-w-0 flex-1 flex-col">
                <span class="text-body text-muted-foreground truncate">{row.title}</span>
                <!-- Words, not only a dimmer colour; two lines rather than a cut-off sentence. -->
                <span class="text-subheadline text-muted-foreground-dim line-clamp-2">
                  {row.contested ? 'Bonus? Not on every source' : 'Not in library'}
                </span>
              </span>
              {#if row.canonicalTrackId != null}
                {@const id = row.canonicalTrackId}
                {#if acquired.has(id)}
                  <!-- Done: a resting confirmation, not a dimmed disabled button. -->
                  <span class="text-subheadline inline-flex shrink-0 items-center gap-1 px-1">
                    <CircleCheck class="text-primary size-4" aria-hidden="true" /> Queued
                  </span>
                {:else}
                  <!-- A 28pt bordered capsule with a 44pt hit area. -->
                  <Button
                    variant="bordered"
                    size="sm"
                    disabled={acquiring.has(id)}
                    aria-busy={acquiring.has(id) || undefined}
                    onclick={() => onAcquire(id)}
                    class="relative h-7 shrink-0 rounded-full px-3 after:absolute after:-inset-2 after:content-['']"
                    aria-label={acquiring.has(id) ? `Queueing ${row.title}` : `Get ${row.title}`}
                  >
                    {#if acquiring.has(id)}
                      <Loader2 class="size-3.5 animate-spin" />
                    {:else}
                      Get
                    {/if}
                  </Button>
                {/if}
              {/if}
              <Button
                variant="ghost"
                size="icon"
                href={findUrl(row.title)}
                target="_blank"
                rel="noopener noreferrer"
                aria-label="Find {row.title} on the web"
                class="text-muted-foreground size-11 shrink-0 rounded-full"
              >
                <Search class="size-5" />
              </Button>
            </li>
          {/if}
        {/each}
      </ol>
    {:else}
      <div class="@container px-4 pt-5 pb-2">
        <div
          class={cn(
            'border-border text-muted-foreground grid items-center gap-4 border-b px-3 py-2 text-[11px] font-medium',
            DESKTOP_GRID
          )}
        >
          <span class="text-right">#</span>
          <span>Title</span>
          <span class="hidden @xl:block">Format</span>
          <span class="hidden @3xl:block">Size</span>
          <span class="hidden @3xl:block">Match</span>
          <span class="text-right"
            ><Clock class="-mt-0.5 inline size-3" aria-label="Duration" /></span
          >
          <span aria-hidden="true"></span>
        </div>

        {#each visibleRows as row (row.key)}
          {@const numLabel = multiDisc ? `${row.disc}.${row.n}` : String(row.n)}
          {#if row.kind === 'owned'}
            {@const song = row.song}
            {@const isSelected = selectedTrackId === song.id}
            {@const isCurrentlyLoaded = playerStore.currentSong?.id === song.id}
            {@const isCurrentlyPlaying = isCurrentlyLoaded && playerStore.isPlaying}
            {@const matchValue = trackMatchValue(song)}
            {@const artist = trackArtist(song)}
            <div
              role="button"
              tabindex="0"
              aria-current={isCurrentlyLoaded ? 'true' : undefined}
              onclick={() => selectTrack(song)}
              onkeydown={(e) => onRowKeydown(e, song)}
              oncontextmenu={(e) => onRowContextMenu(e, song.id)}
              use:longpress={{ onlongpress: (p) => menus[song.id]?.openAt(p) }}
              class={cn(
                'group grid cursor-pointer items-center gap-4 rounded-lg px-3 py-1.5 transition-colors',
                DESKTOP_GRID,
                'hover:bg-accent',
                isSelected && 'bg-primary/12 hover:bg-primary/12',
                isCurrentlyLoaded && 'text-primary'
              )}
            >
              <span
                class="text-muted-foreground relative grid h-full place-items-center text-right"
              >
                <!-- Button first so the index/equalizer can hide off its `peer` focus state:
                     exactly one of the two is ever visible. The index/equalizer must stay
                     `pointer-events-none`: once hover fades it to `opacity-0` it becomes a
                     stacking context and paints *over* the absolutely positioned button, so
                     it would otherwise swallow the click — the press and release then resolve
                     to different nodes and the browser retargets `click` to this wrapper,
                     which reads as a row click and opens the detail panel instead of playing. -->
                <button
                  type="button"
                  onclick={(e) => playTrack(song, e)}
                  aria-label={isCurrentlyPlaying ? 'Pause track' : 'Play track'}
                  class={cn(
                    'peer absolute inset-0 grid place-items-center opacity-0 transition-[opacity,scale] duration-100 ease-out group-hover:opacity-100 focus-visible:opacity-100 active:scale-[0.97] pointer-coarse:opacity-100',
                    isCurrentlyLoaded ? 'text-primary' : 'text-foreground'
                  )}
                >
                  {#if isCurrentlyPlaying}
                    <Pause class="size-4" fill="currentColor" />
                  {:else}
                    <Play class="size-4" fill="currentColor" />
                  {/if}
                </button>
                {#if isCurrentlyLoaded}
                  {@render equalizer(
                    isCurrentlyPlaying,
                    'text-primary group-hover:opacity-0 peer-focus-visible:opacity-0 pointer-coarse:opacity-0'
                  )}
                {:else}
                  <span
                    class="pointer-events-none text-sm tabular-nums transition-opacity group-hover:opacity-0 peer-focus-visible:opacity-0 pointer-coarse:opacity-0"
                  >
                    {numLabel}
                  </span>
                {/if}
              </span>

              <div class="min-w-0">
                <div class="flex min-w-0 items-center gap-1.5">
                  {#if song.likedAtUtc}
                    <Heart
                      class="text-primary size-3 shrink-0"
                      fill="currentColor"
                      role="img"
                      aria-label="In favourites"
                    />
                  {/if}
                  <span
                    class={cn('truncate text-sm font-medium', isCurrentlyLoaded && 'text-primary')}
                  >
                    {#if isCurrentlyLoaded}<span class="sr-only">Now playing, </span>{/if}{(
                      song.title ?? song.fileName
                    ).trim() || song.fileName}
                  </span>
                </div>
                <div class="text-muted-foreground mt-0.5 flex items-center gap-2 text-[11.5px]">
                  <span class="truncate">{artist ?? album.artist}</span>
                  {#if mapEnrichmentStatus(song.enrichmentStatus) === 'needsreview'}
                    <Badge
                      variant="warning"
                      class="min-h-0 px-1.5 py-px"
                      title="Enrichment uncertain — needs review">Review</Badge
                    >
                  {/if}
                  <!-- On hover or selection only, as in Tracks: at rest it was on nearly every row. -->
                  {#if song.hasSyncedLyrics || song.lrclibId}
                    <span
                      class={cn(
                        'bg-muted text-foreground rounded px-1 py-px text-[11px] font-semibold opacity-0 transition-opacity group-hover:opacity-100 group-focus-visible:opacity-100',
                        isSelected && 'opacity-100'
                      )}
                    >
                      LRC
                    </span>
                  {/if}
                </div>
              </div>

              <span
                class="text-muted-foreground hidden truncate text-[11px] tabular-nums @xl:block"
              >
                {trackBitrateLabel(song)}
              </span>
              <span class="text-muted-foreground hidden text-[11px] tabular-nums @3xl:block">
                {formatFileSize(song.fileSizeBytes)}
              </span>
              <span class="hidden items-center gap-2 @3xl:flex">
                {#if matchValue != null}
                  <span class="bg-secondary h-1 flex-1 overflow-hidden rounded-full">
                    <span
                      class="bg-muted-foreground block h-full rounded-full"
                      style="width: {matchValue * 100}%;"
                    ></span>
                  </span>
                  <span
                    class="text-muted-foreground min-w-[28px] text-right text-[11px] tabular-nums"
                  >
                    {matchValue.toFixed(2)}
                  </span>
                {:else}
                  <span
                    class="text-muted-foreground flex-1 text-right text-[11px]"
                    title="No match confidence recorded for this track"
                  >
                    —
                  </span>
                {/if}
              </span>
              <span class="text-muted-foreground text-right text-[11px] tabular-nums">
                {formatDuration(row.durationSeconds)}
              </span>

              <TrackRowMenu
                bind:this={() => menus[song.id], (m) => (menus[song.id] = m)}
                {song}
                albumKey={album.key}
                showAlbum={false}
                onplay={() => playFrom(song)}
                class="size-8 opacity-0 group-hover:opacity-100 focus-visible:opacity-100 aria-expanded:opacity-100 pointer-coarse:opacity-100"
              />
            </div>
          {:else}
            <!-- Canonical track the user is missing — greyed out, with a way to go find it. -->
            <div class={cn('grid items-center gap-4 rounded-lg px-3 py-1.5', DESKTOP_GRID)}>
              <span class="text-muted-foreground-dim text-right text-sm tabular-nums"
                >{numLabel}</span
              >
              <div class="min-w-0">
                <div class="flex min-w-0 items-center gap-2">
                  <span class="text-muted-foreground truncate text-sm font-medium">{row.title}</span
                  >
                  <!-- Narrow table: the Missing / Bonus? tag rides with the title (its column is
                       hidden), so the state still reads in words. -->
                  <span
                    class="bg-muted text-foreground shrink-0 rounded px-1.5 py-0.5 text-[11px] font-semibold @3xl:hidden"
                  >
                    {row.contested ? 'Bonus?' : 'Missing'}
                  </span>
                </div>
                <div class="mt-0.5 flex items-center gap-1">
                  {#if row.canonicalTrackId != null}
                    {@const id = row.canonicalTrackId}
                    {#if acquired.has(id)}
                      <span class="inline-flex h-6 items-center gap-1 pr-2 text-xs">
                        <CircleCheck class="text-primary size-3.5" aria-hidden="true" /> Queued
                      </span>
                    {:else}
                      <Button
                        variant="ghost"
                        size="xs"
                        disabled={acquiring.has(id)}
                        aria-busy={acquiring.has(id) || undefined}
                        onclick={() => onAcquire(id)}
                        class="text-muted-foreground hover:text-primary -ml-2 h-6"
                        title="Queue this track for download"
                      >
                        {#if acquiring.has(id)}
                          <Loader2 class="animate-spin" /> Queueing…
                        {:else}
                          <Download /> Get this track
                        {/if}
                      </Button>
                    {/if}
                  {/if}
                  <Button
                    variant="ghost"
                    size="xs"
                    href={findUrl(row.title)}
                    target="_blank"
                    rel="noopener noreferrer"
                    class="text-muted-foreground hover:text-primary h-6"
                  >
                    <Search /> Find this track
                  </Button>
                </div>
              </div>
              <span class="text-muted-foreground-dim hidden text-[11px] @xl:block">—</span>
              <span class="text-muted-foreground-dim hidden text-[11px] @3xl:block">—</span>
              <span class="hidden items-center @3xl:flex">
                <span
                  class="bg-muted text-foreground rounded px-1.5 py-0.5 text-[11px] font-semibold"
                  title={row.contested
                    ? 'Only some providers list this track — it may be a bonus/edition-specific track'
                    : 'Not in your library'}
                >
                  {row.contested ? 'Bonus?' : 'Missing'}
                </span>
              </span>
              <span class="text-muted-foreground-dim text-right text-[11px] tabular-nums">
                {formatDuration(row.durationSeconds)}
              </span>
              <span></span>
            </div>
          {/if}
        {/each}
      </div>
    {/if}

    <!-- Footer: the Apple Music summary line — "6 of 7 tracks · 21 min · 131 MB", the count and
         the completeness in one phrase — then the credits (only the fields that exist). -->
    <p class="text-footnote text-muted-foreground px-4 pt-4 tabular-nums md:px-7">
      {completeness && completeness.total > completeness.owned
        ? `${completeness.owned} of ${completeness.total} tracks`
        : `${album.trackCount} ${album.trackCount === 1 ? 'track' : 'tracks'}`}
      · {formatTotalDuration(album.durationSeconds)} · {formatFileSize(album.byteSize)}
    </p>
    <dl
      class="grid grid-cols-2 gap-x-6 gap-y-3 px-4 pt-4 pb-8 sm:grid-cols-3 md:grid-cols-4 md:px-7"
    >
      <div>
        <dt class="text-footnote text-muted-foreground">Released</dt>
        <dd class="text-subheadline md:text-[13px]">
          {formatReleaseDate(album.releaseDate ?? album.year)}
        </dd>
      </div>
      <div>
        <dt class="text-footnote text-muted-foreground">Genre</dt>
        <dd class="text-subheadline md:text-[13px]">{album.genre ?? '—'}</dd>
      </div>
      {#if album.label}
        <div>
          <dt class="text-footnote text-muted-foreground">Label</dt>
          <dd class="text-subheadline md:text-[13px]">{album.label}</dd>
        </div>
      {/if}
      {#if album.catalogNumber}
        <div>
          <dt class="text-footnote text-muted-foreground">Catalog #</dt>
          <dd class="text-subheadline font-mono md:text-[12px]">{album.catalogNumber}</dd>
        </div>
      {/if}
      {#if album.upc}
        <div>
          <dt class="text-footnote text-muted-foreground">Barcode</dt>
          <dd class="text-subheadline font-mono md:text-[12px]">{album.upc}</dd>
        </div>
      {/if}
      <div class="col-span-2 min-w-0 sm:col-span-1">
        <dt class="text-footnote text-muted-foreground">MusicBrainz ID</dt>
        <dd class="min-w-0">
          {#if album.musicBrainzReleaseId}
            {@const mbid = album.musicBrainzReleaseId}
            <!-- IDs are for copying: tap to copy, the whole line is the target. -->
            <button
              type="button"
              onclick={() => copyText(mbid, 'MusicBrainz ID')}
              class="text-muted-foreground hover:text-foreground relative flex max-w-full items-center gap-1.5 font-mono text-[12px] after:absolute after:inset-x-0 after:-inset-y-3.5 after:content-['']"
              aria-label="Copy MusicBrainz ID {mbid}"
            >
              <span class="truncate">{mbid}</span>
              <Copy class="size-3.5 shrink-0" aria-hidden="true" />
            </button>
          {:else}
            <span class="text-muted-foreground font-mono text-[12px]">—</span>
          {/if}
        </dd>
      </div>
      {#if destinationFolder}
        <div class="col-span-2 min-w-0 sm:col-span-3 md:col-span-4">
          <dt class="text-footnote text-muted-foreground">Destination</dt>
          <dd class="min-w-0">
            <button
              type="button"
              onclick={revealInDestination}
              class="text-muted-foreground hover:text-foreground relative flex max-w-full items-center gap-1.5 text-left font-mono text-[12px] after:absolute after:inset-x-0 after:-inset-y-3.5 after:content-['']"
              aria-label="Copy destination folder path {destinationFolder}"
            >
              <HardDrive class="size-3.5 shrink-0" aria-hidden="true" />
              <span class="break-all">{destinationFolder}</span>
            </button>
          </dd>
        </div>
      {/if}
    </dl>
  </ScrollArea>

  <AlbumTimelineDialog bind:open={timelineOpen} artist={album.artist} album={album.title} />

  <ProvenanceSheet
    bind:open={provenanceOpen}
    {provenance}
    loading={!provenance && !provenanceError}
    error={provenanceError}
    canManage={isOwner}
  />

  {#if isOwner}
    <ShareWithFriendDialog
      bind:open={shareWithFriendOpen}
      artist={album.artist}
      album={album.title}
    />

    <!-- Album info: what the status row summarises. Provider link, completeness, the AI grade
         (tap to re-grade), and the explanations that used to live in hover tooltips. -->
    <BottomSheet.Root bind:open={infoOpen} title="Album info">
      {#snippet trailing()}
        <BottomSheet.Action prominent onclick={() => (infoOpen = false)}>Done</BottomSheet.Action>
      {/snippet}
      <div class="flex flex-col gap-6 pt-2">
        <GroupedList.Section
          header="Match"
          footer={linkStatus === 'localOnly'
            ? 'No matching album was found on any provider — this album is only in your local library.'
            : undefined}
        >
          <GroupedList.Row
            label="Status"
            value={loadingDetail || linkStatus === 'pending'
              ? 'Checking providers…'
              : linkStatus === 'linked'
                ? 'Linked'
                : 'Local only'}
          />
          {#if linkStatus === 'linked' && sourceLabels.length > 0}
            <GroupedList.Row label="Providers" value={sourceLabels.join(', ')} />
          {/if}
          {#if tracklist?.trackCountContested}
            <GroupedList.Row
              icon={TriangleAlert}
              iconClass="bg-warning/15 text-warning-text"
              label="Sources disagree on length"
              sublabel="Providers list different track counts for this album."
            />
          {/if}
          {#if completeness}
            <GroupedList.Row
              label="Complete"
              value="{completeness.owned} of {completeness.total} · {completeness.pct}%"
            >
              <span
                class="bg-secondary mt-1.5 block h-1 w-full max-w-60 overflow-hidden rounded-full"
              >
                <span
                  class="bg-primary block h-full rounded-full"
                  style="width: {completeness.pct}%"
                ></span>
              </span>
            </GroupedList.Row>
          {/if}
        </GroupedList.Section>

        {#if linkStatus === 'linked'}
          <GroupedList.Section
            header="AI grade"
            footer={albumGrade?.graded && albumGrade.summary ? albumGrade.summary : undefined}
          >
            {#if albumGrade?.graded && albumGrade.verdict}
              <GroupedList.Row
                onclick={gradeNow}
                disabled={grading}
                label="Match: {albumGrade.verdict}{albumGrade.score != null
                  ? ` · ${albumGrade.score}`
                  : ''}"
                sublabel={grading ? 'Grading…' : 'Tap to re-grade'}
              >
                {#snippet leading()}
                  <span class="grid size-[29px] place-items-center">
                    <span class={cn('size-2.5 rounded-full', VERDICT_DOT[albumGrade!.verdict!])}
                    ></span>
                  </span>
                {/snippet}
                {#snippet trailing()}
                  {#if grading}<Loader2 class="text-muted-foreground size-4 animate-spin" />{/if}
                {/snippet}
              </GroupedList.Row>
            {:else}
              <GroupedList.Row
                onclick={gradeNow}
                disabled={grading}
                icon={Sparkles}
                label={grading ? 'Grading…' : 'Grade match'}
                sublabel="Ask the AI whether this is the right provider album"
              />
            {/if}
          </GroupedList.Section>
        {/if}
      </div>
    </BottomSheet.Root>
  {/if}
{/if}

<style>
  .album-wash {
    --album-wash: 34%;
  }
  :global(.dark) .album-wash {
    --album-wash: 55%;
  }

  /* Apple-style now-playing equalizer: three bars, animated only while playing. */
  .mh-eq {
    display: inline-flex;
    align-items: flex-end;
    justify-content: center;
    gap: 2px;
    height: 14px;
    transition: opacity 150ms;
  }
  .mh-eq > :global(i) {
    width: 3px;
    height: 35%;
    border-radius: 1px;
    background: currentColor;
  }
  .mh-eq.is-playing > :global(i) {
    animation: mh-eq 0.9s ease-in-out infinite;
  }
  .mh-eq > :global(i:nth-child(1)) {
    animation-delay: -0.5s;
  }
  .mh-eq > :global(i:nth-child(2)) {
    animation-delay: -0.2s;
  }
  .mh-eq > :global(i:nth-child(3)) {
    animation-delay: -0.7s;
  }
  @keyframes mh-eq {
    0%,
    100% {
      height: 30%;
    }
    50% {
      height: 100%;
    }
  }
  /* Belt-and-braces alongside the global app.css reduced-motion guard: the
     equalizer holds a static three-bar pose instead of animating. */
  @media (prefers-reduced-motion: reduce) {
    .mh-eq.is-playing > :global(i) {
      animation: none;
    }
  }
</style>
