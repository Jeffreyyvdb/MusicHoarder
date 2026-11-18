<script lang="ts">
  import {
    Airplay,
    ChevronDown,
    Heart,
    Info,
    ListMusic,
    MessageSquareQuote,
    MonitorPlay,
    MonitorSpeaker
  } from '@lucide/svelte';
  import { untrack, type Component } from 'svelte';
  import { MediaQuery } from 'svelte/reactivity';
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { toast } from 'svelte-sonner';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import SongTransport from '#lib/components/file-browser/SongTransport.svelte';
  import VideoWatchTab, {
    type VideoHousing
  } from '#lib/components/file-browser/VideoWatchTab.svelte';
  import AiLyricsBadge, { AI_LYRICS_COPY } from '#lib/components/file-browser/AiLyricsBadge.svelte';
  import NowPlayingArt from '#lib/components/file-browser/now-playing/NowPlayingArt.svelte';
  import { hardwareMode } from '#lib/hardware/hardware-mode.svelte.ts';
  import NowPlayingInfo from '#lib/components/file-browser/now-playing/NowPlayingInfo.svelte';
  import NowPlayingLyrics from '#lib/components/file-browser/now-playing/NowPlayingLyrics.svelte';
  import NowPlayingMoreMenu from '#lib/components/file-browser/now-playing/NowPlayingMoreMenu.svelte';
  import NowPlayingQueue from '#lib/components/file-browser/now-playing/NowPlayingQueue.svelte';
  import ManageVideoSheet from '#lib/components/file-browser/now-playing/ManageVideoSheet.svelte';
  import LyricsCompareSheet from '#lib/components/file-browser/now-playing/LyricsCompareSheet.svelte';
  import ActionSheet from '#lib/components/file-browser/now-playing/ActionSheet.svelte';
  import VolumeControl from '#lib/components/file-browser/now-playing/VolumeControl.svelte';
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
  import { lrclibWebUrl } from '#lib/lrclib-url.ts';
  import { playerStore } from '#lib/stores/player.svelte.ts';
  import { playbackSync } from '#lib/stores/playback-sync.svelte.ts';
  import { songsStore } from '#lib/stores/songs.svelte.ts';
  import { featuresStore } from '#lib/stores/features.svelte.ts';
  import type { DetailMode } from '#lib/stores/song-detail.svelte.ts';
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
   * the song's album, the scrubber is locked at 0, prev/next are off, lyrics don't track.
   */
  type Props = {
    album: AlbumSummary;
    song: ApiSong;
    trackIndex: number;
    /** The mode an `open()` asked for, if any (a row menu's "Song info" asks for `info`). */
    requestedMode?: DetailMode;
    /** Changes on every open(), so a repeat request for the song on screen still applies. */
    requestSeq?: number;
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
  // A phone in landscape: the manifest does not lock orientation, and the portrait stack (art,
  // title, a 190pt transport) overflows 393pt of height.
  const sideways = new MediaQuery('(orientation: landscape) and (max-height: 500px)');

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
    synced: () => song.syncedLyrics ?? undefined,
    plain: () => song.plainLyrics ?? undefined,
    hasSynced: () => song.hasSyncedLyrics ?? false,
    hasPlain: () => song.hasPlainLyrics ?? false,
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
  let mode = $state<DetailMode>('player');
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
    else if (untrack(() => mode) === 'lyrics' && !ai.hasLyrics) mode = 'player';
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
    if (mode === 'video' && video.settled && video.songId === song.id && !video.playable) {
      mode = 'player';
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
    wide.current && mode === 'player'
      ? ai.hasLyrics
        ? 'lyrics'
        : videoByDefault
          ? 'video'
          : 'player'
      : mode
  );
  const playerOnly = $derived(wide.current && content === 'player');

  // Behind the watch view the backdrop plays the same clip; sharp, it reads as the video twice.
  $effect(() => {
    watching = content === 'video' && video.playable;
  });
  $effect(() => () => {
    watching = false;
  });

  // Each mode button toggles, and releasing it goes back to the artwork — at lg, to what the right
  // column shows by default, so releasing the default itself changes nothing.
  function toggleMode(target: DetailMode) {
    mode = content === target ? 'player' : target;
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
    });
  });
  $effect(() => {
    // The split only exists at lg; don't leave it latched on (it suppresses the stacked
    // translation) after the window narrows.
    if (!wide.current && ai.showCompare) ai.toggleCompare();
  });

  let manageVideoOpen = $state(false);

  // ── Song facts ──────────────────────────────────────────────────────────────
  const isCurrentlyLoaded = $derived(playerStore.currentSong?.id === song.id);
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
  // The hardware skin (an easter egg, #lib/hardware): the loaded song's art, title and transport
  // become the MH-1 device. Not for a song that is only being looked at, nor while the music plays
  // on another device — the device's keys act on the music here.
  const hardware = $derived(hardwareMode.enabled && isCurrentlyLoaded && !elsewhere);

  const trackTitle = $derived((song.title ?? song.fileName).trim() || song.fileName);
  const trackArtist = $derived((song.artist ?? album.artist).trim() || album.artist);
  // Deep-links into the Library, filtered to this track's artist / album. Match the grouping keys
  // the Library views use (artist = albumArtist ?? artist, album = the canonical AlbumSummary key)
  // so the target page is populated.
  const artistHref = $derived(`/library?artist=${encodeURIComponent(artistLabelForSong(song))}`);
  const albumHref = $derived(`/library?album=${encodeURIComponent(album.key)}`);
  const coverUrl = $derived(coverUrlForSong(song) ?? album.coverUrl ?? null);
  const lrclibUrl = $derived(lrclibWebUrl(trackArtist, trackTitle));

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

  // The AI disclosure travels with the title too, as a chip that spells itself out on a tap.
  const aiProvenance = $derived(
    ai.hasLyrics && viewerDoc.provenance && viewerDoc.provenance !== 'Human'
      ? viewerDoc.provenance
      : null
  );
  let aiDetailOpen = $state(false);

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
    const queue = album.songs.map((s) => toPlayerSong(s, album.artist));
    void playerStore.playSong(toPlayerSong(song, album.artist), queue, trackIndex);
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

