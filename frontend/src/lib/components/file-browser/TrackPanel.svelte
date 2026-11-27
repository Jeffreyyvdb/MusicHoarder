<script lang="ts">
  import {
    Airplay,
    ChevronRight,
    Heart,
    Info,
    ListMusic,
    MessageSquareQuote,
    MonitorPlay,
    MonitorSpeaker
  } from '@lucide/svelte';
  import { untrack, type Component, type Snippet } from 'svelte';
  import { MediaQuery } from 'svelte/reactivity';
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { toast } from 'svelte-sonner';
  import { Button } from '#lib/components/ui/button/index.ts';
  import SongTransport from '#lib/components/file-browser/SongTransport.svelte';
  import VideoWatchTab, {
    type VideoHousing
  } from '#lib/components/file-browser/VideoWatchTab.svelte';
  import { skinHousing } from '#lib/hardware/hardware-mode.svelte.ts';
  import NowPlayingInfo from '#lib/components/file-browser/now-playing/NowPlayingInfo.svelte';
  import NowPlayingLayout, {
    type QueuePreviewEntry
  } from '#lib/components/file-browser/now-playing/NowPlayingLayout.svelte';
  import VideoLyricsOverlay from '#lib/components/file-browser/now-playing/VideoLyricsOverlay.svelte';
  import NowPlayingLyrics from '#lib/components/file-browser/now-playing/NowPlayingLyrics.svelte';
  import NowPlayingMoreMenu from '#lib/components/file-browser/now-playing/NowPlayingMoreMenu.svelte';
  import NowPlayingQueue from '#lib/components/file-browser/now-playing/NowPlayingQueue.svelte';
  import ManageVideoSheet from '#lib/components/file-browser/now-playing/ManageVideoSheet.svelte';
  import LyricsCompareSheet from '#lib/components/file-browser/now-playing/LyricsCompareSheet.svelte';
  import ActionSheet from '#lib/components/file-browser/now-playing/ActionSheet.svelte';
  import {
    lyricsAvailability,
    videoAvailability,
    type MediaAvailability
  } from '#lib/components/file-browser/now-playing/media-availability.ts';
  import type { Stage } from '#lib/components/file-browser/now-playing/mode-motion.ts';
  import DevicePicker from '#lib/components/playback-sync/DevicePicker.svelte';
  import { DEVICE_ICONS } from '#lib/components/playback-sync/device-icons.ts';
  import {
    videoProblem,
    videoSyncLabel,
    type SongVideo
  } from '#lib/components/file-browser/now-playing/song-video.svelte.ts';
  import SharedByBadge from '#lib/components/v2/SharedByBadge.svelte';
  import {
    artistLabelForSong,
    coverUrlForSong,
    toPlayerSong,
    type ApiSong,
    type AlbumSummary
  } from '#lib/api-client.ts';
  import { formatDuration, formatFileSize } from '#lib/formatters.ts';
  import { createAiLyrics } from '#lib/lyrics/ai-lyrics.svelte.ts';
  import { createLyricsDoc } from '#lib/lyrics/lyrics-doc.svelte.ts';
  import { lyricsSourceUrl } from '#lib/lyrics-source.ts';
  import { playerStore } from '#lib/stores/player.svelte.ts';
  import { playbackSync } from '#lib/stores/playback-sync.svelte.ts';
  import { songsStore } from '#lib/stores/songs.svelte.ts';
  import { featuresStore } from '#lib/stores/features.svelte.ts';
  import type { DetailMode as RequestedDetailMode } from '#lib/stores/song-detail.svelte.ts';
  import { isAdmin } from '#lib/auth/capabilities.ts';
  import { cn } from '#lib/utils.ts';
  import { DYNAMIC_TYPE_CHANGE, dynamicTypeScale } from '#lib/dynamic-type.ts';

  /**
   * Now Playing's content. One fixed frame whose MIDDLE swaps between modes — `player` (the art
   * and title block), `lyrics`, `video` and `info` — while the transport keeps one home at the
   * bottom (it used to move between tabs, and scroll away on the lyrics tab). Compact (below lg)
   * is the iPhone layout: grabber, ⌄ and ⋯ at the top, the middle, then the scrubber, times,
   * transport and the Lyrics · AirPlay · Video · Info row. lg is two columns: art, title and the
   * transport on the left; lyrics, else the video, info or the compare split on the right — and
   * with neither lyrics nor a video the right column goes and the player grows into the middle. A
   * phone on its side (short and landscape) splits the same way: the middle on the left, the title
   * and transport on the right, since the portrait stack cannot fit in 393pt of height.
   *
   * A browsed song (not the one playing) behaves as it always has: the transport's play queues
   * the list it was opened from (`playFromList`), else the song's album, the scrubber is locked at 0, prev/next are off, lyrics don't track.
   */
  type Props = {
    album: AlbumSummary;
    song: ApiSong;
    trackIndex: number;
    /** The mode an `open()` asked for, if any (a row menu's "Song info" asks for `info`). */
    requestedMode?: RequestedDetailMode;
    /** Changes on every open(), so a repeat request for the song on screen still applies. */
    requestSeq?: number;
    /** Plays the list the song was opened from, from this song (see `DetailOpenOptions.play`). */
    playFromList?: () => void;
    video: SongVideo;
    onClose: () => void;
    onResetEnrichment?: () => void;
    /** Link to the standalone /track/[id] provenance timeline (Enrichment's "View timeline"). */
    timelineHref?: string;
    /** "Share with a friend…": the host closes this overlay and opens the album grant dialog. */
    onShareWithFriend?: () => void;
    /** "Add to playlist…": the host opens the sheet over this overlay. */
    onAddToPlaylist?: () => void;
    /** "Report wrong download…": the host opens the report sheet over this overlay. */
    onReport?: () => void;
    /** Out: Video mode is up, so the host blurs the backdrop's copy of the clip behind it. */
    watching?: boolean;
  };
  let {
    album,
    song,
    trackIndex,
    requestedMode,
    requestSeq = 0,
    playFromList,
    video,
    onClose,
    onResetEnrichment,
    timelineHref,
    onShareWithFriend,
    onAddToPlaylist,
    onReport,
    watching = $bindable(false)
  }: Props = $props();

  // Fingerprint + Enrichment, the AI actions, sharing and video management are owner vocabulary
  // hitting owner-only endpoints. isAdmin is false for Demo too — it sees the member panel here.
  const isOwner = $derived(isAdmin(page.data.user));
  // The two-column layout is genuinely wider work (lyrics beside the art), so it waits for lg;
  // md–lg keeps the phone layout, centred.
  const wide = new MediaQuery('(min-width: 1024px)');

  // ── Lyrics state ────────────────────────────────────────────────────────────
  // The AI subsystem (transcribe/re-sync + pronunciation/translation, the LRCLIB-vs-AI default,
  // which document the viewer shows) lives in ai-lyrics.svelte.ts so its rules are unit-tested;
  // the experimental features are only offered when the provider is configured server-side.
  $effect(() => {
    void featuresStore.ensureLoaded();
  });
  const ai = createAiLyrics({
    song: () => song,
    isOwner: () => isOwner,
    lyricsFeatureEnabled: () => featuresStore.lyricsTranscription,
    translationFeatureEnabled: () => featuresStore.lyricsTranslation
  });
  // Load any existing AI documents when the song changes; the module guards re-entry itself.
  $effect(() => {
    ai.syncToSong();
  });

  // The stored LRCLIB lyrics and the AI transcription, each as a document the viewer, the
  // compare view and the ⋯ menu share — so "Check the timing" works from the artwork view too,
  // and a remounted viewer keeps what was loaded.
  const lrclibDoc = createLyricsDoc({
    songId: () => song.id,
    // The song's own lyrics response, once it lands, is fresher than the library row.
    synced: () => (ai.officialLyrics ? ai.officialLyrics.synced : (song.syncedLyrics ?? undefined)),
    plain: () => (ai.officialLyrics ? ai.officialLyrics.plain : (song.plainLyrics ?? undefined)),
    hasSynced: () =>
      ai.officialLyrics ? Boolean(ai.officialLyrics.synced) : (song.hasSyncedLyrics ?? false),
    hasPlain: () =>
      ai.officialLyrics ? Boolean(ai.officialLyrics.plain) : (song.hasPlainLyrics ?? false),
    status: () => ai.lyricsStatus,
    provenance: () => ai.lrclibProvenance,
    trustProvidedProvenance: true
  });
  const aiDoc = createLyricsDoc({
    songId: () => song.id,
    // A fresh transcription replaces the text wholesale for the same song.
    key: () =>
      `${song.id}:${ai.transcription?.at ?? ''}:${ai.transcription?.synced?.length ?? 0}:${ai.transcription?.plain?.length ?? 0}`,
    synced: () => ai.transcription?.synced,
    plain: () => ai.transcription?.plain,
    hasSynced: () => Boolean(ai.transcription?.synced),
    hasPlain: () => Boolean(ai.transcription?.plain),
    status: () => 'Fetched',
    provenance: () => ai.aiProvenance,
    trustProvidedProvenance: true,
    fetchable: false
  });
  $effect(() => lrclibDoc.syncToSong());
  $effect(() => lrclibDoc.ensureLoaded());
  $effect(() => aiDoc.syncToSong());
  const viewerDoc = $derived(ai.showAiInViewer ? aiDoc : lrclibDoc);

  // ── Mode ────────────────────────────────────────────────────────────────────
  type DetailMode = RequestedDetailMode | 'lyrics-video' | 'artwork';
  let mode = $state<DetailMode>('player');

  const lyrics = $derived(
    lyricsAvailability({
      hasLyrics: viewerDoc.hasAny,
      busy: ai.enhanceBusy,
      instrumental: ai.confirmedInstrumental,
      canRecheck: isOwner,
      canEnhance: ai.canEnhance
    })
  );
  const videoState = $derived(
    videoAvailability({
      playable: video.playable,
      settled: video.settled && video.songId === song.id,
      canManage: isOwner
    })
  );
  // The reason a toggle is disabled, for its tooltip and accessible name.
  const lyricsWhy = $derived(ai.isInstrumental ? 'instrumental' : 'none for this song');
  // The panel mounts on each open, so it starts on the artwork. A request that names a mode (a row
  // menu's "Song info") switches to it. One that names none is the host following playback to the
  // next song: the view the listener picked stays, except Lyrics for a song that has none.
  let appliedSeq: number | null = null;
  let appliedSongId: number | null = null;
  $effect(() => {
    const seq = requestSeq;
    const id = song.id;
    if (appliedSeq === seq && appliedSongId === id) return;
    const freshRequest = appliedSeq !== seq;
    appliedSeq = seq;
    appliedSongId = id;
    if (freshRequest && requestedMode) mode = requestedMode;
    else if (untrack(() => lyrics) !== 'ready') {
      const current = untrack(() => mode);
      if (current === 'lyrics') mode = 'player';
      else if (current === 'lyrics-video') mode = 'video';
    }
  });

  // The Queue is the queue loaded on this device: offered while there is one, and left (back to the
  // artwork) when there no longer is — the music stopped, or the session went to another device.
  const queueAvailable = $derived(playerStore.queueEditable);
  $effect(() => {
    if (mode === 'queue' && !queueAvailable) mode = 'player';
  });

  // Video mode needs a watchable clip; when it turns out there is none (or it disappears), fall
  // back rather than show an empty frame.
  $effect(() => {
    if (
      (mode === 'video' || mode === 'lyrics-video') &&
      video.settled &&
      video.songId === song.id &&
      !video.playable
    ) {
      mode = mode === 'lyrics-video' ? 'lyrics' : 'player';
    }
  });

  // The video's answer arrives after each song change; until it does, the last one stands, so
  // following playback through songs that all have one does not flash the lone player between them.
  let videoByDefault = $state(false);
  $effect(() => {
    if (video.settled) videoByDefault = video.playable;
  });

  // What the middle (compact) / right column (lg) shows. At lg the art is always on the left, so
  // "player" means the song's lyrics there, else its video. Info is only ever asked for: a song
  // with neither keeps the player alone, centred and larger (`playerOnly`).
  const content = $derived<DetailMode>(
    mode === 'artwork'
      ? 'player'
      : wide.current && mode === 'player'
        ? ai.hasLyrics
          ? 'lyrics'
          : videoByDefault
            ? 'video'
            : 'player'
        : mode
  );
  const stage = $derived<Stage>(
    content === 'player' || content === 'artwork'
      ? null
      : content === 'lyrics-video'
        ? 'video'
        : content
  );

  // Behind the watch view the backdrop plays the same clip; sharp, it reads as the video twice.
  $effect(() => {
    watching = (content === 'video' || content === 'lyrics-video') && video.playable;
  });
  $effect(() => () => {
    watching = false;
  });

  // An explicit artwork choice bypasses the desktop's automatic lyrics/video default.
  function toggleMode(target: DetailMode) {
    if (target === 'lyrics' || target === 'video') {
      if (content === 'lyrics-video') mode = target === 'lyrics' ? 'video' : 'lyrics';
      else if (
        (target === 'lyrics' && content === 'video') ||
        (target === 'video' && content === 'lyrics')
      )
        mode = 'lyrics-video';
      else mode = content === target ? 'artwork' : target;
    } else mode = content === target ? 'player' : target;
  }

  // A toggle that is on always turns off, whatever the song has.
  function pressLyrics() {
    if (lyrics === 'ready' || content === 'lyrics' || content === 'lyrics-video')
      toggleMode('lyrics');
    else if (lyrics === 'offer') lyricsOfferOpen = true;
  }

  function pressVideo() {
    if (videoState === 'ready' || content === 'video' || content === 'lyrics-video')
      toggleMode('video');
    else if (videoState === 'offer') videoOfferOpen = true;
  }

  async function recheckLyrics() {
    const id = song.id;
    await lrclibDoc.recheck();
    if (song.id !== id) return;
    if (lrclibDoc.hasAny) toggleMode('lyrics');
    else toast('LRCLIB still has nothing for this track');
  }

  function findVideo() {
    void video.fetch();
    toast('Looking for a video — it shows up when it’s ready');
  }

  function showLyrics() {
    if (content !== 'lyrics') mode = 'lyrics';
  }

  // Compare: a split in the lg column; a nested sheet on a phone — mounted here, outside the mode
  // content, so it opens from the artwork or Info as well as from Lyrics.
  let compareOpen = $state(false);
  function openCompare() {
    if (wide.current) {
      if (!ai.comparing) ai.toggleCompare();
      showLyrics();
    } else {
      compareOpen = true;
    }
  }

  // "Improve with AI…" starts a paid AI job, so it asks first, with the explanation the desktop
  // control bar used to carry for this particular track.
  let aiConfirmOpen = $state(false);
  const aiHint = $derived(
    ai.canTranscribe && ai.canTranslate
      ? ai.hasLyrics
        ? 'Re-sync these lyrics to the audio and add a pronunciation guide + translation — one go.'
        : 'Transcribe the audio and add a pronunciation guide + translation — one go.'
      : ai.canTranscribe
        ? 'Transcribe the audio with AI to compare against LRCLIB.'
        : 'Generate a pronunciation guide + English translation to sing along.'
  );
  function startEnhance() {
    showLyrics();
    void ai.enhance();
  }

  // Sheets about one song must not carry over to the next (follow-playback re-targets the panel
  // while it is open).
  $effect(() => {
    void song.id;
    untrack(() => {
      compareOpen = false;
      aiConfirmOpen = false;
      lyricsOfferOpen = false;
      videoOfferOpen = false;
    });
  });
  $effect(() => {
    // The split only exists at lg; don't leave it latched on (it suppresses the stacked
    // translation) after the window narrows.
    if (!wide.current && ai.showCompare) ai.toggleCompare();
  });

  let manageVideoOpen = $state(false);
  let lyricsOfferOpen = $state(false);
  let videoOfferOpen = $state(false);

  const lyricsOfferDescription = $derived(
    lrclibDoc.status === 'NotFound'
      ? 'No lyrics found for this track.'
      : lrclibDoc.status === 'Failed'
        ? 'The last lyrics fetch failed.'
        : 'Lyrics are fetched automatically after a metadata match.'
  );
  const lyricsOfferActions = $derived([
    { label: 'Check LRCLIB again', onAction: () => void recheckLyrics() },
    // Picking it here is the confirmation: a second sheet would ask the same question again.
    ...(ai.canEnhance ? [{ label: ai.enhanceLabel, onAction: startEnhance }] : [])
  ]);
  const videoOfferTitle = $derived(
    videoProblem(video.info, video.infoUnavailable)?.text ?? 'No music video for this song'
  );
  const videoOfferActions = $derived([
    ...(video.info?.status === 'Fetching'
      ? []
      : [{ label: 'Find one automatically', onAction: findVideo }]),
    { label: 'Choose a video…', onAction: () => (manageVideoOpen = true) }
  ]);

  // ── Song facts ──────────────────────────────────────────────────────────────
  const isCurrentlyLoaded = $derived(playerStore.currentSong?.id === song.id);
  const playbackSource = $derived(isCurrentlyLoaded ? playerStore.playbackSource : null);
  const isCurrentlyPlaying = $derived(isCurrentlyLoaded && playerStore.isPlaying);
  // The account's session, when it plays on another device (or is only remembered): the transport
  // steers it there, a line under the toggle row says where, and this device's volume — which is
  // not what anyone is hearing — steps out of the way. The Devices button shows whenever the
  // feature is on (signed in, not the demo), with or without another device open — the rule on
  // both clients; alone, its picker says where other devices come from. (The mini player is the
  // quiet one: nothing about devices there while the music is here and no other device is open.)
  const elsewhere = $derived(isCurrentlyLoaded && playbackSync.showsSession);
  const deviceLine = $derived(isCurrentlyLoaded ? playbackSync.line : null);
  const showDevices = $derived(playbackSync.enabled);
  // The art rests at full size for a browsed song (nothing is paused — it just isn't playing).
  const artPlaying = $derived(!isCurrentlyLoaded || playerStore.isPlaying);

  const trackTitle = $derived((song.title ?? song.fileName).trim() || song.fileName);
  const trackArtist = $derived((song.artist ?? album.artist).trim() || album.artist);
  // Deep-links into the Library, filtered to this track's artist / album. Match the grouping keys
  // the Library views use (artist = albumArtist ?? artist, album = the canonical AlbumSummary key)
  // so the target page is populated.
  const artistHref = $derived(`/library?artist=${encodeURIComponent(artistLabelForSong(song))}`);
  const albumHref = $derived(`/library?album=${encodeURIComponent(album.key)}`);
  const coverUrl = $derived(coverUrlForSong(song) ?? album.coverUrl ?? null);
  const lrclibUrl = $derived(
    lyricsSourceUrl(lrclibDoc.origin, lrclibDoc.geniusSongId, trackArtist, trackTitle)
  );

  // Quality, three ways: the capsule between the times ("FLAC", "MP3 320" — lossless formats
  // don't need a bitrate), the Info row, and the lg meta line.
  const LOSSLESS = new Set(['FLAC', 'ALAC', 'WAV', 'AIFF', 'AIF', 'APE', 'WV']);
  const ext = $derived((song.extension ?? '').replace(/^\./, '').toUpperCase());
  const formatCapsule = $derived(
    ext ? (LOSSLESS.has(ext) || !song.bitRate ? ext : `${ext} ${song.bitRate}`) : null
  );
  const formatLabel = $derived(
    ext ? (song.bitRate && song.bitRate > 0 ? `${ext} ${song.bitRate} kbps` : ext) : '—'
  );
  // Past 1.2× Dynamic Type the title may take a second line: a one-line title at that size cut
  // "Satellites Over Lisbon" to "Satellites Ove…", and the HIG asks to truncate less as text grows.
  let largeType = $state(dynamicTypeScale() > 1.2);
  $effect(() => {
    const onChange = () => (largeType = dynamicTypeScale() > 1.2);
    window.addEventListener(DYNAMIC_TYPE_CHANGE, onChange);
    return () => window.removeEventListener(DYNAMIC_TYPE_CHANGE, onChange);
  });

  const metaLine = $derived(
    [
      ext ? (song.bitRate ? `${ext} ${song.bitRate}` : ext) : null,
      formatDuration(song.durationSeconds),
      formatFileSize(song.fileSizeBytes),
      song.hasSyncedLyrics || song.lrclibId ? 'LRC' : null
    ]
      .filter((part) => part && part !== '—')
      .join(' · ')
  );

  const isLiked = $derived(Boolean(song.likedAtUtc));
  async function toggleLike() {
    try {
      await songsStore.toggleLike(song.id);
    } catch (err) {
      toast.error('Could not update favourites', {
        description: err instanceof Error ? err.message : undefined
      });
    }
  }

  function handlePlayToggle() {
    if (isCurrentlyLoaded) {
      playerStore.togglePlay();
      return;
    }
    if (playFromList) {
      playFromList();
      return;
    }
    const queue = album.songs.map((s) => toPlayerSong(s, album.artist));
    void playerStore.playSong(toPlayerSong(song, album.artist), queue, trackIndex, {
      label: album.title,
      href: albumHref
    });
  }

  function go(href: string) {
    onClose();
    void goto(href);
  }

  function hidePlayer() {
    playerStore.dismissMiniPlayer();
    onClose();
  }

  const videoLine = $derived.by(() => {
    const problem = videoProblem(video.info, video.infoUnavailable);
    if (problem) return problem;
    if (isOwner && video.playable)
      return { text: videoSyncLabel(video.info), tone: 'muted' as const };
    return null;
  });

  const queuePreview = $derived<QueuePreviewEntry[]>(
    isCurrentlyLoaded && queueAvailable
      ? playerStore.upNext.slice(0, wide.current ? 5 : 3).map((entry, index) => ({
          id: entry.id,
          title: entry.title,
          artist: entry.artist,
          durationSeconds: songsStore.songsById.get(entry.id)?.durationSeconds,
          onSelect: () => playerStore.skipToInQueue(index)
        }))
      : []
  );
