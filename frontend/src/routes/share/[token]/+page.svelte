<script lang="ts">
  import { page } from '$app/state';
  import {
    Airplay,
    AlertCircle,
    ChevronRight,
    Ellipsis,
    Film,
    Gauge,
    ListMusic,
    Loader2,
    MessageCircle,
    MessageSquareQuote,
    MonitorPlay,
    Music
  } from '@lucide/svelte';
  import { untrack, type Component, type Snippet } from 'svelte';
  import { MediaQuery } from 'svelte/reactivity';
  import Cover from '#lib/components/file-browser/Cover.svelte';
  import LyricsPanel from '#lib/components/file-browser/LyricsPanel.svelte';
  import SongTransport from '#lib/components/file-browser/SongTransport.svelte';
  import VideoWatchTab, {
    type VideoHousing
  } from '#lib/components/file-browser/VideoWatchTab.svelte';
  import NowPlayingLayout, {
    type QueuePreviewEntry
  } from '#lib/components/file-browser/now-playing/NowPlayingLayout.svelte';
  import VideoLyricsOverlay from '#lib/components/file-browser/now-playing/VideoLyricsOverlay.svelte';
  import {
    coverDimAlpha,
    DIM_UNKNOWN
  } from '#lib/components/file-browser/now-playing/cover-dim.ts';
  import type { MediaAvailability } from '#lib/components/file-browser/now-playing/media-availability.ts';
  import type { Stage } from '#lib/components/file-browser/now-playing/mode-motion.ts';
  import VideoBackdrop, { type VideoBackdropSource } from '#lib/components/v2/VideoBackdrop.svelte';
  import { Button } from '#lib/components/ui/button/index.ts';
  import * as DropdownMenu from '#lib/components/ui/dropdown-menu/index.ts';
  import { SegmentedControl } from '#lib/components/ui/segmented-control/index.ts';
  import { formatOf } from '#lib/audio-formats.ts';
  import { DYNAMIC_TYPE_CHANGE, dynamicTypeScale } from '#lib/dynamic-type.ts';
  import { formatDuration } from '#lib/formatters.ts';
  import {
    claimHardwareSkin,
    mediaAppearance,
    provideHardwareSkin,
    skinHousing,
    skinLook
  } from '#lib/hardware/hardware-mode.svelte.ts';
  import { SKIN_STYLESHEET_HREF } from '#lib/hardware/skin-stylesheet.ts';
  import { createLyricsDoc } from '#lib/lyrics/lyrics-doc.svelte.ts';
  import { playerStore, type PlayerSong } from '#lib/stores/player.svelte.ts';
  import { themeSurface } from '#lib/stores/theme-surface.svelte.ts';
  import { videoBackdropPrefs } from '#lib/stores/video-backdrop-prefs.svelte.ts';
  import { trackUmamiEvent } from '#lib/analytics/umami.ts';
  import { fetchChatShareContext } from '#lib/api-client.ts';
  import {
    externalReferrer,
    fetchShareLyrics,
    reportSharePlay,
    reportShareVisit,
    shareCoverUrl,
    shareOpenEventData,
    sharePlayEventData,
    shareStreamUrl,
    shareVideoStreamUrl,
    type ShareLyrics,
    type ShareTrack
  } from '#lib/share-client.ts';
  import { cn } from '#lib/utils.ts';
  import type { PageProps } from './$types';

  const { data }: PageProps = $props();

  const payload = $derived(data.payload);
  const tracks = $derived(payload?.tracks ?? []);
  // A one-track album has no tracklist or queue to shuffle, so treat multi-track shares as album UI.
  const isAlbumShare = $derived(tracks.length > 1);
  const tracksById = $derived(new Map(tracks.map((t) => [t.id, t])));

  // The track whose art/metadata/lyrics/video the player is presenting. Starts at the shared song
  // and follows playback as this share's own album queue advances (never a library track with the
  // same song id).
  let followedId = $state<number | null>(null);
  const activeId = $derived(followedId ?? payload?.sharedSongId ?? -1);
  const activeTrack = $derived(tracks.find((t) => t.id === activeId) ?? tracks[0]);

  $effect(() => {
    const song = playerStore.currentSong;
    const playingId = song?.id;
    if (
      playingId != null &&
      song?.streamUrl === shareStreamUrl(data.token, playingId) &&
      tracks.some((t) => t.id === playingId)
    ) {
      followedId = playingId;
    }
  });

  // SPEC-01: when the person who shared this link had hardware mode on, present the share page in
  // SPEC-01 too (MH-1 console, MH-2 video monitor, CRT backdrop) without unlocking or persisting
  // it into the visitor's own localStorage. Context handles SSR first paint (paired with
  // hooks.server.ts hoisting `data-mh-skin="hardware"` onto <html>); the browser effect keeps the
  // document attribute, haptics and theme-color active until navigation away.
  const forcedHardware = $derived(payload?.skin === 'hardware');
  provideHardwareSkin(() => forcedHardware);
  $effect(() => (forcedHardware ? claimHardwareSkin() : undefined));

  // What the owner's Share links page counts: this open, once per page load, and the first time
  // each track actually plays. Effects run only in the browser, so the server render a link-preview
  // crawler fetches reports nothing. The server drops the owner's own visits, bots and repeats.
  // The same two moments go to Umami (when the instance has it) as named events, so its dashboard
  // shows which song was opened rather than only a page view of an opaque token URL.
  let reportedVisitFor: string | null = null;
  let chatContext = $state<{ conversationId: string; ownerName: string } | null>(null);
  const reportedPlays = new Set<number>();
  $effect(() => {
    if (!payload || reportedVisitFor === data.token) return;
    reportedVisitFor = data.token;
    reportedPlays.clear();
    const token = data.token;
    // Signed in to this MusicHoarder? The open just put this in your chat with whoever shared it;
    // say so, with the way there. An anonymous visitor's check is a quiet 401.
    void reportShareVisit(token, externalReferrer(document.referrer, location.origin))
      .then(() => fetchChatShareContext(token))
      .then((context) => {
        if (data.token === token) chatContext = context;
      });
    trackUmamiEvent('share-open', shareOpenEventData(payload));
  });
  $effect(() => {
    const song = playerStore.isPlaying ? playerStore.currentSong : null;
    const playingId = song?.id;
    if (playingId == null || reportedPlays.has(playingId)) return;
    // The share's own stream, not a library song that happens to carry the same id (the player
    // store outlives a client-side hop from the app to a share page).
    if (song?.streamUrl !== shareStreamUrl(data.token, playingId)) return;
    reportedPlays.add(playingId);
    reportSharePlay(data.token, playingId);
    const track = tracks.find((t) => t.id === playingId);
    if (payload && track) trackUmamiEvent('share-play', sharePlayEventData(payload, track));
  });

  // ── Display metadata ────────────────────────────────────────────────────────
  const wide = new MediaQuery('(min-width: 1024px)');
  const surface = $derived(skinLook('bg-black', 'bg-background', forcedHardware));

  const albumArtist = $derived(payload?.album.artist ?? activeTrack?.artist ?? 'Unknown artist');
  const displayArtist = $derived(activeTrack?.artist ?? albumArtist);
  const albumTitle = $derived(payload?.album.title ?? activeTrack?.title ?? '');
  const albumLabel = $derived(
    [payload?.album.title, payload?.album.year].filter(Boolean).join(' · ')
  );

  const coverUrl = $derived(
    activeTrack?.hasCoverArt ? shareCoverUrl(data.token, activeTrack.id) : null
  );
  const ambientUrl = $derived(
    activeTrack?.hasCoverArt ? shareCoverUrl(data.token, activeTrack.id, 600) : null
  );
  // Absolute URL for link-preview crawlers (og:image must not be relative).
  const ogImage = $derived(coverUrl ? new URL(coverUrl, page.url.origin).href : null);
  const pageTitle = $derived(
    payload && activeTrack
      ? isAlbumShare
        ? `${payload.album.title ?? 'Album'} — ${albumArtist}`
        : `${activeTrack.title} — ${displayArtist}`
      : 'Shared music'
  );

  let dimAlpha = $state(DIM_UNKNOWN);
  $effect(() => {
    const url = ambientUrl;
    let cancelled = false;
    void coverDimAlpha(url).then((alpha) => {
      if (!cancelled) dimAlpha = alpha;
    });
    return () => {
      cancelled = true;
    };
  });

  let largeType = $state(dynamicTypeScale() > 1.2);
  $effect(() => {
    const onChange = () => (largeType = dynamicTypeScale() > 1.2);
    window.addEventListener(DYNAMIC_TYPE_CHANGE, onChange);
    return () => window.removeEventListener(DYNAMIC_TYPE_CHANGE, onChange);
  });

  const activeDurationSeconds = $derived(
    activeTrack?.durationMs ? activeTrack.durationMs / 1000 : 0
  );
  const LOSSLESS = new Set(['FLAC', 'ALAC', 'WAV', 'AIFF', 'AIF', 'APE', 'WV']);
  const ext = $derived((activeTrack?.extension ?? '').replace(/^\./, '').toUpperCase());
  const formatCapsule = $derived(
    ext
      ? LOSSLESS.has(ext) || !activeTrack?.bitRate
        ? ext
        : `${ext} ${activeTrack.bitRate}`
      : null
  );
  const metaLine = $derived(
    [
      ext ? (activeTrack?.bitRate ? `${ext} ${activeTrack.bitRate}` : ext) : null,
      formatDuration(activeDurationSeconds > 0 ? activeDurationSeconds : null),
      activeTrack?.hasSyncedLyrics ? 'LRC' : null
    ]
      .filter((part) => part && part !== '—')
      .join(' · ')
  );

  // ── Playback ────────────────────────────────────────────────────────────────
  function toPlayerSong(track: ShareTrack): PlayerSong {
    return {
      id: track.id,
      title: track.title,
      artist: track.artist ?? albumArtist,
      streamUrl: shareStreamUrl(data.token, track.id),
      coverUrl: track.hasCoverArt ? shareCoverUrl(data.token, track.id) : null,
      album: payload?.album.title ?? null,
      format: formatOf(track.extension)
    };
  }

  const isCurrentlyLoaded = $derived(
    activeTrack != null &&
      playerStore.currentSong?.id === activeTrack.id &&
      playerStore.currentSong?.streamUrl === shareStreamUrl(data.token, activeTrack.id)
  );
  const isCurrentlyPlaying = $derived(isCurrentlyLoaded && playerStore.isPlaying);
  const artPlaying = $derived(!isCurrentlyLoaded || playerStore.isPlaying);

  function playTrack(track: ShareTrack) {
    const queue = tracks.map(toPlayerSong);
    void playerStore.playSong(
      toPlayerSong(track),
      queue,
      tracks.findIndex((t) => t.id === track.id)
    );
  }

  function handlePlayToggle() {
    if (!activeTrack) return;
    if (isCurrentlyLoaded) {
      playerStore.togglePlay();
      return;
    }
    playTrack(activeTrack);
  }

  // ── Video (backdrop + watch mode) ───────────────────────────────────────────
  const shareVideo = $derived<VideoBackdropSource>({
    playable: Boolean(activeTrack?.hasVideo),
    songId: activeTrack?.id ?? null,
    offsetMs: activeTrack?.videoOffsetMs ?? 0,
    generation: 0,
    info: activeTrack?.hasVideo
      ? {
          status: 'Ready',
          durationSeconds: activeTrack.videoDurationSeconds ?? null,
          letterbox: activeTrack.videoLetterbox ?? null,
          pillarbox: activeTrack.videoPillarbox ?? null
        }
      : null
  });
  let videoShowing = $state(false);

  $effect(() => (payload && activeTrack ? themeSurface.claimMedia() : undefined));
  $effect(() => (payload && activeTrack && videoShowing ? themeSurface.claimTv() : undefined));

  // ── Lyrics ──────────────────────────────────────────────────────────────────
  let lyrics = $state<ShareLyrics | null>(null);
  let lyricsForKey = $state<string | null>(null);
  let lyricsLoading = $state(false);
  let lyricsError = $state(false);
  let lyricsAttempt = $state(0);
  let lyricsReqSeq = 0;

  const activeLyricsKey = $derived(activeTrack ? `${data.token}:${activeTrack.id}` : null);

  $effect(() => {
    void lyricsAttempt;
    const track = activeTrack;
    const key = activeLyricsKey;
    if (!track || !key || lyricsForKey === key) return;
    if (!track.hasSyncedLyrics && !track.hasPlainLyrics) {
      lyricsReqSeq += 1;
      lyrics = null;
      lyricsError = false;
      lyricsLoading = false;
      lyricsForKey = key;
      return;
    }
    const seq = ++lyricsReqSeq;
    const token = data.token;
    lyrics = null;
    lyricsError = false;
    lyricsLoading = true;
    fetchShareLyrics(token, track.id)
      .then((result) => {
        if (seq !== lyricsReqSeq) return;
        lyrics = result;
        lyricsError = false;
        lyricsForKey = key;
      })
      .catch(() => {
        if (seq !== lyricsReqSeq) return;
        lyrics = null;
        lyricsError = true;
        lyricsForKey = key;
      })
      .finally(() => {
        if (seq === lyricsReqSeq) lyricsLoading = false;
      });
  });

  function retryLyrics() {
    lyricsForKey = null;
    lyricsError = false;
    lyricsAttempt += 1;
  }

  const lyricsReady = $derived(
    lyrics !== null && activeLyricsKey !== null && lyricsForKey === activeLyricsKey
  );
  const lyricsDoc = createLyricsDoc({
    songId: () => activeTrack?.id ?? null,
    key: () =>
      `${activeTrack?.id ?? 'none'}:${lyricsReady ? (lyrics?.synced?.length ?? 0) : -1}:${lyricsReady ? (lyrics?.plain?.length ?? 0) : -1}`,
    synced: () => (lyricsReady ? (lyrics?.synced ?? undefined) : undefined),
    plain: () => (lyricsReady ? (lyrics?.plain ?? undefined) : undefined),
    hasSynced: () => activeTrack?.hasSyncedLyrics ?? false,
    hasPlain: () => activeTrack?.hasPlainLyrics ?? false,
    status: () => (activeTrack?.isInstrumental ? 'Instrumental' : 'Fetched'),
    provenance: () => (lyricsReady ? (lyrics?.lyricsProvenance ?? null) : null),
    trustProvidedProvenance: true,
    fetchable: false
  });
  $effect(() => lyricsDoc.syncToSong());

  let lyricsView = $state<'original' | 'pronunciation' | 'translation'>('original');
  const lyricsViewItems: { value: typeof lyricsView; label: string }[] = [
    { value: 'original', label: 'Original' },
    { value: 'pronunciation', label: 'Pronunciation' },
    { value: 'translation', label: 'Translation' }
  ];
  $effect(() => {
    void activeTrack?.id;
    lyricsView = 'original';
  });
  const hasShareTranslation = $derived(
    Boolean(
      lyricsReady &&
      (lyrics?.romanizedSynced ??
        lyrics?.romanizedPlain ??
        lyrics?.translatedSynced ??
        lyrics?.translatedPlain)
    )
  );
  const secondarySynced = $derived.by(() => {
    if (!hasShareTranslation) return undefined;
    if (lyricsView === 'pronunciation') return lyrics?.romanizedSynced ?? undefined;
    if (lyricsView === 'translation') return lyrics?.translatedSynced ?? undefined;
    return undefined;
  });
  const secondaryPlain = $derived.by(() => {
    if (!hasShareTranslation) return undefined;
    if (lyricsView === 'pronunciation') return lyrics?.romanizedPlain ?? undefined;
    if (lyricsView === 'translation') return lyrics?.translatedPlain ?? undefined;
    return undefined;
  });

  // ── Modes (mirrors TrackPanel: artwork, lyrics, video, lyrics-video, queue) ─
  type ShareMode = 'player' | 'artwork' | 'lyrics' | 'video' | 'lyrics-video' | 'queue';
  let mode = $state<ShareMode>('player');

  const hasLyrics = $derived(
    activeTrack != null &&
      (activeTrack.hasSyncedLyrics || activeTrack.hasPlainLyrics || activeTrack.isInstrumental)
  );
  const lyricsState = $derived<MediaAvailability>(hasLyrics ? 'ready' : 'none');
  const videoState = $derived<MediaAvailability>(activeTrack?.hasVideo ? 'ready' : 'none');
  const lyricsWhy = $derived(activeTrack?.isInstrumental ? 'instrumental' : 'none for this song');

  // If the album advances to a track without lyrics or video, fall back cleanly.
  $effect(() => {
    const id = activeTrack?.id;
    void id;
    const current = untrack(() => mode);
    if (!hasLyrics && (current === 'lyrics' || current === 'lyrics-video')) {
      mode = current === 'lyrics-video' && activeTrack?.hasVideo ? 'video' : 'player';
    } else if (!activeTrack?.hasVideo && (current === 'video' || current === 'lyrics-video')) {
      mode = current === 'lyrics-video' && hasLyrics ? 'lyrics' : 'player';
    }
  });

  const content = $derived<ShareMode>(
    mode === 'artwork'
      ? 'player'
      : wide.current && mode === 'player'
        ? hasLyrics
          ? 'lyrics'
          : activeTrack?.hasVideo
            ? 'video'
            : isAlbumShare
              ? 'queue'
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
  const videoWatching = $derived(
    (content === 'video' || content === 'lyrics-video') && Boolean(activeTrack?.hasVideo)
  );

  function toggleMode(target: ShareMode) {
    if (target === 'lyrics' || target === 'video') {
      if (content === 'lyrics-video') mode = target === 'lyrics' ? 'video' : 'lyrics';
      else if (
        (target === 'lyrics' && content === 'video') ||
        (target === 'video' && content === 'lyrics')
      )
        mode = 'lyrics-video';
      else mode = content === target ? 'artwork' : target;
    } else {
      mode = content === target ? 'artwork' : target;
    }
  }

  function pressLyrics() {
    if (lyricsState === 'ready' || content === 'lyrics' || content === 'lyrics-video') {
      toggleMode('lyrics');
    }
  }

  function pressVideo() {
    if (videoState === 'ready' || content === 'video' || content === 'lyrics-video') {
      toggleMode('video');
    }
  }

  const queuePreview = $derived.by<QueuePreviewEntry[]>(() => {
    if (!activeTrack) return [];
    // Once this share is loaded in the player, always mirror the live queue (including an empty
    // slice at the end of a shuffled queue) rather than inventing phantom tracks from album order.
    if (isCurrentlyLoaded) {
      return playerStore.upNext.slice(0, wide.current ? 5 : 3).map((entry, index) => {
        const match = tracksById.get(entry.id);
        return {
          id: entry.id,
          title: entry.title,
          artist: entry.artist,
          durationSeconds: match?.durationMs ? match.durationMs / 1000 : null,
          onSelect: () => playerStore.skipToInQueue(index)
        };
      });
    }
    if (!isAlbumShare) return [];
    const idx = tracks.findIndex((t) => t.id === activeTrack.id);
    const rest = idx >= 0 ? tracks.slice(idx + 1) : [];
    return rest.slice(0, wide.current ? 5 : 3).map((t) => ({
      id: t.id,
      title: t.title,
      artist: t.artist ?? albumArtist,
      durationSeconds: t.durationMs ? t.durationMs / 1000 : null,
      onSelect: () => playTrack(t)
    }));
  });

  const speedOptions = [0.5, 0.65, 0.75, 0.85, 1, 1.1, 1.25, 1.5];
  const hasLyricsMenu = $derived(hasShareTranslation || lyricsDoc.canToggleSynced);
</script>

<svelte:head>
  <title>{pageTitle}</title>
  <meta name="robots" content="noindex" />
  {#if forcedHardware}
    <link
      rel="stylesheet"
      href={SKIN_STYLESHEET_HREF}
      data-mh-skin-css=""
      data-mh-share-skin="hardware"
    />
  {/if}
  {#if payload && activeTrack}
    <meta property="og:type" content="music.song" />
    <meta property="og:title" content={pageTitle} />
    <meta
      property="og:description"
      content={isAlbumShare
        ? `Listen to ${tracks.length} tracks, shared from a MusicHoarder library.`
        : `Listen to “${activeTrack.title}”, shared from a MusicHoarder library.`}
    />
    {#if ogImage}
      <meta property="og:image" content={ogImage} />
    {/if}
  {/if}
</svelte:head>

{#snippet sharedViaLink()}
  <a
    href="/"
    class="text-footnote text-muted-foreground focus-visible:ring-ring pointer-fine:hover:text-foreground inline-flex min-h-11 items-center gap-1 rounded-lg px-3 transition-colors focus-visible:ring-2 focus-visible:outline-hidden"
  >
    <span>Shared via <span class="text-foreground font-medium">MusicHoarder</span></span>
  </a>
{/snippet}

{#snippet chatBanner()}
  {#if chatContext}
    <div class="flex min-w-0 flex-1 justify-center">
      <a
        href={`/chats/${chatContext.conversationId}`}
        class="text-subheadline focus-visible:ring-ring inline-flex min-h-11 min-w-0 items-center gap-1.5 rounded-lg px-3 font-semibold focus-visible:ring-2 focus-visible:outline-hidden pointer-fine:hover:underline"
      >
        <MessageCircle class="text-primary size-4 shrink-0" aria-hidden="true" />
        <span class="truncate">In your chat with {chatContext.ownerName}</span>
        <ChevronRight class="text-muted-foreground size-3.5 shrink-0" aria-hidden="true" />
      </a>
    </div>
  {:else}
    <div class="flex-1"></div>
  {/if}
{/snippet}

{#snippet shareMoreMenu()}
  <DropdownMenu.Root>
    <DropdownMenu.Trigger>
      {#snippet child({ props })}
        <Button
          {...props}
          variant="ghost"
          size="icon"
          class="group/more focus-visible:ring-ring relative z-10 size-11 shrink-0 rounded-full hover:bg-transparent focus-visible:ring-2 aria-expanded:bg-transparent"
          aria-label="More"
        >
          <span
            class="bg-secondary group-hover/more:bg-secondary-hover flex size-8 items-center justify-center rounded-full transition-colors"
          >
            <Ellipsis class="size-5" />
          </span>
        </Button>
      {/snippet}
    </DropdownMenu.Trigger>
    <DropdownMenu.Content align="end" class={['z-[70] min-w-56', mediaAppearance(forcedHardware)]}>
      {#if hasLyricsMenu}
        <DropdownMenu.Sub>
          <DropdownMenu.SubTrigger>
            <MessageSquareQuote />
            Lyrics
          </DropdownMenu.SubTrigger>
          <DropdownMenu.SubContent class="min-w-52">
            {#if hasShareTranslation}
              <DropdownMenu.RadioGroup
                value={lyricsView}
                onValueChange={(v) => {
                  lyricsView = v as typeof lyricsView;
                  if (content !== 'lyrics' && content !== 'lyrics-video') mode = 'lyrics';
                }}
              >
                <DropdownMenu.RadioItem value="original">Show original</DropdownMenu.RadioItem>
                <DropdownMenu.RadioItem value="pronunciation">Pronunciation</DropdownMenu.RadioItem>
                <DropdownMenu.RadioItem value="translation">Translation</DropdownMenu.RadioItem>
              </DropdownMenu.RadioGroup>
              {#if lyricsDoc.canToggleSynced}
                <DropdownMenu.Separator />
              {/if}
            {/if}
            {#if lyricsDoc.canToggleSynced}
              <DropdownMenu.RadioGroup
                value={lyricsDoc.showSynced ? 'synced' : 'plain'}
                onValueChange={(v) => {
                  lyricsDoc.setShowSynced(v === 'synced');
                  if (content !== 'lyrics' && content !== 'lyrics-video') mode = 'lyrics';
                }}
              >
                <DropdownMenu.RadioItem value="synced">Synced</DropdownMenu.RadioItem>
                <DropdownMenu.RadioItem value="plain">Plain</DropdownMenu.RadioItem>
              </DropdownMenu.RadioGroup>
            {/if}
          </DropdownMenu.SubContent>
        </DropdownMenu.Sub>
      {/if}

      {#if activeTrack?.hasVideo}
        <DropdownMenu.Sub>
          <DropdownMenu.SubTrigger>
            <Film />
            Music video
          </DropdownMenu.SubTrigger>
          <DropdownMenu.SubContent class="min-w-52">
            <DropdownMenu.CheckboxItem
              checked={videoBackdropPrefs.enabled}
              onCheckedChange={(v) => videoBackdropPrefs.setEnabled(v)}
            >
              Show as background
            </DropdownMenu.CheckboxItem>
          </DropdownMenu.SubContent>
        </DropdownMenu.Sub>
      {/if}

      <DropdownMenu.Sub>
        <DropdownMenu.SubTrigger>
          <Gauge />
          Playback speed
        </DropdownMenu.SubTrigger>
        <DropdownMenu.SubContent class="min-w-44">
          <DropdownMenu.RadioGroup
            value={String(playerStore.playbackRate)}
            onValueChange={(v) => playerStore.setPlaybackRate(Number(v))}
          >
            {#each speedOptions as rate (rate)}
              <DropdownMenu.RadioItem value={String(rate)} class="tabular-nums">
                {rate === 1 ? 'Normal' : `${rate}×`}
              </DropdownMenu.RadioItem>
            {/each}
          </DropdownMenu.RadioGroup>
        </DropdownMenu.SubContent>
      </DropdownMenu.Sub>
    </DropdownMenu.Content>
  </DropdownMenu.Root>
{/snippet}

{#snippet titleBlock()}
  {#if activeTrack}
    <div class="flex min-w-0 items-center gap-2">
      <div class="min-w-0 flex-1">
        <h2
          class={cn('text-title-2', largeType ? 'line-clamp-2' : 'truncate')}
          title={activeTrack.title}
        >
          {activeTrack.title}
        </h2>
        <p class="text-body text-muted-foreground truncate">
          {displayArtist}{#if albumLabel}<span aria-hidden="true"> — </span>{albumLabel}{/if}
        </p>
      </div>
    </div>
  {/if}
{/snippet}

{#snippet modeButton(
  target: ShareMode,
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

{#snippet bottomRow()}
  <div class="flex items-center justify-evenly" data-mh-rocker>
    {@render modeButton(
      'lyrics',
      'Lyrics',
      MessageSquareQuote,
      lyricsState,
      lyricsWhy,
      pressLyrics
    )}
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
    {@render modeButton(
      'video',
      'Video',
      MonitorPlay,
      videoState,
      'none for this song',
      pressVideo
    )}
    {#if isAlbumShare}
      {@render modeButton('queue', 'Tracklist', ListMusic)}
    {/if}
  </div>
{/snippet}

{#snippet transport(layout?: Snippet<[Snippet, Snippet, Snippet]>)}
  <SongTransport
    isActive={isCurrentlyLoaded}
    isPlaying={isCurrentlyPlaying}
    fallbackDuration={activeDurationSeconds}
    onPlayToggle={handlePlayToggle}
    format={wide.current ? null : formatCapsule}
    {layout}
    media
    shuffle={isAlbumShare}
  />
{/snippet}

{#snippet videoLyrics()}
  <VideoLyricsOverlay
    doc={lyricsDoc}
    currentTimeMs={isCurrentlyLoaded ? playerStore.currentTime * 1000 : null}
    {secondarySynced}
    {secondaryPlain}
  />
{/snippet}

{#snippet videoHousing(housing: VideoHousing)}
  {#await import('#lib/hardware/HardwareVideo.svelte') then { default: HardwareVideo }}
    <HardwareVideo {housing} />
  {:catch}
    {@render housing.screen()}
  {/await}
{/snippet}

{#snippet modeContent()}
  {#if activeTrack}
    {@const monitor = skinHousing(videoHousing, forcedHardware)}
    {#if content === 'lyrics'}
      <div class={cn('flex min-h-0 flex-1 flex-col gap-3', !wide.current && 'px-6')}>
        {#if hasShareTranslation}
          <div
            class={cn(
              'flex shrink-0 flex-col gap-2',
              wide.current ? 'mx-auto w-full max-w-3xl px-1' : 'items-center text-center'
            )}
          >
            <SegmentedControl
              items={lyricsViewItems}
              bind:value={lyricsView}
              label="Lyrics view"
              class={cn(wide.current ? 'w-auto self-center' : 'max-w-sm')}
            />
          </div>
        {/if}
        <div
          class={cn(
            'flex min-h-0 flex-1 flex-col [mask-image:linear-gradient(to_bottom,transparent,#000_24px,#000_calc(100%-40px),transparent)] pt-3',
            wide.current && 'mx-auto w-full max-w-3xl'
          )}
        >
          {#if activeTrack.isInstrumental || lyricsReady}
            {#key `${activeTrack.id}-${lyricsView}`}
              <LyricsPanel
                variant="theater"
                readOnly
                songId={activeTrack.id}
                doc={lyricsDoc}
                isInstrumental={activeTrack.isInstrumental}
                currentTimeMs={isCurrentlyLoaded ? playerStore.currentTime * 1000 : null}
                onSeek={isCurrentlyLoaded ? (timeMs) => playerStore.seek(timeMs / 1000) : undefined}
                {secondarySynced}
                {secondaryPlain}
              />
            {/key}
          {:else if lyricsLoading}
            <div
              class="text-subheadline text-muted-foreground flex flex-1 items-center justify-center gap-2"
            >
              <Loader2 class="size-4 animate-spin" /> Loading lyrics…
            </div>
          {:else if lyricsError}
            <div class="flex flex-1 flex-col items-center justify-center gap-3 py-8 text-center">
              <AlertCircle class="text-destructive-text size-8" />
              <p class="text-subheadline text-muted-foreground">Couldn’t load lyrics.</p>
              <Button
                variant="gray"
                class="h-9 rounded-full px-4 pointer-coarse:h-11"
                onclick={retryLyrics}
              >
                Retry
              </Button>
            </div>
          {/if}
        </div>
      </div>
    {:else if (content === 'video' || content === 'lyrics-video') && activeTrack.hasVideo}
      <div class={cn('flex min-h-0 flex-1 flex-col', !wide.current && 'px-4 py-3')}>
        <VideoWatchTab
          songId={activeTrack.id}
          streamUrl={shareVideoStreamUrl(data.token, activeTrack.id)}
          isCurrent={isCurrentlyLoaded}
          offsetMs={activeTrack.videoOffsetMs ?? 0}
          title={activeTrack.title}
          artist={displayArtist}
          fallbackDuration={activeDurationSeconds}
          housing={monitor}
          lyricsVisible={content === 'lyrics-video'}
          lyricsAvailability={lyricsState}
          onToggleLyrics={pressLyrics}
          lyricsOverlay={videoLyrics}
          onPlayRequest={handlePlayToggle}
        />
      </div>
    {:else if content === 'queue' && isAlbumShare}
      <div
        class={cn(
          'no-scrollbar min-h-0 flex-1 overflow-y-auto overscroll-contain pb-6',
          '[mask-image:linear-gradient(to_bottom,transparent,#000_12px,#000_calc(100%-24px),transparent)]',
          wide.current ? 'pt-0' : 'pt-1'
        )}
      >
        <div class="flex min-h-11 items-end px-4 pt-2 pb-1.5">
          <h3 class="text-headline md:text-[15px] md:font-semibold">
            {payload?.album.title ?? 'Tracklist'}
          </h3>
        </div>
        <ol aria-label="Tracklist">
          {#each tracks as track, i (track.id)}
            {@const isRowActive = track.id === activeTrack.id}
            <li class="flex h-16 items-center md:h-14">
              <button
                type="button"
                class={cn(
                  'focus-visible:ring-ring flex h-full min-w-0 flex-1 items-center gap-3 rounded-lg px-4 text-left focus-visible:ring-2 focus-visible:outline-hidden focus-visible:ring-inset active:bg-[var(--np-fill)]',
                  isRowActive && 'bg-[var(--np-fill)]'
                )}
                aria-current={isRowActive ? 'true' : undefined}
                onclick={() => playTrack(track)}
              >
                <span
                  class="text-footnote text-muted-foreground w-6 shrink-0 text-right tabular-nums"
                >
                  {track.trackNumber ?? i + 1}
                </span>
                <Cover
                  artist={track.artist ?? albumArtist}
                  title={payload?.album.title ?? track.title}
                  coverUrl={track.hasCoverArt ? shareCoverUrl(data.token, track.id, 160) : null}
                  size={44}
                  corner={6}
                  caption={false}
                  dprCap={3}
                  class="shrink-0 md:size-10!"
                />
                <span class="flex min-w-0 flex-1 flex-col">
                  <span
                    class={cn(
                      'text-body truncate md:text-sm md:font-medium',
                      isRowActive && 'font-semibold text-[var(--np-liked,var(--primary))]'
                    )}
                  >
                    {track.title}
                  </span>
                  <span class="text-subheadline text-muted-foreground truncate md:text-[13px]">
                    {track.artist ?? albumArtist}
                  </span>
                </span>
                <span class="text-footnote text-muted-foreground shrink-0 tabular-nums">
                  {formatDuration(track.durationMs ? track.durationMs / 1000 : null)}
                </span>
              </button>
            </li>
          {/each}
        </ol>
      </div>
    {/if}
  {/if}
{/snippet}

{#if !payload || !activeTrack}
  <main
    class="bg-background text-foreground relative flex min-h-dvh flex-col items-center justify-center gap-3 px-6 pt-[env(safe-area-inset-top)] pb-[env(safe-area-inset-bottom)]"
  >
    <Music class="text-muted-foreground size-10" />
    <h1 class="text-title-3">This link isn’t available</h1>
    <p class="text-subheadline text-muted-foreground max-w-sm text-center">
      The share link doesn’t exist or has been revoked by the person who created it.
    </p>
  </main>
{:else}
  <!-- Public share player: the exact same Now Playing surface, video backdrop, watch mode, lyrics,
       and SPEC-01 hardware skin (when the sharer had it on) — read-only, with no mutations. -->
  <div
    data-mh-desk={skinLook(undefined, '', forcedHardware)}
    data-mh-tv-on={videoShowing || undefined}
    class={[
      'mh-np text-foreground fixed inset-0 flex flex-col overflow-hidden pt-[env(safe-area-inset-top)] pr-[env(safe-area-inset-right)] pb-[env(safe-area-inset-bottom)] pl-[env(safe-area-inset-left)]',
      mediaAppearance(forcedHardware),
      surface
    ]}
    style="--mh-content-pad: 0px"
  >
    <VideoBackdrop
      songId={activeTrack.id}
      {ambientUrl}
      {dimAlpha}
      video={shareVideo}
      streamUrl={shareVideoStreamUrl(data.token, activeTrack.id)}
      isCurrent={isCurrentlyLoaded}
      blurred={videoWatching}
      bind:showing={videoShowing}
    />

    <div
      class="mh-surface-edges absolute inset-0"
      style="--mh-page-surface: var(--background)"
      aria-hidden="true"
    ></div>

    <main
      class="relative z-10 flex h-full min-h-0 flex-col {videoShowing
        ? '[text-shadow:0_0_4px_rgb(0_0_0/0.6),0_1px_14px_rgb(0_0_0/0.5)]'
        : ''}"
    >
      <NowPlayingLayout
        wide={wide.current}
        {stage}
        trackTitle={activeTrack.title}
        trackArtist={displayArtist}
        {albumTitle}
        {coverUrl}
        {isCurrentlyLoaded}
        {isCurrentlyPlaying}
        {artPlaying}
        durationSeconds={activeDurationSeconds > 0 ? activeDurationSeconds : null}
        {metaLine}
        showQueuePreview={isCurrentlyLoaded || isAlbumShare}
        {queuePreview}
        onShowArtwork={() => (mode = 'player')}
        headerLeft={sharedViaLink}
        headerCenter={chatBanner}
        headerRight={shareMoreMenu}
        {titleBlock}
        {transport}
        {bottomRow}
        {modeContent}
      />
    </main>
  </div>
{/if}