{#snippet closeButton()}
  <Button
    variant="ghost"
    size="icon"
    onclick={onClose}
    class="focus-visible:ring-ring relative z-10 size-11 shrink-0 rounded-full hover:bg-transparent focus-visible:ring-2"
    aria-label="Close"
    title="Close (Esc)"
  >
    <ChevronDown class="size-7" />
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
{#snippet titleBlock()}
  <div class="flex min-w-0 items-center gap-2">
    <div class="min-w-0 flex-1">
      <h2 class={cn('text-title-2', largeType ? 'line-clamp-2' : 'truncate')} title={trackTitle}>
        {trackTitle}
      </h2>
      <p class="text-body text-muted-foreground -my-3 truncate py-3">
        <a
          href={artistHref}
          onclick={onClose}
          class="focus-visible:ring-ring pointer-fine:hover:text-foreground relative rounded-sm focus-visible:outline-hidden after:absolute after:inset-x-0 after:-inset-y-3 focus-visible:ring-2 pointer-fine:hover:underline"
          >{trackArtist}</a
        >
        <span aria-hidden="true"> — </span>
        <a
          href={albumHref}
          onclick={onClose}
          class="focus-visible:ring-ring pointer-fine:hover:text-foreground relative rounded-sm focus-visible:outline-hidden after:absolute after:inset-x-0 after:-inset-y-3 focus-visible:ring-2 pointer-fine:hover:underline"
          >{album.title}</a
        >
      </p>
    </div>
    {@render heart('lg')}
  </div>
  {#if aiProvenance || song.sharedByUserId}
    <div class="mt-2 flex flex-wrap items-center gap-2">
      <!-- Renders nothing for a track this account owns. -->
      <SharedByBadge {song} />
      {#if aiProvenance}
        <button
          type="button"
          class="focus-visible:ring-ring relative rounded-full focus-visible:outline-hidden after:absolute after:inset-x-0 after:-inset-y-3 focus-visible:ring-2"
          aria-expanded={aiDetailOpen}
          onclick={() => (aiDetailOpen = !aiDetailOpen)}
        >
          <AiLyricsBadge provenance={aiProvenance} variant="theater" />
        </button>
      {/if}
    </div>
    {#if aiProvenance && aiDetailOpen}
      <p class="text-footnote text-muted-foreground mt-2">{AI_LYRICS_COPY[aiProvenance].detail}</p>
    {/if}
  {/if}
{/snippet}

<!-- Compact: a toggle (aria-pressed) — release it to go back to the artwork. lg: it picks what the
     right column shows, so the one showing is marked current rather than pressed (releasing the
     default view has nowhere else to go). -->
{#snippet modeButton(target: DetailMode, label: string, Icon: Component<{ class?: string }>)}
  {@const active = content === target}
  <button
    type="button"
    class={cn(
      'focus-visible:ring-ring flex size-11 items-center justify-center rounded-full transition-colors focus-visible:outline-hidden focus-visible:ring-2',
      active ? 'text-foreground' : 'text-muted-foreground pointer-fine:hover:text-foreground'
    )}
    aria-pressed={wide.current ? undefined : active}
    aria-current={wide.current && active ? 'true' : undefined}
    aria-label={label}
    title={label}
    onclick={() => toggleMode(target)}
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
  <div class="flex items-center justify-evenly">
    {@render modeButton('lyrics', 'Lyrics', MessageSquareQuote)}
    {#if playerStore.airPlayAvailable && isCurrentlyLoaded}
      <button
        type="button"
        class="text-muted-foreground focus-visible:ring-ring flex size-11 items-center justify-center rounded-full focus-visible:outline-hidden focus-visible:ring-2"
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
              'focus-visible:ring-ring flex size-11 items-center justify-center rounded-full transition-colors focus-visible:outline-hidden focus-visible:ring-2',
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
    {#if video.playable}
      {@render modeButton('video', 'Video', MonitorPlay)}
    {/if}
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
            'text-footnote focus-visible:ring-ring mx-auto flex h-8 max-w-full items-center gap-1.5 rounded-full px-3 focus-visible:outline-hidden focus-visible:ring-2',
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

<!-- Loaded on demand: the device's stylesheet is the vendored SPEC-01 skin, which nobody without
     the easter egg should download. -->
{#snippet hardwareDevice(maxRows: number, fill = false)}
  {#await import('#lib/hardware/HardwareNowPlaying.svelte') then { default: HardwareNowPlaying }}
    <HardwareNowPlaying {maxRows} {fill} />
  {:catch}
    {@render transport()}
  {/await}
{/snippet}

<!-- At lg the meta line above the scrubber already reads "FLAC 1411 · 3:06 · …", so the format
     capsule would say it twice; only the speed capsule (when not 1×) stays under the bar there. -->
{#snippet transport()}
  <SongTransport
    isActive={isCurrentlyLoaded}
    isPlaying={isCurrentlyPlaying}
    fallbackDuration={song.durationSeconds ?? 0}
    onPlayToggle={handlePlayToggle}
    format={wide.current ? null : formatCapsule}
    media
    shuffle
  />
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

<!-- Under the hardware skin the clip plays on the MH-2, a monitor that carries the status line on
     its LCD. Loaded on demand like the MH-1 (same stylesheet). Unlike the MH-1 it is not tied to
     the loaded song: its one key is fullscreen, which works for a browsed song too. -->
{#snippet videoHousing(housing: VideoHousing)}
  {#await import('#lib/hardware/HardwareVideo.svelte') then { default: HardwareVideo }}
    <HardwareVideo {housing} status={videoLine} />
  {:catch}
    {@render housing.screen()}
  {/await}
{/snippet}

<!-- The swapping region: the compact middle, or the lg right column. Info stays mounted (hidden)
     so its lazy loads settle once per song however often the mode changes. -->
{#snippet modeContent()}
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
  {:else if content === 'video' && video.playable}
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
        caption={videoLine && !hardwareMode.enabled ? videoCaption : undefined}
        housing={hardwareMode.enabled ? videoHousing : undefined}
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
      {formatLabel}
      onNavigate={onClose}
      {onResetEnrichment}
      {timelineHref}
    />
  </div>
{/snippet}

<!-- Compact top bar, where a mouse can drag Now Playing down (a touch can anywhere): the grabber
     (a tap closes too), ⌄, and the ⋯ menu.
     Outside the artwork the song is condensed into the bar — tap it to go back to the art. -->
{#snippet topBar()}
  <div class="shrink-0 touch-none select-none" data-drag-handle>
    <!-- 12pt tall with a 44pt hit strip (16pt above and below). It fades while a sheet is up
         over Now Playing, so two grabbers never stack (see the style block). -->
    <button
      type="button"
      tabindex="-1"
      aria-hidden="true"
      class="np-grabber relative mx-auto flex h-3 w-24 justify-center transition-opacity duration-200 focus-visible:outline-hidden after:absolute after:inset-x-0 after:-top-4 after:-bottom-4"
      onclick={onClose}
    >
      <span class="bg-foreground/35 mt-1.5 block h-[5px] w-9 rounded-full"></span>
    </button>
    <div class="flex h-14 items-center gap-1 px-2">
      {@render closeButton()}
      {#if mode !== 'player'}
        <button
          type="button"
          class="focus-visible:ring-ring relative z-10 flex min-w-0 flex-1 items-center gap-3 rounded-lg py-1 text-left focus-visible:outline-hidden focus-visible:ring-2"
          aria-label={`${trackTitle} by ${trackArtist}. Show the artwork`}
          onclick={() => (mode = 'player')}
        >
          <NowPlayingArt
            artist={trackArtist}
            title={album.title}
            {coverUrl}
            size={44}
            corner={6}
            class="shrink-0 !shadow-[0_2px_8px_rgb(0_0_0/0.35)]"
          />
          <span class="flex min-w-0 flex-col">
            <span class="text-headline truncate">{trackTitle}</span>
            <span class="text-subheadline text-muted-foreground truncate">{trackArtist}</span>
          </span>
        </button>
        {@render heart('md')}
      {:else}
        <div class="flex-1"></div>
      {/if}
      {@render moreMenu()}
    </div>
  </div>
{/snippet}

<!-- A window wide enough for two columns is not dragged down, as on an iPad's full screen. -->
<div class="flex h-full max-h-full min-h-0 flex-col" data-drag-ignore={wide.current || undefined}>
  {#if wide.current}
    <!-- lg: close and ⋯ in a bar; art, title and transport on the left; the mode on the right. -->
    <header class="flex shrink-0 items-center justify-between px-5 pt-3 pb-1">
      {@render closeButton()}
      {@render moreMenu()}
    </header>
    <!-- Under the hardware skin the MH-1 is the player, and it fills a wider column: 38% of the
         window (the whole width when it is alone), as tall as the window allows (it is 1.3× as
         tall as wide, with the mode row under it), at most 1.6× its own 400px. -->
    <div
      class={cn(
        'grid min-h-0 flex-1 gap-12 px-12 pb-8 xl:gap-16',
        playerOnly
          ? 'grid-cols-[minmax(0,560px)] justify-center'
          : 'grid-cols-[minmax(300px,380px)_minmax(0,1fr)] xl:grid-cols-[400px_minmax(0,1fr)]'
      )}
      style:grid-template-columns={hardware
        ? playerOnly
          ? 'max(300px, min(100%, 640px, (100svh - 190px) * 0.72))'
          : 'max(300px, min(38vw, 640px, (100svh - 190px) * 0.72)) minmax(0, 1fr)'
        : undefined}
    >
      <!-- The column scrolls only when the window is too short even for the smaller art. Its box
           reaches 48px past the column on either side and down into the grid's bottom padding —
           a scroll container clips its contents, and the art's 60px shadow would otherwise end
           in hard edges at the column's sides. `my-auto` (not justify-center) centres the block
           while it fits and lets it overflow downward only, so its top is never unreachable —
           which is why the ScrollArea's content element is made a flex column. -->
      <aside class="-mx-12 -mb-8 flex min-h-0 flex-col">
        <ScrollArea
          class="min-h-0 flex-1 [&>[data-slot=scroll-area-viewport]>*]:flex [&>[data-slot=scroll-area-viewport]>*]:flex-col"
          viewportClass="px-12 pt-6 pb-8"
        >
          <div class="my-auto flex w-full flex-col">
            {#if hardware}
              {@render hardwareDevice(6, true)}
              <div class="mt-4">{@render bottomRow()}</div>
            {:else}
            <!-- The art gives way first on a short window (the transport, title and volume below it
                 need ~430px), and never shrinks under 200px. -->
            <NowPlayingArt
              artist={trackArtist}
              title={album.title}
              {coverUrl}
              size={400}
              corner={12}
              playing={artPlaying}
              class={cn(
                'mx-auto w-full',
                playerOnly
                  ? 'max-w-[min(100%,56vh,max(200px,calc(100svh-460px)))]'
                  : 'max-w-[min(100%,46vh,max(200px,calc(100svh-460px)))]'
              )}
            />
            <div class="mt-6">{@render titleBlock()}</div>
            {#if metaLine}
              <p class="text-footnote text-muted-foreground mt-1.5 tabular-nums">{metaLine}</p>
            {/if}
            <div class="mt-5">{@render transport()}</div>
            <div class="mt-2">{@render bottomRow()}</div>
            {#if !elsewhere}
              <div class="mt-4 hidden pointer-fine:block">
                <VolumeControl />
              </div>
            {/if}
            {/if}
          </div>
        </ScrollArea>
      </aside>
      <div class={cn('flex min-h-0 flex-col pt-2', playerOnly && 'hidden')}>
        {@render modeContent()}
      </div>
    </div>
  {:else if sideways.current}
    <!-- A phone on its side: the middle on the left, the title block and the transport on the
         right. Same top bar, same modes. -->
    <div class="flex h-full min-h-0 w-full flex-col">
      {@render topBar()}
      <div class="flex min-h-0 flex-1 gap-6 pr-6 pl-4">
        <div class="flex min-h-0 min-w-0 flex-1 flex-col">
          {#if content === 'player'}
            <div
              class="flex min-h-0 flex-1 touch-none items-center justify-center pb-3 select-none"
              data-drag-handle
            >
              <NowPlayingArt
                artist={trackArtist}
                title={album.title}
                {coverUrl}
                size={360}
                corner={12}
                playing={artPlaying}
                class="w-[min(100%,calc(100svh-120px))]"
              />
            </div>
          {/if}
          {@render modeContent()}
        </div>
        <!-- Scrolls only if the title block carries chips on the shortest phones. -->
        <div class="no-scrollbar flex w-[min(360px,46%)] shrink-0 flex-col overflow-y-auto pb-2">
          <div class="my-auto">
            {#if content === 'player'}
              <div class="mb-3">{@render titleBlock()}</div>
            {/if}
            {@render transport()}
            <div class="mt-1">{@render bottomRow()}</div>
          </div>
        </div>
      </div>
    </div>
  {:else}
    <div class="mx-auto flex h-full min-h-0 w-full max-w-xl flex-col">
      {@render topBar()}

      <div class="flex min-h-0 flex-1 flex-col">
        {#if content === 'player' && hardware}
          <!-- The device, set on the dark media surface like a player on a desk. It takes its own
               drags (the fader, the knob), so it does not pull Now Playing down, and its queue
               scrolls if a short phone needs it. -->
          <div
            class="flex min-h-0 flex-1 flex-col justify-center overflow-y-auto px-4 py-2"
            data-drag-ignore
          >
            {@render hardwareDevice(4)}
          </div>
        {:else if content === 'player'}
          <!-- The artwork and title, where a mouse can drag Now Playing down too. -->
          <div
            class="flex min-h-0 flex-1 touch-none flex-col justify-center px-8 select-none"
            data-drag-handle
          >
            <div
              class="mx-auto flex w-full max-w-[min(100%,40svh,329px)] flex-col md:max-w-[min(100%,40svh,440px)]"
            >
              <NowPlayingArt
                artist={trackArtist}
                title={album.title}
                {coverUrl}
                size={360}
                corner={12}
                playing={artPlaying}
                class="w-full"
              />
              <div class="mt-7">{@render titleBlock()}</div>
            </div>
          </div>
        {/if}
        {@render modeContent()}
      </div>

      <footer class="shrink-0 px-7 pt-3 pb-2">
        <!-- Under the hardware skin the device on the player page carries the transport. -->
        {#if !(hardware && content === 'player')}
          {@render transport()}
        {/if}
        <div class="mt-1">{@render bottomRow()}</div>
        <!-- md–lg with a mouse (a narrow desktop window, an iPad with a trackpad): Now Playing hides
             the mini player that holds the volume, so it needs its own here too. -->
        {#if !elsewhere && !(hardware && content === 'player')}
          <div class="mt-3 hidden md:pointer-fine:block">
            <VolumeControl />
          </div>
        {/if}
      </footer>
    </div>
  {/if}
</div>

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
    actionLabel={ai.enhanceLabel}
    onAction={startEnhance}
  />
{/if}

{#if isOwner}
  <ManageVideoSheet bind:open={manageVideoOpen} {video} title={`${trackTitle} — ${trackArtist}`} />
{/if}

<style>
  /* While a sheet is up over Now Playing (compare, manage video, a confirmation), it stops 12pt
     short of the top, and the overlay's own grabber would peek out above the sheet's. Every sheet
     that can be open while this overlay is up is nested over it, and sheets are portaled to the
     body, so ask the body. */
  :global(body:has([data-slot='bottom-sheet-content'][data-state='open'])) .np-grabber {
    opacity: 0;
  }
</style>