</script>

<!-- `relative z-10` on the top bar's controls: the grabber's 44pt hit strip reaches down into the
     bar, and must only take the taps that land where nothing else is. -->
{#snippet heart(size: 'lg' | 'md')}
  <Button
    variant="ghost"
    size="icon"
    onclick={toggleLike}
    class="focus-visible:ring-ring relative z-10 size-11 shrink-0 rounded-full hover:bg-transparent focus-visible:ring-2 active:scale-90"
    aria-label={isLiked ? 'Remove from favourites' : 'Add to favourites'}
    aria-pressed={isLiked}
    title={isLiked ? 'Remove from favourites' : 'Add to favourites'}
  >
    <Heart
      class={cn(
        size === 'lg' ? 'size-6' : 'size-5',
        isLiked ? 'text-[var(--np-liked,var(--primary))]' : 'text-foreground'
      )}
      fill={isLiked ? 'currentColor' : 'none'}
    />
  </Button>
{/snippet}

{#snippet moreMenu()}
  <NowPlayingMoreMenu
    {song}
    {isOwner}
    isCurrent={isCurrentlyLoaded}
    {ai}
    {viewerDoc}
    {lrclibDoc}
    {video}
    {lrclibUrl}
    {albumHref}
    {artistHref}
    onGo={go}
    onShowLyrics={showLyrics}
    onCompare={openCompare}
    onEnhance={() => (aiConfirmOpen = true)}
    onManageVideo={() => (manageVideoOpen = true)}
    onShareWithFriend={() => onShareWithFriend?.()}
    onAddToPlaylist={() => onAddToPlaylist?.()}
    onReport={onReport ? () => onReport() : undefined}
    onHidePlayer={hidePlayer}
    class="focus-visible:ring-ring relative z-10 focus-visible:ring-2"
  />
{/snippet}

<!-- Title (one line), the heart, "Artist — Album" as two links, then the Shared-by and AI chips.
     The links are one line of body text (a 20px inline box); an after: strip grows each to 44pt
     without moving anything, and the line's own padding (cancelled by a negative margin) keeps the
     strips inside its truncation clip. -->
{#snippet titleBlock(withHeart = true)}
  <div class="flex min-w-0 items-center gap-2">
    <div class="min-w-0 flex-1">
      <h2 class={cn('text-title-2', largeType ? 'line-clamp-2' : 'truncate')} title={trackTitle}>
        {trackTitle}
      </h2>
      <p class="text-body text-muted-foreground -my-3 truncate py-3">
        <a
          href={artistHref}
          onclick={onClose}
          class="focus-visible:ring-ring pointer-fine:hover:text-foreground relative rounded-sm after:absolute after:inset-x-0 after:-inset-y-3 focus-visible:ring-2 focus-visible:outline-hidden pointer-fine:hover:underline"
          >{trackArtist}</a
        >
        <span aria-hidden="true"> — </span>
        <a
          href={albumHref}
          onclick={onClose}
          class="focus-visible:ring-ring pointer-fine:hover:text-foreground relative rounded-sm after:absolute after:inset-x-0 after:-inset-y-3 focus-visible:ring-2 focus-visible:outline-hidden pointer-fine:hover:underline"
          >{album.title}</a
        >
      </p>
    </div>
    {#if withHeart}{@render heart('lg')}{/if}
  </div>
  {#if song.sharedByUserId}
    <div class="mt-2 flex flex-wrap items-center gap-2">
      <SharedByBadge {song} />
    </div>
  {/if}
{/snippet}

<!-- Lyrics and video can stay pressed together. -->
{#snippet modeButton(
  target: DetailMode,
  label: string,
  Icon: Component<{ class?: string }>,
  availability: MediaAvailability = 'ready',
  why = '',
  onpress: () => void = () => toggleMode(target)
)}
  {@const mediaToggle = target === 'lyrics' || target === 'video'}
  {@const active = content === target || (content === 'lyrics-video' && mediaToggle)}
  {@const none = availability === 'none' && !active}
  {@const name = none ? `${label} — ${why}` : label}
  <button
    type="button"
    class={cn(
      'focus-visible:ring-ring flex size-11 items-center justify-center rounded-full transition-colors focus-visible:ring-2 focus-visible:outline-hidden',
      none
        ? 'text-muted-foreground-dim'
        : active
          ? 'text-foreground'
          : 'text-muted-foreground pointer-fine:hover:text-foreground'
    )}
    aria-pressed={mediaToggle || !wide.current ? active : undefined}
    aria-current={!mediaToggle && wide.current && active ? 'true' : undefined}
    aria-label={name}
    title={name}
    disabled={none}
    onclick={onpress}
  >
    <span
      class={cn(
        'flex h-8 w-11 items-center justify-center rounded-full transition-colors duration-200',
        active && 'bg-[var(--np-fill)]'
      )}
    >
      <Icon class="size-[22px]" />
    </span>
  </button>
{/snippet}

<!-- Lyrics · AirPlay (only where Safari reports a receiver) · Devices (signed in, not the demo) ·
     Video (when watchable) · Info · Queue (while a queue is loaded here). While the music plays on
     another device, the line under the row says where, and opens the same picker. -->
{#snippet bottomRow()}
  <div class="flex items-center justify-evenly" data-mh-rocker>
    {@render modeButton('lyrics', 'Lyrics', MessageSquareQuote, lyrics, lyricsWhy, pressLyrics)}
    {#if playerStore.airPlayAvailable && isCurrentlyLoaded}
      <button
        type="button"
        class="text-muted-foreground focus-visible:ring-ring flex size-11 items-center justify-center rounded-full focus-visible:ring-2 focus-visible:outline-hidden"
        aria-label="AirPlay"
        title="AirPlay"
        onclick={() => playerStore.showAirPlayPicker()}
      >
        <Airplay class="size-[22px]" />
      </button>
    {/if}
    {#if showDevices}
      <DevicePicker nested>
        {#snippet trigger(props)}
          <button
            {...props}
            type="button"
            class={cn(
              'focus-visible:ring-ring flex size-11 items-center justify-center rounded-full transition-colors focus-visible:ring-2 focus-visible:outline-hidden',
              elsewhere
                ? 'text-primary'
                : 'text-muted-foreground pointer-fine:hover:text-foreground'
            )}
            aria-label={deviceLine ? `Devices. ${deviceLine}` : 'Devices'}
            title="Devices"
          >
            <!-- Lit like a selected mode while the music is on another device. -->
            <span
              class={cn(
                'flex h-8 w-11 items-center justify-center rounded-full transition-colors duration-200',
                elsewhere && 'bg-[var(--np-fill)]'
              )}
            >
              <MonitorSpeaker class="size-[22px]" />
            </span>
          </button>
        {/snippet}
      </DevicePicker>
    {/if}
    {@render modeButton(
      'video',
      'Video',
      MonitorPlay,
      videoState,
      'none for this song',
      pressVideo
    )}
    {@render modeButton('info', 'Info', Info)}
    {#if queueAvailable}
      {@render modeButton('queue', 'Queue', ListMusic)}
    {/if}
  </div>
  {#if deviceLine}
    {@const DeviceIcon = DEVICE_ICONS[playbackSync.activeKind]}
    <DevicePicker nested>
      {#snippet trigger(props)}
        <!-- 32pt tall (above the 28pt floor) so the footer does not grow by a full 44. -->
        <button
          {...props}
          type="button"
          class={cn(
            'text-footnote focus-visible:ring-ring mx-auto flex h-8 max-w-full items-center gap-1.5 rounded-full px-3 focus-visible:ring-2 focus-visible:outline-hidden',
            playbackSync.isRemote ? 'text-primary' : 'text-muted-foreground'
          )}
          aria-label={`${deviceLine}. Choose a device`}
        >
          <DeviceIcon class="size-4 shrink-0" />
          <span class="truncate">{deviceLine}</span>
        </button>
      {/snippet}
    </DevicePicker>
  {/if}
{/snippet}

<!-- At lg the meta line above the scrubber already reads "FLAC 1411 · 3:06 · …", so the format
     capsule would say it twice; only the speed capsule (when not 1×) stays under the bar there. -->
{#snippet transport(layout?: Snippet<[Snippet, Snippet, Snippet]>)}
  <SongTransport
    isActive={isCurrentlyLoaded}
    isPlaying={isCurrentlyPlaying}
    fallbackDuration={song.durationSeconds ?? 0}
    onPlayToggle={handlePlayToggle}
    format={wide.current ? null : formatCapsule}
    {layout}
    media
    shuffle
  />
{/snippet}

{#snippet consoleLike()}
  {@render heart('lg')}
  <small aria-hidden="true">Like</small>
{/snippet}

{#snippet condensedRight()}
  {@render heart('md')}
{/snippet}

{#snippet videoCaption()}
  <p
    class={cn(
      'text-footnote text-center',
      videoLine?.tone === 'destructive' ? 'text-destructive-text' : 'text-muted-foreground'
    )}
  >
    {videoLine?.text}
  </p>
{/snippet}

{#snippet videoLyrics()}
  <VideoLyricsOverlay
    doc={viewerDoc}
    currentTimeMs={isCurrentlyLoaded ? playerStore.currentTime * 1000 : null}
    secondarySynced={ai.secondarySynced}
    secondaryPlain={ai.secondaryPlain}
  />
{/snippet}

{#snippet videoHousing(housing: VideoHousing)}
  {#await import('#lib/hardware/HardwareVideo.svelte') then { default: HardwareVideo }}
    <HardwareVideo {housing} status={videoLine} />
  {:catch}
    {@render housing.screen()}
    {#if videoLine}{@render videoCaption()}{/if}
  {/await}
{/snippet}

<!-- The swapping region: the compact middle, or the lg right column. Info stays mounted (hidden)
     so its lazy loads settle once per song however often the mode changes. -->
{#snippet modeContent()}
  {@const monitor = skinHousing(videoHousing)}
  {#if content === 'lyrics'}
    <NowPlayingLyrics
      {ai}
      {song}
      {lrclibDoc}
      {aiDoc}
      {isOwner}
      {isCurrentlyLoaded}
      wide={wide.current}
      {lrclibUrl}
    />
  {:else if (content === 'video' || content === 'lyrics-video') && video.playable}
    <!-- The whole middle (the whole column at lg) is the clip's stage: the watch view fits the
         picture into it, with the status line right under the picture. -->
    <div class={cn('flex min-h-0 flex-1 flex-col', !wide.current && 'px-4 py-3')}>
      <VideoWatchTab
        songId={song.id}
        offsetMs={video.offsetMs}
        generation={video.generation}
        title={trackTitle}
        artist={trackArtist}
        fallbackDuration={song.durationSeconds ?? 0}
        caption={videoLine ? videoCaption : undefined}
        housing={monitor}
        lyricsVisible={content === 'lyrics-video'}
        lyricsAvailability={lyrics}
        onToggleLyrics={pressLyrics}
        lyricsOverlay={videoLyrics}
        onPlayRequest={handlePlayToggle}
      />
    </div>
  {:else if content === 'queue'}
    <NowPlayingQueue wide={wide.current} />
  {/if}
  <div class={cn('flex min-h-0 flex-1 flex-col', content !== 'info' && 'hidden')}>
    <NowPlayingInfo
      {album}
      {song}
      {trackIndex}
      active={content === 'info'}
      {isOwner}
      {trackTitle}
      {trackArtist}
      {artistHref}
      {albumHref}
      lyricsStatus={ai.lyricsStatus}
      lyricsOrigin={lrclibDoc.origin}
      geniusSongId={lrclibDoc.geniusSongId}
      {formatLabel}
      onNavigate={onClose}
      {onResetEnrichment}
      {timelineHref}
    />
  </div>
{/snippet}

{#snippet sourceLink()}
  {#if playbackSource}
    <div class="flex min-w-0 flex-1 justify-center">
      <a
        href={playbackSource.href}
        onclick={onClose}
        data-drag-ignore
        class="text-subheadline focus-visible:ring-ring inline-flex min-h-11 min-w-0 items-center gap-1 rounded-lg px-3 font-semibold focus-visible:ring-2 focus-visible:outline-hidden pointer-fine:hover:underline"
        aria-label={`Started from ${playbackSource.label}. Open source`}
      >
        <span class="truncate">{playbackSource.label}</span>
        <ChevronRight class="text-muted-foreground size-3.5 shrink-0" />
      </a>
    </div>
  {:else}
    <div class="flex-1"></div>
  {/if}
{/snippet}

<NowPlayingLayout
  wide={wide.current}
  {stage}
  {trackTitle}
  {trackArtist}
  albumTitle={album.title}
  {coverUrl}
  {isCurrentlyLoaded}
  {isCurrentlyPlaying}
  {artPlaying}
  durationSeconds={song.durationSeconds}
  {metaLine}
  {elsewhere}
  showQueuePreview={isCurrentlyLoaded}
  {queuePreview}
  {onClose}
  onShowArtwork={() => (mode = 'player')}
  headerCenter={sourceLink}
  headerRight={moreMenu}
  {condensedRight}
  {titleBlock}
  {consoleLike}
  {transport}
  {bottomRow}
  {modeContent}
/>

{#if !wide.current && ai.canCompare}
  <LyricsCompareSheet
    bind:open={compareOpen}
    {ai}
    songId={song.id}
    {lrclibDoc}
    {aiDoc}
    {isCurrentlyLoaded}
    {lrclibUrl}
  />
{/if}

{#if ai.canEnhance}
  <ActionSheet
    bind:open={aiConfirmOpen}
    title={`${ai.enhanceLabel}?`}
    description={aiHint}
    actions={[{ label: ai.enhanceLabel, onAction: startEnhance }]}
  />
{/if}

{#if isOwner}
  <ActionSheet
    bind:open={lyricsOfferOpen}
    title="No lyrics for this song"
    description={lyricsOfferDescription}
    actions={lyricsOfferActions}
  />
  <ActionSheet
    bind:open={videoOfferOpen}
    title={videoOfferTitle}
    description="Find one automatically, or pick from the search results."
    actions={videoOfferActions}
  />
  <ManageVideoSheet bind:open={manageVideoOpen} {video} title={`${trackTitle} — ${trackArtist}`} />
{/if}
