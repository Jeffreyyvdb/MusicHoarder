<script lang="ts">
  import {
    AlertCircle,
    Check,
    CheckCircle2,
    Copy,
    History,
    Languages,
    Loader2,
    RotateCcw,
    Heart,
    Search,
    Share2,
    Sparkles,
    X
  } from '@lucide/svelte';
  import { createShareAndCopyLink } from '$lib/share-actions';
  import { Button } from '$lib/components/ui/button';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import * as Tabs from '$lib/components/ui/tabs/index.js';
  import * as ToggleGroup from '$lib/components/ui/toggle-group/index.js';
  import LyricsPanel from '$lib/components/file-browser/LyricsPanel.svelte';
  import LyricsCard from '$lib/components/file-browser/LyricsCard.svelte';
  import LyricsFullscreen from '$lib/components/file-browser/LyricsFullscreen.svelte';
  import SourceRow from '$lib/components/file-browser/SourceRow.svelte';
  import SongTransport from '$lib/components/file-browser/SongTransport.svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import VideoWatchTab from '$lib/components/file-browser/VideoWatchTab.svelte';
  import {
    artistLabelForSong,
    coverThumbUrl,
    coverUrlForSong,
    enrichSong,
    fetchEnrichmentDetail,
    getSongVideoInfoUntilSettled,
    toPlayerSong,
    mapEnrichmentStatus,
    resetSongEnrichment,
    fetchSongQualityGrade,
    gradeSong,
    copyQualitySongDossier,
    fetchTrackLyrics,
    transcribeSongLyrics,
    translateSongLyrics,
    setPreferredLyricsSource,
    soulseek,
    type ApiSong,
    type AlbumSummary,
    type EnrichmentDetail,
    type ProviderAttempt,
    type SongQualityGradeView,
    type QualityVerdict,
    type SongVideoInfo
  } from '$lib/api-client';
  import { fingerprintBars, fingerprintHash, providerAttemptRows } from '$lib/review-helpers';
  import { formatDuration, formatFileSize } from '$lib/formatters';
  import { lrclibWebUrl, lrclibWebSearchUrl } from '$lib/lrclib-url';
  import { acoustIdSourceConnected, lrclibSourceConnected } from '$lib/source-connection';
  import { playerStore } from '$lib/stores/player.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { featuresStore } from '$lib/stores/features.svelte';
  import { toast } from 'svelte-sonner';
  import { cn, scrollStripToActive } from '$lib/utils';
  import type { LyricsStatus } from '$lib/types';

  type Props = {
    album: AlbumSummary;
    song: ApiSong;
    trackIndex: number;
    onClose: () => void;
    onResetEnrichment?: () => void;
    /**
     * Link to the standalone /track/[id] provenance timeline. When set, the
     * Enrichment tab shows a "View timeline" link.
     */
    timelineHref?: string;
  };
  const { album, song, trackIndex, onClose, onResetEnrichment, timelineHref }: Props = $props();

  type TabId = 'metadata' | 'lyrics' | 'video' | 'fingerprint' | 'enrichment';

  // A music video attached to this song adds a Video tab (watch mode — the synced clip in a
  // full frame, vs the ambient backdrop behind the panel).
  let videoInfo = $state<SongVideoInfo | null>(null);
  $effect(() => {
    const id = song.id;
    videoInfo = null;
    // The load only settles on a definitive answer (info, or a 404 meaning none attached) and
    // retries transient failures until unmount — a dropped fetch after a page refresh would
    // otherwise hide the tab as if no video existed.
    const abort = new AbortController();
    getSongVideoInfoUntilSettled(id, { signal: abort.signal }).then(
      (info) => {
        if (!abort.signal.aborted) videoInfo = info;
      },
      () => {} // rejects only on abort
    );
    return () => abort.abort();
  });
  // fileMissing = the mp4 vanished from disk; the stream would 404, so no watch tab.
  const hasWatchableVideo = $derived(videoInfo?.status === 'Ready' && !videoInfo.fileMissing);

  const TAB_DEFS = $derived<{ value: TabId; label: string }[]>([
    { value: 'metadata', label: 'Metadata' },
    { value: 'lyrics', label: 'Lyrics' },
    ...(hasWatchableVideo ? [{ value: 'video' as const, label: 'Video' }] : []),
    { value: 'fingerprint', label: 'Fingerprint' },
    { value: 'enrichment', label: 'Enrichment' }
  ]);

  let activeTab = $state<TabId>('metadata');

  // If the Video tab is open and the song changes to one without a video (or the video is
  // removed), fall back rather than showing an empty tab.
  $effect(() => {
    if (activeTab === 'video' && videoInfo !== null && !hasWatchableVideo) {
      activeTab = 'metadata';
    }
  });

  // The tab strip scrolls horizontally when it doesn't fit (phones), so keep the
  // active tab in view — otherwise switching to Enrichment, or opening the panel
  // straight on Lyrics, can leave the highlighted tab off-screen.
  let tabsScroller = $state<HTMLElement | null>(null);
  $effect(() => {
    const scroller = tabsScroller;
    void activeTab;
    if (!scroller) return;
    const frame = requestAnimationFrame(() =>
      scrollStripToActive(scroller, scroller.querySelector<HTMLElement>('[data-state="active"]'))
    );
    return () => cancelAnimationFrame(frame);
  });

  let resetState = $state<'idle' | 'loading' | 'success' | 'error'>('idle');
  let resetError = $state<string | null>(null);
  let enrichState = $state<'idle' | 'loading' | 'success' | 'error'>('idle');
  let enrichOutcome = $state<string | null>(null);
  let enrichError = $state<string | null>(null);

  // Share: mint (or fetch the existing) public link for this song and copy it. The link plays
  // the song and shows its lyrics/metadata to anyone — no account needed.
  let shareState = $state<'idle' | 'loading'>('idle');

  const isLiked = $derived(Boolean(song.likedAtUtc));

  async function toggleLike() {
    try {
      await songsStore.toggleLike(song.id);
    } catch (err) {
      toast.error('Could not update liked songs', {
        description: err instanceof Error ? err.message : undefined
      });
    }
  }

  async function shareSong() {
    if (shareState === 'loading') return;
    shareState = 'loading';
    try {
      await createShareAndCopyLink(song.id, 'song');
    } finally {
      shareState = 'idle';
    }
  }

  // --- AI lyrics: one action, two stages ---
  //
  // "Enhance with AI" runs the transcription (which re-times the song's own official lyrics when it
  // has them, so it doubles as a re-sync) and then the pronunciation + translation, in one click.
  // The two server calls stay separate so the first result lands on screen while the second runs.
  type AiLyrics = { synced?: string; plain?: string; model?: string; at?: string };
  let aiLyrics = $state<AiLyrics | null>(null);
  // Which version the big synced viewer shows when both exist, the compare-view toggle, and save state.
  let preferredSource = $state<'lrclib' | 'transcribed'>('lrclib');
  let showCompare = $state(false);
  let preferSaving = $state(false);
  // Plain (non-reactive) guard so re-syncing on song change can't loop the effect below.
  let aiLoadedForSongId: number | null = null;

  // --- AI lyrics pronunciation (romanization) + English translation (display-only) ---
  type LyricsTranslation = {
    romanizedSynced?: string;
    romanizedPlain?: string;
    translatedSynced?: string;
    translatedPlain?: string;
    language?: string;
    model?: string;
    at?: string;
  };
  let translation = $state<LyricsTranslation | null>(null);
  let lyricsView = $state<'original' | 'pronunciation' | 'translation'>('original');
  // True when the stored translation was generated from lyrics that have since changed. Actions
  // that change the display lyrics (a preferred-source flip) regenerate it.
  let translationStale = $state(false);

  // Shared progress for the combined run: which stage is in flight, plus the outcome banner.
  let enhanceState = $state<'idle' | 'transcribing' | 'translating' | 'success' | 'error'>('idle');
  let enhanceError = $state<string | null>(null);
  // Non-fatal note when one stage failed but the other still produced something useful.
  let enhanceNote = $state<string | null>(null);
  let enhanceTimer: ReturnType<typeof setTimeout> | null = null;
  const enhanceBusy = $derived(enhanceState === 'transcribing' || enhanceState === 'translating');

  /** Clears the success/error banner after a beat; cancels any pending reset first. */
  function settleEnhance(ms: number) {
    if (enhanceTimer) clearTimeout(enhanceTimer);
    enhanceTimer = setTimeout(() => {
      enhanceTimer = null;
      enhanceState = 'idle';
      enhanceError = null;
      enhanceNote = null;
    }, ms);
  }

  // Provider attempts (real candidate matches) are loaded lazily when the
  // Fingerprint tab is first viewed, and refetched when the song changes.
  let enrichmentDetail = $state<EnrichmentDetail | null>(null);
  let detailLoading = $state(false);
  let detailError = $state<string | null>(null);
  let loadedSongId = $state<number | null>(null);

  async function loadEnrichmentDetail(id: number) {
    detailLoading = true;
    detailError = null;
    try {
      const detail = await fetchEnrichmentDetail(id);
      if (id !== song.id) return; // song changed while in flight — discard
      enrichmentDetail = detail;
      loadedSongId = id;
    } catch (err) {
      if (id !== song.id) return; // stale failure for a song we navigated away from
      detailError = err instanceof Error ? err.message : 'Failed to load provider attempts';
    } finally {
      detailLoading = false; // ALWAYS clear — gating this on id === song.id deadlocks the effect
    }
  }

  $effect(() => {
    if ((activeTab !== 'fingerprint' && activeTab !== 'enrichment') || detailLoading || loadedSongId === song.id)
      return;
    void loadEnrichmentDetail(song.id);
  });

  // ── Soulseek quality upgrade ────────────────────────────────────────────────
  // Shown only when slskd is configured; the /api/soulseek/* endpoints enforce owner-only.
  let soulseekConfigured = $state(false);
  let upgradeRequesting = $state(false);
  let upgradeError = $state<string | null>(null);

  $effect(() => {
    let cancelled = false;
    void soulseek
      .getStatus()
      .then((s) => {
        if (!cancelled) soulseekConfigured = s.configured;
      })
      .catch(() => {
        // Endpoint unavailable (not owner / not configured) — keep the action hidden.
      });
    return () => {
      cancelled = true;
    };
  });

  // Reflect an in-flight upgrade as a disabled button label.
  const upgradeActiveLabel = $derived.by(() => {
    const u = enrichmentDetail?.upgrade;
    if (!u?.active) return null;
    switch (u.status) {
      case 'Queued':
        return 'Queued…';
      case 'Searching':
        return 'Searching…';
      case 'Downloading':
        return 'Downloading…';
      case 'AwaitingIngest':
        return 'Processing…';
      default:
        return 'Upgrading…';
    }
  });

  const upgradeTerminalNote = $derived.by(() => {
    const u = enrichmentDetail?.upgrade;
    if (!u || u.active) return null;
    if (u.status === 'NotFound') return 'No better copy found on Soulseek.';
    if (u.status === 'Failed') return u.error ? `Upgrade failed — ${u.error}` : 'Upgrade failed.';
    if (u.status === 'Completed') return 'Upgraded to a better copy.';
    return null;
  });

  async function handleFindBetterQuality() {
    if (upgradeRequesting || !song) return;
    upgradeRequesting = true;
    upgradeError = null;
    try {
      await soulseek.requestUpgrade({ songId: song.id });
      loadedSongId = null; // force a refetch so the button reflects the new active state
      await loadEnrichmentDetail(song.id);
    } catch (err) {
      upgradeError = err instanceof Error ? err.message : 'Failed to queue upgrade';
    } finally {
      upgradeRequesting = false;
    }
  }

  // AI quality grade for the Enrichment tab — loaded lazily, refetched per song.
  let quality = $state<SongQualityGradeView | null>(null);
  let qualityLoadedId = $state<number | null>(null);
  let gradeBusy = $state(false);
  let copied = $state(false);

  async function handleCopyDossier() {
    try {
      await copyQualitySongDossier(song.id);
      copied = true;
      setTimeout(() => (copied = false), 1500);
    } catch {
      // keep the panel quiet; failure leaves the icon unchanged
    }
  }

  $effect(() => {
    if (activeTab !== 'enrichment' || qualityLoadedId === song.id) return;
    const id = song.id;
    void (async () => {
      try {
        const grade = await fetchSongQualityGrade(id);
        if (id !== song.id) return; // song changed while in flight — discard
        quality = grade;
        qualityLoadedId = id;
      } catch {
        // grade is optional UI; ignore load failures
      }
    })();
  });

  async function handleGradeNow() {
    gradeBusy = true;
    try {
      await gradeSong(song.id);
      quality = await fetchSongQualityGrade(song.id);
      qualityLoadedId = song.id;
    } catch {
      // surfaced via the unchanged grade card; keep the panel quiet
    } finally {
      gradeBusy = false;
    }
  }

  function verdictTint(v: QualityVerdict | undefined): string {
    switch (v) {
      case 'Excellent':
        return 'bg-emerald-500/15 text-emerald-600 dark:text-emerald-400 border-emerald-500/30';
      case 'Good':
        return 'bg-teal-500/15 text-teal-600 dark:text-teal-400 border-teal-500/30';
      case 'Questionable':
        return 'bg-amber-500/15 text-amber-600 dark:text-amber-400 border-amber-500/30';
      case 'Wrong':
        return 'bg-red-500/15 text-red-600 dark:text-red-400 border-red-500/30';
      default:
        return 'bg-muted text-muted-foreground border-border';
    }
  }

  const trackN = $derived(song.trackNumber ?? trackIndex + 1);
  const totalTracks = $derived(album.trackCount);
  const isCurrentlyLoaded = $derived(playerStore.currentSong?.id === song.id);
  const isCurrentlyPlaying = $derived(isCurrentlyLoaded && playerStore.isPlaying);

  const trackTitle = $derived((song.title ?? song.fileName).trim() || song.fileName);
  const trackArtist = $derived((song.artist ?? album.artist).trim() || album.artist);
  // Deep-links into the Library, filtered to this track's artist / album. Match
  // the grouping keys used by the Library views (artist = albumArtist ?? artist,
  // album = the canonical AlbumSummary key) so the target page is populated.
  const artistHref = $derived(
    `/library?artist=${encodeURIComponent(artistLabelForSong(song))}`
  );
  const albumHref = $derived(`/library?album=${encodeURIComponent(album.key)}`);
  const lyricsStatus = $derived((song.lyricsStatus ?? 'NotFetched') as LyricsStatus);
  const coverUrl = $derived(coverUrlForSong(song) ?? album.coverUrl ?? null);
  const ambientUrl = $derived(coverThumbUrl(coverUrl, 600));

  // Mobile fullscreen lyrics overlay (shared with the public share page).
  let lyricsExpanded = $state(false);

  // Smart default tab (Apple-Music style): open on Lyrics when the track has any
  // lyrics, otherwise Metadata. Re-applied only when the *song id* changes — so
  // follow-playback re-targeting picks a sensible tab while a manual tab switch
  // on the same song is never clobbered (e.g. when lyrics arrive via SSE).
  const hasLyrics = $derived(
    Boolean(song.hasSyncedLyrics) || Boolean(song.hasPlainLyrics) || lyricsStatus === 'Fetched'
  );
  let smartTabForSongId: number | null = null;
  $effect(() => {
    if (smartTabForSongId === song.id) return;
    smartTabForSongId = song.id;
    activeTab = hasLyrics ? 'lyrics' : 'metadata';
  });

  function bitrateLabel(): string {
    const ext = (song.extension ?? '').replace(/^\./, '').toUpperCase();
    if (song.bitRate && song.bitRate > 0) {
      return ext ? `${ext} ${song.bitRate}kbps` : `${song.bitRate} kbps`;
    }
    return ext || '—';
  }

  function handlePlayToggle() {
    if (isCurrentlyLoaded) {
      playerStore.togglePlay();
      return;
    }
    const queue = album.songs.map((s) => toPlayerSong(s, album.artist));
    void playerStore.playSong(toPlayerSong(song, album.artist), queue, trackIndex);
  }

  async function handleResetEnrichment() {
    resetState = 'loading';
    resetError = null;
    try {
      await resetSongEnrichment(song.id);
      resetState = 'success';
      onResetEnrichment?.();
      setTimeout(() => (resetState = 'idle'), 3000);
    } catch (err) {
      resetState = 'error';
      resetError = err instanceof Error ? err.message : 'Failed to reset enrichment';
      setTimeout(() => {
        resetState = 'idle';
        resetError = null;
      }, 5000);
    }
  }

  async function handleEnrichNow() {
    enrichState = 'loading';
    enrichError = null;
    enrichOutcome = null;
    try {
      // reset=true gives a clean re-run from scratch and returns the exact outcome —
      // works even when the automatic pipeline is disabled.
      const result = await enrichSong(song.id, true);
      enrichState = 'success';
      enrichOutcome = result.outcome;
      onResetEnrichment?.();
      setTimeout(() => {
        enrichState = 'idle';
        enrichOutcome = null;
      }, 4000);
    } catch (err) {
      enrichState = 'error';
      enrichError = err instanceof Error ? err.message : 'Failed to enrich song';
      setTimeout(() => {
        enrichState = 'idle';
        enrichError = null;
      }, 5000);
    }
  }

  // Load any existing AI transcription + pronunciation/translation when the song changes, and reset
  // transient state so a prior song's data never bleeds into the next (the panel instance is reused
  // across songs). Keyed on the plain `aiLoadedForSongId` (not $state) so it can't re-trigger itself.
  $effect(() => {
    const id = song.id;
    if (aiLoadedForSongId === id) return;
    aiLoadedForSongId = id;
    aiLyrics = null;
    showCompare = false;
    translation = null;
    lyricsView = 'original';
    translationStale = false;
    // A pending banner reset from the previous song must not fire onto this one's fresh state.
    if (enhanceTimer) clearTimeout(enhanceTimer);
    enhanceTimer = null;
    enhanceState = 'idle';
    enhanceError = null;
    enhanceNote = null;
    preferredSource = song.preferredLyricsSource === 'Transcribed' ? 'transcribed' : 'lrclib';
    // The songs list carries no translation flag, so any song with lyrics may have one — one small
    // fetch covers both the transcription and the translation.
    if (!song.hasTranscribedLyrics && !song.hasSyncedLyrics && !song.hasPlainLyrics) return;
    fetchTrackLyrics(id)
      .then((d) => {
        if (aiLoadedForSongId !== id) return; // navigated away while in flight
        if (d.transcribedSynced || d.transcribedPlain) {
          aiLyrics = {
            synced: d.transcribedSynced ?? undefined,
            plain: d.transcribedPlain ?? undefined,
            model: d.transcriptionModel ?? undefined,
            at: d.transcribedAtUtc ?? undefined
          };
        }
        if (d.lyricsTranslationStatus === 'Completed') {
          translation = {
            romanizedSynced: d.romanizedSynced ?? undefined,
            romanizedPlain: d.romanizedPlain ?? undefined,
            translatedSynced: d.translatedSynced ?? undefined,
            translatedPlain: d.translatedPlain ?? undefined,
            language: d.detectedLanguage ?? undefined,
            model: d.lyricsTranslationModel ?? undefined,
            at: d.lyricsTranslatedAtUtc ?? undefined
          };
          translationStale = d.lyricsTranslationStale === true;
        }
      })
      .catch(() => {});
  });

  /** Stage 1 — transcribe/re-sync the audio and reflect the server's (possibly promoted) default. */
  async function runTranscribe() {
    const r = await transcribeSongLyrics(song.id);
    aiLyrics = {
      synced: r.synced ?? undefined,
      plain: r.plain ?? undefined,
      model: r.model ?? undefined,
      at: r.transcribedAtUtc ?? undefined
    };
    // A re-sync of the song's own official lyrics is promoted server-side; mirror that here so the
    // viewer shows the freshly-timed version straight away.
    if (r.preferredLyricsSource) {
      preferredSource = r.preferredLyricsSource === 'Transcribed' ? 'transcribed' : 'lrclib';
    }
    // The new lyrics may have replaced the text an existing pronunciation/translation was generated
    // from. Stage 2 regenerates it either way; this just keeps the mismatched doc off screen.
    if (translation != null) translationStale = r.lyricsTranslationStale === true;
  }

  /** Stage 2 — pronunciation + English translation of whatever stage 1 left on screen. */
  async function runTranslate() {
    const r = await translateSongLyrics(song.id);
    translation = {
      romanizedSynced: r.romanizedSynced ?? undefined,
      romanizedPlain: r.romanizedPlain ?? undefined,
      translatedSynced: r.translatedSynced ?? undefined,
      translatedPlain: r.translatedPlain ?? undefined,
      language: r.detectedLanguage ?? undefined,
      model: r.model ?? undefined,
      at: r.lyricsTranslatedAtUtc ?? undefined
    };
    if (lyricsView === 'original' && (r.romanizedSynced || r.romanizedPlain)) {
      lyricsView = 'pronunciation'; // show the fresh result right away
    }
    translationStale = false;
  }

  /**
   * The one AI lyrics action: re-sync the timings, then generate the pronunciation guide and English
   * translation — no second click. Either half can be unavailable (provider not configured, nothing
   * to translate), and a failed transcription still lets the translation run off the existing lyrics,
   * so the outcome is reported per stage rather than as a single pass/fail.
   */
  async function handleEnhance() {
    if (enhanceBusy) return;
    if (enhanceTimer) clearTimeout(enhanceTimer);
    enhanceTimer = null;
    enhanceError = null;
    enhanceNote = null;

    let transcribeFailure: string | null = null;
    if (canTranscribe) {
      enhanceState = 'transcribing';
      try {
        await runTranscribe();
      } catch (err) {
        transcribeFailure = err instanceof Error ? err.message : 'Transcription failed';
      }
    }
    if (song.id !== aiLoadedForSongId) return; // navigated away mid-run

    if (canTranslate) {
      enhanceState = 'translating';
      try {
        await runTranslate();
      } catch (err) {
        if (song.id !== aiLoadedForSongId) return;
        enhanceState = 'error';
        enhanceError = err instanceof Error ? err.message : 'Translation failed';
        settleEnhance(6000);
        return;
      }
    }
    if (song.id !== aiLoadedForSongId) return;

    if (transcribeFailure && !canTranslate) {
      enhanceState = 'error';
      enhanceError = transcribeFailure;
      settleEnhance(6000);
      return;
    }
    enhanceState = 'success';
    // Half the run landed — say which half missed instead of flashing a plain "Done".
    if (transcribeFailure) enhanceNote = `Lyrics re-sync failed — ${transcribeFailure}`;
    settleEnhance(transcribeFailure ? 6000 : 3000);
  }

  // The experimental AI lyrics feature is only shown when a transcription provider is configured server-side.
  $effect(() => {
    void featuresStore.ensureLoaded();
  });
  const lyricsFeatureEnabled = $derived(featuresStore.lyricsTranscription);
  const translationFeatureEnabled = $derived(featuresStore.lyricsTranslation);

  // An already-English song completes with a language code but no generated documents.
  const translationIsEnglish = $derived(
    translation != null &&
      translation.language === 'en' &&
      !translation.romanizedSynced &&
      !translation.romanizedPlain &&
      !translation.translatedSynced &&
      !translation.translatedPlain
  );
  const hasTranslation = $derived(translation != null && !translationIsEnglish);

  // What the single AI action can actually do for this track. Translation needs lyrics to work from,
  // which stage 1 may itself have just produced — hence the `aiLyrics` term, read fresh between the
  // two stages (a demo row with no file on disk fails stage 1 and falls through to stage 2).
  const canTranscribe = $derived(lyricsFeatureEnabled && song.isInstrumental !== true);
  const canTranslate = $derived(
    translationFeatureEnabled && song.isInstrumental !== true && (hasLyrics || aiLyrics != null)
  );
  const canEnhance = $derived(canTranscribe || canTranslate);
  const hasAiOutput = $derived(aiLyrics != null || translation != null);

  // The button says what this particular track will get, since either half can be unconfigured.
  const enhanceLabel = $derived.by(() => {
    if (!canTranslate) return hasAiOutput ? 'Re-sync with AI' : 'Transcribe with AI';
    if (!canTranscribe) return hasAiOutput ? 'Regenerate' : 'Pronunciation & translation';
    if (hasAiOutput) return 'Redo with AI';
    return hasLyrics ? 'Improve with AI' : 'Generate with AI';
  });

  // Secondary document stacked under the original lines, picked by the current lyrics view. The
  // translation was generated from the *display* lyrics, so it isn't offered inside the compare
  // split, and a STALE document (generated from since-changed lyrics) is never stacked — its lines
  // would misalign with what's on screen.
  const secondarySynced = $derived.by(() => {
    if (comparingLyrics || !hasTranslation || translationStale) return undefined;
    if (lyricsView === 'pronunciation') return translation?.romanizedSynced;
    if (lyricsView === 'translation') return translation?.translatedSynced;
    return undefined;
  });
  const secondaryPlain = $derived.by(() => {
    if (comparingLyrics || !hasTranslation || translationStale) return undefined;
    if (lyricsView === 'pronunciation') return translation?.romanizedPlain;
    if (lyricsView === 'translation') return translation?.translatedPlain;
    return undefined;
  });

  // Comparison only makes sense once an AI transcription exists alongside LRCLIB lyrics.
  const canCompareLyrics = $derived(lyricsFeatureEnabled && aiLyrics != null && hasLyrics);
  const comparingLyrics = $derived(showCompare && canCompareLyrics);
  // The big synced viewer shows the AI version when it's the chosen default (or it's all we have).
  const showAiInViewer = $derived(
    lyricsFeatureEnabled && aiLyrics != null && (!hasLyrics || preferredSource === 'transcribed')
  );

  // The mobile lyrics card / fullscreen overlay present the same source as the big viewer.
  const lyricsExpandable = $derived(
    song.isInstrumental !== true &&
      (showAiInViewer ? Boolean(aiLyrics?.synced || aiLyrics?.plain) : hasLyrics)
  );
  $effect(() => {
    if (!lyricsExpandable) lyricsExpanded = false;
  });

  async function handleSetPreferred(source: 'lrclib' | 'transcribed') {
    if (preferredSource === source || preferSaving) return;
    const previous = preferredSource;
    preferredSource = source; // optimistic
    preferSaving = true;
    try {
      const r = await setPreferredLyricsSource(song.id, source);
      // The flip changed which lyrics are displayed — regenerate a now-stale translation so the
      // stacked pronunciation/translation always matches what's on screen. (False also syncs:
      // flipping back to the source the translation was generated from makes it fresh again.)
      if (translation != null) {
        translationStale = r.lyricsTranslationStale === true;
        if (translationStale && !enhanceBusy) {
          enhanceState = 'translating';
          void runTranslate()
            .then(() => {
              enhanceState = 'success';
              settleEnhance(3000);
            })
            .catch((err) => {
              enhanceState = 'error';
              enhanceError = err instanceof Error ? err.message : 'Translation failed';
              settleEnhance(6000);
            });
        }
      }
    } catch {
      preferredSource = previous; // revert on failure
    } finally {
      preferSaving = false;
    }
  }

  const matchValue = $derived.by(() => {
    const v = song.matchConfidence ?? enrichmentDetail?.matchConfidence;
    return typeof v === 'number' ? Math.max(0, Math.min(1, v)) : null;
  });

  const enrichmentNormalized = $derived(mapEnrichmentStatus(song.enrichmentStatus));

  // The enrich action also builds the track into the library, so the label reflects the outcome:
  // "Add to library" for a track not yet built, "Update in library" once it has a destination.
  const inLibrary = $derived(!!song.destinationPath);

  // Real provider attempts → candidate rows, guarded so stale data from a
  // previously-viewed song isn't shown while the new one loads.
  const attemptRows = $derived(
    loadedSongId === song.id ? providerAttemptRows(enrichmentDetail) : []
  );

  // Provider attempts keyed by backend provider name, for the Enrichment tab's
  // connected dots. Empty until the detail loads for the current song.
  const attemptByProvider = $derived.by(() => {
    const map = new Map<string, ProviderAttempt>();
    if (loadedSongId !== song.id || !enrichmentDetail) return map;
    for (const a of enrichmentDetail.providerAttempts) map.set(a.provider, a);
    return map;
  });

  type EnrichmentSource = { key: string; name: string; connected: boolean; url?: string; label?: string };

  // The full catalogue of enrichment sources wired into the pipeline. AcoustID /
  // MusicBrainz / Spotify resolve their connected state from stored song ids;
  // Deezer / Apple Music / Tracker have no stored id, so they reflect whether the
  // provider produced a candidate on its last attempt. Tracker is opt-in and niche,
  // so it only appears once it has actually run for this song.
  const enrichmentSources = $derived.by<EnrichmentSource[]>(() => {
    const query = encodeURIComponent(`${trackArtist} ${trackTitle}`.trim());
    const matched = (provider: string) => attemptByProvider.get(provider)?.candidate != null;

    const sources: EnrichmentSource[] = [
      {
        key: 'acoustid',
        name: 'AcoustID',
        connected: acoustIdSourceConnected(song.acoustIdTrackId ?? undefined, song.matchedBy ?? undefined),
        url: song.acoustIdTrackId ? `https://acoustid.org/track/${song.acoustIdTrackId}` : 'https://acoustid.org',
        label: song.acoustIdTrackId ? `acoustid.org/track/${song.acoustIdTrackId.slice(0, 8)}…` : undefined
      },
      {
        key: 'musicbrainz-recording',
        name: 'MusicBrainz Recording',
        connected: Boolean(song.musicBrainzId),
        url: song.musicBrainzId
          ? `https://musicbrainz.org/recording/${song.musicBrainzId}`
          : 'https://musicbrainz.org',
        label: song.musicBrainzId ? `musicbrainz.org/recording/${song.musicBrainzId.slice(0, 8)}…` : undefined
      }
    ];

    if (song.musicBrainzReleaseId) {
      sources.push({
        key: 'musicbrainz-release',
        name: 'MusicBrainz Release',
        connected: true,
        url: `https://musicbrainz.org/release/${song.musicBrainzReleaseId}`,
        label: `musicbrainz.org/release/${song.musicBrainzReleaseId.slice(0, 8)}…`
      });
    }

    sources.push({
      key: 'spotify',
      name: 'Spotify',
      connected: Boolean(song.spotifyId),
      url: song.spotifyId ? `https://open.spotify.com/track/${song.spotifyId}` : 'https://spotify.com',
      label: song.spotifyId ? `open.spotify.com/track/${song.spotifyId.slice(0, 8)}…` : undefined
    });

    sources.push({
      key: 'deezer',
      name: 'Deezer',
      connected: matched('Deezer'),
      url: query ? `https://www.deezer.com/search/${query}` : 'https://www.deezer.com',
      label: query ? 'deezer.com/search/…' : undefined
    });

    sources.push({
      key: 'apple-music',
      name: 'Apple Music',
      connected: matched('AppleMusic'),
      url: query ? `https://music.apple.com/search?term=${query}` : 'https://music.apple.com',
      label: query ? 'music.apple.com/search/…' : undefined
    });

    sources.push({
      key: 'lrclib',
      name: 'LRCLIB (Lyrics)',
      connected: lrclibSourceConnected({
        lrclibId: song.lrclibId ?? undefined,
        lyricsStatus,
        artist: trackArtist,
        title: trackTitle,
        enrichmentStatus: enrichmentNormalized
      }),
      url: lrclibWebUrl(trackArtist, trackTitle),
      label: lrclibWebSearchUrl(trackArtist, trackTitle) ? 'lrclib.net/search/…' : undefined
    });

    if (attemptByProvider.has('Tracker')) {
      sources.push({
        key: 'tracker',
        name: 'Community Tracker',
        connected: matched('Tracker')
      });
    }

    return sources;
  });

  // Optional descriptive rows: only shown when a value exists, so a track that never got these
  // enrichment fields doesn't gain a wall of "—" placeholders.
  const optionalRow = (label: string, value: string | null | undefined): [string, string, string?][] =>
    value && value.trim() ? [[label, value.trim()]] : [];

  const metadataRows = $derived<[string, string, string?][]>([
    ['Title', trackTitle],
    ['Artist', trackArtist, artistHref],
    ['Album', album.title, albumHref],
    ['Track', `${trackN} / ${totalTracks}`],
    ['Year', album.year != null ? String(album.year) : '—'],
    ...optionalRow('Release date', song.releaseDate),
    ['Genre', song.genre ?? album.genre ?? '—'],
    ...optionalRow('Composer', song.composer),
    ...optionalRow('Label', song.label ?? album.label),
    ...optionalRow('Catalog #', song.catalogNumber ?? album.catalogNumber),
    ...optionalRow('Barcode', song.upc ?? album.upc),
    ...optionalRow('Copyright', song.copyright),
    ['MusicBrainz ID', song.musicBrainzId ?? '—'],
    ['MusicBrainz release', song.musicBrainzReleaseId ?? album.musicBrainzReleaseId ?? '—'],
    ['AcoustID', song.acoustIdTrackId ?? '—'],
    ['Fingerprint', song.fingerprint ? `${song.fingerprint.slice(0, 22)}…` : '—'],
    ['ISRC', song.isrc ?? '—'],
    ['Format', bitrateLabel()],
    ['Sample rate', song.sampleRate ? `${(song.sampleRate / 1000).toFixed(1)} kHz` : '—'],
    ['File size', formatFileSize(song.fileSizeBytes)],
    ['Status', enrichmentNormalized]
  ]);

</script>

{#snippet transport()}
  <div class="mx-auto w-full max-w-[340px]">
    <SongTransport
      isActive={isCurrentlyLoaded}
      isPlaying={isCurrentlyPlaying}
      fallbackDuration={song.durationSeconds ?? 0}
      onPlayToggle={handlePlayToggle}
    />
  </div>
{/snippet}

<!--
  The single AI lyrics action, shared by the mobile card and the desktop control bar. One click runs
  the sync pass and then the pronunciation + translation pass, so the label reports which stage is
  in flight rather than a generic spinner.
-->
{#snippet enhanceButton()}
  <Button
    variant="subtle"
    size="sm"
    class={cn(
      enhanceState === 'success' && 'text-primary',
      enhanceState === 'error' && 'text-destructive'
    )}
    disabled={enhanceBusy}
    onclick={handleEnhance}
  >
    {#if enhanceState === 'transcribing'}
      <Loader2 class="mr-1.5 size-3.5 animate-spin" />
      Syncing lyrics…
    {:else if enhanceState === 'translating'}
      <Loader2 class="mr-1.5 size-3.5 animate-spin" />
      Translating…
    {:else if enhanceState === 'success'}
      <CheckCircle2 class="mr-1.5 size-3.5" />
      Done
    {:else if enhanceState === 'error'}
      <AlertCircle class="mr-1.5 size-3.5" />
      Failed
    {:else}
      <Sparkles class="mr-1.5 size-3.5" />
      {enhanceLabel}
    {/if}
  </Button>
{/snippet}

<div class="flex h-full max-h-full min-h-0 flex-col bg-transparent text-foreground mh-track-panel-enter">
  <!-- Tabs span the full overlay: a top segmented tab bar; a compact header on
       mobile (art + title) / a persistent left rail on desktop (art + transport),
       beside the active tab's content; transport pinned at the bottom on mobile. -->
  <Tabs.Root bind:value={activeTab} class="flex min-h-0 flex-1 flex-col">
    <!-- Top bar: close (left) + segmented tabs + like/share (right). All three
         sit in normal flow — the tab strip used to be centred with the buttons
         absolutely positioned over it, which on a phone let the widest tab slide
         underneath the like button. The strip now takes the space that's left
         and scrolls horizontally instead, centring itself when it fits. -->
    <div class="flex shrink-0 items-center gap-1 px-2 py-2.5 sm:gap-2 sm:px-5 sm:py-3">
      <Button
        variant="ghost"
        size="icon"
        onclick={onClose}
        class="bg-foreground/5 hover:bg-foreground/10 size-9 shrink-0 rounded-full"
        aria-label="Close"
      >
        <X class="size-4" />
      </Button>
      <div bind:this={tabsScroller} class="no-scrollbar min-w-0 flex-1 overflow-x-auto">
        <Tabs.List class="bg-foreground/5 mx-auto h-auto w-max gap-1 rounded-full p-1">
          {#each TAB_DEFS as tab (tab.value)}
            <Tabs.Trigger
              value={tab.value}
              class="text-muted-foreground hover:text-foreground data-[state=active]:bg-background data-[state=active]:text-foreground shrink-0 rounded-full border-0 bg-transparent px-3 py-1.5 text-xs font-medium whitespace-nowrap shadow-none transition-colors data-[state=active]:shadow-sm sm:px-4 sm:text-[13px]"
            >
              {tab.label}
            </Tabs.Trigger>
          {/each}
        </Tabs.List>
      </div>
      <Button
        variant="ghost"
        size="icon"
        onclick={toggleLike}
        class="bg-foreground/5 hover:bg-foreground/10 size-9 shrink-0 rounded-full active:scale-90"
        aria-label={isLiked ? 'Remove from liked songs' : 'Add to liked songs'}
        aria-pressed={isLiked}
        title={isLiked ? 'Remove from liked songs' : 'Add to liked songs'}
      >
        <Heart class={cn('size-4', isLiked && 'text-primary')} fill={isLiked ? 'currentColor' : 'none'} />
      </Button>
      <Button
        variant="ghost"
        size="icon"
        onclick={shareSong}
        disabled={shareState === 'loading'}
        class="bg-foreground/5 hover:bg-foreground/10 size-9 shrink-0 rounded-full"
        aria-label="Share song — copy a public link"
        title="Share — copy a public link that plays this song for anyone, no account needed."
      >
        {#if shareState === 'loading'}
          <Loader2 class="size-4 animate-spin" />
        {:else}
          <Share2 class="size-4" />
        {/if}
      </Button>
    </div>

    <!-- Body: compact header (mobile) / left rail (desktop) + tab content + transport -->
    <div
      class="flex min-h-0 flex-1 flex-col gap-4 overflow-hidden px-4 pb-4 sm:px-6 lg:flex-row lg:gap-10 lg:px-12 lg:pb-10"
    >
      <!-- Desktop left rail: big album art, title/artist, badges, transport -->
      <div
        class="hidden shrink-0 flex-col gap-4 lg:flex lg:w-[340px] lg:items-stretch lg:justify-center"
      >
        <Cover
          artist={trackArtist}
          title={album.title}
          {coverUrl}
          size={340}
          corner={12}
          caption={false}
          class="aspect-square !h-auto !w-full !shadow-[0_24px_48px_rgba(0,0,0,0.45)]"
        />
        <div class="min-w-0 text-left">
          <h2 class="truncate text-2xl font-bold tracking-[-0.02em]">{trackTitle}</h2>
          <p class="text-muted-foreground mt-1 truncate text-sm">
            <a href={artistHref} onclick={onClose} class="hover:text-foreground hover:underline">
              {trackArtist}
            </a>
            ·
            <a
              href={albumHref}
              onclick={onClose}
              class="text-muted-foreground/70 hover:text-foreground hover:underline"
            >
              {album.title}
            </a>
          </p>
          <div
            class="text-muted-foreground mt-2.5 flex flex-wrap items-center gap-2 text-[11px]"
          >
            <span class="bg-primary/15 text-primary rounded px-1.5 py-0.5 font-mono text-[9px] font-semibold tracking-wider">
              {bitrateLabel().split(' ')[0] || 'FILE'}
            </span>
            <span class="font-mono">{formatDuration(song.durationSeconds)}</span>
            <span class="font-mono">{formatFileSize(song.fileSizeBytes)}</span>
            {#if song.hasSyncedLyrics || song.lrclibId}
              <span class="bg-primary/15 text-primary rounded px-1.5 py-0.5 font-mono text-[9px] font-semibold tracking-wider">
                LRC
              </span>
            {/if}
          </div>
        </div>

        {@render transport()}
      </div>

      <!-- Mobile compact header: small art + title/artist. Hidden on the Lyrics tab,
           whose share-style hero carries the art, title, and transport itself. -->
      <div
        class={cn(
          'shrink-0 items-center gap-3 lg:hidden',
          activeTab === 'lyrics' ? 'hidden' : 'flex'
        )}
      >
        <Cover
          artist={trackArtist}
          title={album.title}
          {coverUrl}
          size={56}
          corner={8}
          caption={false}
          class="shrink-0 !shadow-md"
        />
        <div class="min-w-0 flex-1">
          <h2 class="truncate text-base leading-tight font-semibold tracking-[-0.01em]">
            {trackTitle}
          </h2>
          <p class="text-muted-foreground truncate text-xs">
            <a href={artistHref} onclick={onClose} class="hover:text-foreground hover:underline">
              {trackArtist}
            </a>
            ·
            <a
              href={albumHref}
              onclick={onClose}
              class="text-muted-foreground/70 hover:text-foreground hover:underline"
            >
              {album.title}
            </a>
          </p>
        </div>
      </div>

      <!-- Active tab content (maximized middle on mobile, right column on desktop) -->
      <div class="flex min-h-0 flex-1 flex-col overflow-hidden">

    <Tabs.Content value="metadata" class="flex min-h-0 flex-1 flex-col">
      <ScrollArea class="min-h-0 flex-1">
        <div class="mx-auto w-full max-w-2xl py-2">
          <!-- Narrower label column on a phone (140px left the value column too
               tight to hold a title on one line), and both cells carry the row
               padding so a wrapped value can't drift out of line with its label. -->
          <div class="grid grid-cols-[6.25rem_minmax(0,1fr)] gap-x-3 gap-y-0.5 sm:grid-cols-[140px_minmax(0,1fr)]">
            {#each metadataRows as [k, v, href] (k)}
              <div class="text-muted-foreground py-1.5 text-[11.5px]">{k}</div>
              {#if href}
                <a href={href} onclick={onClose} class="hover:text-foreground py-1.5 font-mono text-[12px] break-all hover:underline">{v}</a>
              {:else}
                <div class="py-1.5 font-mono text-[12px] break-all">{v}</div>
              {/if}
            {/each}
          </div>
          {#if song.destinationPath}
            <div class="border-border mt-4 border-t pt-4">
              <div class="text-muted-foreground text-[11.5px]">Destination path</div>
              <div class="bg-primary/10 text-primary mt-1.5 rounded px-2.5 py-2 font-mono text-[11px] break-all">
                {song.destinationPath}
              </div>
            </div>
          {/if}
          {#if song.sourcePath}
            <div class="mt-3">
              <div class="text-muted-foreground text-[11.5px]">Source path</div>
              <div class="bg-muted text-muted-foreground mt-1.5 rounded px-2.5 py-2 font-mono text-[11px] break-all">
                {song.sourcePath}
              </div>
            </div>
          {/if}
        </div>
      </ScrollArea>
    </Tabs.Content>

    <Tabs.Content value="lyrics" class="flex min-h-0 flex-1 flex-col gap-2">
      <!-- Mobile: the share-screen treatment — hero art, transport, and a lyrics card
           that expands to the fullscreen lyrics overlay. -->
      <div class="min-h-0 flex-1 overflow-y-auto lg:hidden">
        <div class="mx-auto flex w-full max-w-xl flex-col items-center px-2 pt-4 pb-8">
          <Cover
            artist={trackArtist}
            title={album.title}
            {coverUrl}
            size={288}
            corner={12}
            caption={false}
            class="aspect-square !h-auto w-56 shrink-0 !shadow-[0_24px_48px_rgba(0,0,0,0.45)] sm:w-64"
          />
          <div class="mt-5 w-full text-center">
            <h2 class="truncate text-2xl font-bold tracking-[-0.02em]">{trackTitle}</h2>
            <p class="text-muted-foreground mt-1 truncate text-sm">
              <a href={artistHref} onclick={onClose} class="hover:text-foreground hover:underline">
                {trackArtist}
              </a>
              ·
              <a
                href={albumHref}
                onclick={onClose}
                class="text-muted-foreground/70 hover:text-foreground hover:underline"
              >
                {album.title}
              </a>
            </p>
          </div>
          <div class="mt-6 w-full">
            {@render transport()}
          </div>
          <div class="mt-8 w-full">
            {#if canEnhance || hasTranslation}
              <!-- Compact AI lyrics controls (the desktop control bar is lg-only). -->
              <div class="mb-3 flex w-full flex-wrap items-center justify-center gap-2">
                {#if hasTranslation}
                  <ToggleGroup.Root
                    type="single"
                    size="sm"
                    variant="segmented"
                    value={lyricsView}
                    onValueChange={(v) => {
                      if (v) lyricsView = v as typeof lyricsView;
                    }}
                  >
                    <ToggleGroup.Item value="original" aria-label="Original lyrics">Original</ToggleGroup.Item>
                    <ToggleGroup.Item value="pronunciation" aria-label="Pronunciation guide">
                      Pronunciation
                    </ToggleGroup.Item>
                    <ToggleGroup.Item value="translation" aria-label="English translation">
                      Translation
                    </ToggleGroup.Item>
                  </ToggleGroup.Root>
                {:else if translationIsEnglish}
                  <span class="text-muted-foreground text-xs">Lyrics are already in English.</span>
                {/if}
                {#if canEnhance}
                  {@render enhanceButton()}
                {/if}
              </div>
              {#if enhanceError}
                <p class="text-destructive mb-3 text-center text-[11px]">{enhanceError}</p>
              {:else if enhanceNote}
                <p class="text-muted-foreground mb-3 text-center text-[11px]">{enhanceNote}</p>
              {/if}
            {/if}
            <LyricsCard expandable={lyricsExpandable} onExpand={() => (lyricsExpanded = true)}>
              {#key `${showAiInViewer ? `ai-${aiLyrics?.at}` : 'lrclib'}-${lyricsView}`}
                <div class="flex h-full flex-col">
                  <LyricsPanel
                    variant="theater"
                    songId={song.id}
                    syncedLyrics={showAiInViewer ? aiLyrics?.synced : (song.syncedLyrics ?? undefined)}
                    plainLyrics={showAiInViewer ? aiLyrics?.plain : (song.plainLyrics ?? undefined)}
                    lyricsStatus={showAiInViewer ? 'Fetched' : lyricsStatus}
                    hasSyncedLyrics={showAiInViewer ? Boolean(aiLyrics?.synced) : (song.hasSyncedLyrics ?? false)}
                    hasPlainLyrics={showAiInViewer ? Boolean(aiLyrics?.plain) : (song.hasPlainLyrics ?? false)}
                    isInstrumental={song.isInstrumental ?? undefined}
                    currentTimeMs={isCurrentlyLoaded ? playerStore.currentTime * 1000 : null}
                    {secondarySynced}
                    {secondaryPlain}
                  />
                </div>
              {/key}
            </LyricsCard>
          </div>
        </div>
      </div>

      <!-- Desktop: AI-lyrics tooling + the big theater viewer -->
      <div class="hidden min-h-0 flex-1 flex-col gap-2 lg:flex">
      {#if canEnhance || hasTranslation}
        <!-- Control bar: one AI action (re-sync the timings + generate pronunciation and
             translation) plus, once both an LRCLIB version and an AI one exist, the compare
             toggle that lets you put the curated timings back. -->
        <div
          class={cn(
            'mx-auto flex w-full items-center justify-between gap-2 px-1',
            comparingLyrics ? 'max-w-6xl' : 'max-w-3xl'
          )}
        >
          <div class="text-muted-foreground flex min-w-0 items-center gap-1.5 text-xs">
            {#if enhanceState === 'transcribing'}
              <Sparkles class="text-primary size-3.5 shrink-0" />
              <span class="truncate">Step 1 of 2 — listening to the audio to sync the lyrics…</span>
            {:else if enhanceState === 'translating'}
              <Languages class="text-primary size-3.5 shrink-0" />
              <span class="truncate">Step 2 of 2 — writing the pronunciation guide + translation…</span>
            {:else if translationStale && hasTranslation}
              <Languages class="size-3.5 shrink-0 text-amber-600 dark:text-amber-500" />
              <span class="truncate">Lyrics changed — pronunciation & translation are outdated. Run it again to refresh.</span>
            {:else if translationIsEnglish}
              <Languages class="text-primary size-3.5 shrink-0" />
              <span class="truncate">Lyrics are already in English.</span>
            {:else if canCompareLyrics}
              <Sparkles class="text-primary size-3.5 shrink-0" />
              <span class="truncate">
                Player shows: {preferredSource === 'transcribed'
                  ? `AI-synced · ${aiLyrics?.model ?? 'whisper'}`
                  : 'LRCLIB'}
              </span>
            {:else if aiLyrics}
              <Sparkles class="text-primary size-3.5 shrink-0" />
              <span class="truncate">AI transcription{aiLyrics.model ? ` · ${aiLyrics.model}` : ''}</span>
            {:else if canTranscribe && canTranslate}
              <span class="truncate">
                {hasLyrics
                  ? 'Re-sync these lyrics to the audio and add a pronunciation guide + translation — one go.'
                  : 'Transcribe the audio and add a pronunciation guide + translation — one go.'}
              </span>
            {:else if canTranscribe}
              <span class="truncate">Transcribe the audio with AI to compare against LRCLIB.</span>
            {:else}
              <span class="truncate">Generate a pronunciation guide + English translation to sing along.</span>
            {/if}
          </div>
          <div class="flex shrink-0 items-center gap-2">
            {#if canCompareLyrics}
              <Button
                variant="subtle"
                size="sm"
                class={cn(comparingLyrics && 'bg-foreground/[0.14] text-foreground dark:bg-white/20')}
                onclick={() => (showCompare = !showCompare)}
              >
                {comparingLyrics ? 'Done' : 'Compare'}
              </Button>
            {/if}
            {#if canEnhance}
              {@render enhanceButton()}
            {/if}
          </div>
        </div>
        {#if enhanceError}
          <p class="text-destructive mx-auto w-full max-w-3xl px-1 text-[11px]">{enhanceError}</p>
        {:else if enhanceNote}
          <p class="text-muted-foreground mx-auto w-full max-w-3xl px-1 text-[11px]">{enhanceNote}</p>
        {/if}
        {#if hasTranslation && !comparingLyrics}
          <!-- Original / Pronunciation / Translation view toggle: Pronunciation and Translation
               stack the generated line under each original line, Apple-Music style. -->
          <div class="mx-auto flex w-full max-w-3xl items-center justify-center px-1">
            <ToggleGroup.Root
              type="single"
              size="sm"
              variant="segmented"
              value={lyricsView}
              onValueChange={(v) => {
                if (v) lyricsView = v as typeof lyricsView;
              }}
            >
              <ToggleGroup.Item value="original" aria-label="Original lyrics">Original</ToggleGroup.Item>
              <ToggleGroup.Item value="pronunciation" aria-label="Pronunciation guide">
                Pronunciation
              </ToggleGroup.Item>
              <ToggleGroup.Item value="translation" aria-label="English translation">
                Translation
              </ToggleGroup.Item>
            </ToggleGroup.Root>
          </div>
        {/if}
      {/if}

      {#if comparingLyrics}
        <!-- Side-by-side: LRCLIB vs AI, each with a "Set as default" chooser for the player. -->
        <div class="mx-auto flex min-h-0 w-full max-w-6xl flex-1 flex-col gap-4 lg:flex-row">
          <div class="flex min-h-0 flex-1 flex-col gap-1.5">
            <div class="flex items-center justify-between gap-2 px-1">
              <div class="text-muted-foreground flex items-center gap-1.5 text-xs font-medium">
                <CheckCircle2 class="size-3.5 text-green-600 dark:text-green-500" />
                LRCLIB
              </div>
              {#if preferredSource === 'lrclib'}
                <span class="text-primary inline-flex items-center gap-1 text-[11px] font-medium">
                  <Check class="size-3" /> Player default
                </span>
              {:else}
                <Button
                  variant="ghost"
                  size="sm"
                  class="h-6 px-2 text-[11px]"
                  disabled={preferSaving}
                  onclick={() => handleSetPreferred('lrclib')}
                >
                  Set as default
                </Button>
              {/if}
            </div>
            <LyricsPanel
              variant="panel"
              songId={song.id}
              syncedLyrics={song.syncedLyrics ?? undefined}
              plainLyrics={song.plainLyrics ?? undefined}
              {lyricsStatus}
              hasSyncedLyrics={song.hasSyncedLyrics ?? false}
              hasPlainLyrics={song.hasPlainLyrics ?? false}
              currentTimeMs={isCurrentlyLoaded ? playerStore.currentTime * 1000 : null}
              onSeek={isCurrentlyLoaded ? (timeMs: number) => playerStore.seek(timeMs / 1000) : undefined}
              lrclibUrl={lrclibWebUrl(trackArtist, trackTitle)}
            />
          </div>
          <div class="flex min-h-0 flex-1 flex-col gap-1.5">
            <div class="flex items-center justify-between gap-2 px-1">
              <div class="text-muted-foreground flex items-center gap-1.5 text-xs font-medium">
                <Sparkles class="text-primary size-3.5" />
                AI · {aiLyrics?.model ?? 'whisper'}
              </div>
              {#if preferredSource === 'transcribed'}
                <span class="text-primary inline-flex items-center gap-1 text-[11px] font-medium">
                  <Check class="size-3" /> Player default
                </span>
              {:else}
                <Button
                  variant="ghost"
                  size="sm"
                  class="h-6 px-2 text-[11px]"
                  disabled={preferSaving}
                  onclick={() => handleSetPreferred('transcribed')}
                >
                  Set as default
                </Button>
              {/if}
            </div>
            {#key aiLyrics?.at}
              <LyricsPanel
                variant="panel"
                songId={song.id}
                syncedLyrics={aiLyrics?.synced}
                plainLyrics={aiLyrics?.plain}
                lyricsStatus="Fetched"
                hasSyncedLyrics={Boolean(aiLyrics?.synced)}
                hasPlainLyrics={Boolean(aiLyrics?.plain)}
                currentTimeMs={isCurrentlyLoaded ? playerStore.currentTime * 1000 : null}
                onSeek={isCurrentlyLoaded ? (timeMs: number) => playerStore.seek(timeMs / 1000) : undefined}
              />
            {/key}
          </div>
        </div>
      {:else}
        <!-- Big synced viewer showing the chosen default (AI when preferred / only option, else LRCLIB). -->
        <div class="mx-auto flex min-h-0 w-full max-w-3xl flex-1 flex-col">
          {#key `${showAiInViewer ? `ai-${aiLyrics?.at}` : 'lrclib'}-${lyricsView}`}
            <LyricsPanel
              variant="theater"
              songId={song.id}
              syncedLyrics={showAiInViewer ? aiLyrics?.synced : (song.syncedLyrics ?? undefined)}
              plainLyrics={showAiInViewer ? aiLyrics?.plain : (song.plainLyrics ?? undefined)}
              lyricsStatus={showAiInViewer ? 'Fetched' : lyricsStatus}
              hasSyncedLyrics={showAiInViewer ? Boolean(aiLyrics?.synced) : (song.hasSyncedLyrics ?? false)}
              hasPlainLyrics={showAiInViewer ? Boolean(aiLyrics?.plain) : (song.hasPlainLyrics ?? false)}
              isInstrumental={song.isInstrumental ?? undefined}
              currentTimeMs={isCurrentlyLoaded ? playerStore.currentTime * 1000 : null}
              onSeek={isCurrentlyLoaded ? (timeMs: number) => playerStore.seek(timeMs / 1000) : undefined}
              lrclibUrl={showAiInViewer ? undefined : lrclibWebUrl(trackArtist, trackTitle)}
              {secondarySynced}
              {secondaryPlain}
            />
          {/key}
        </div>
      {/if}
      </div>
    </Tabs.Content>

    {#if hasWatchableVideo}
      <Tabs.Content value="video" class="flex min-h-0 flex-1 flex-col">
        <VideoWatchTab
          songId={song.id}
          offsetMs={videoInfo?.syncOffsetMs ?? 0}
          onPlayRequest={handlePlayToggle}
        />
      </Tabs.Content>
    {/if}

    <Tabs.Content value="fingerprint" class="flex min-h-0 flex-1 flex-col">
      <ScrollArea class="min-h-0 flex-1">
        <div class="mx-auto w-full max-w-2xl py-2">
          <div class="border-border flex items-end justify-between border-b pb-3">
            <div>
              <div class="text-muted-foreground font-mono text-[10px] tracking-wider">
                AcoustID · Chromaprint v1.5
              </div>
              <div class="mt-1 text-sm font-semibold">{trackTitle}</div>
            </div>
            <div class="text-right">
              <div class="text-muted-foreground text-[9.5px] font-semibold tracking-[0.08em] uppercase">
                Match Confidence
              </div>
              <div class="text-primary mt-0.5 font-mono text-[22px] font-semibold tracking-[-0.02em]">
                {matchValue !== null ? matchValue.toFixed(2) : '—'}
              </div>
            </div>
          </div>

          <div class="bg-surface-sunken mt-4 flex h-16 items-end gap-[2px] rounded p-1.5">
            {#each fingerprintBars(song.fingerprint) as h, i (i)}
              <div
                class="from-primary flex-1 rounded-[1px] bg-gradient-to-t to-cyan-300/70"
                style="height: {h}%; min-height: 2px;"
              ></div>
            {/each}
          </div>

          <div class="bg-surface-sunken text-muted-foreground mt-2.5 rounded px-2.5 py-2 font-mono text-[10px] leading-relaxed break-all">
            {song.fingerprint ? fingerprintHash(song.fingerprint) : '— no fingerprint —'}
          </div>

          <div class="mt-5">
            <div class="text-muted-foreground text-[10px] font-semibold tracking-[0.08em] uppercase">
              {#if attemptRows.length}
                {attemptRows.length} provider {attemptRows.length === 1 ? 'attempt' : 'attempts'}
              {:else}
                Provider attempts
              {/if}
            </div>
            <div class="mt-2 flex flex-col gap-1.5">
              {#if detailLoading}
                <div class="text-muted-foreground flex items-center gap-2 px-1 py-3 text-[12px]">
                  <Loader2 class="size-3.5 animate-spin" /> Loading provider attempts…
                </div>
              {:else if detailError}
                <div class="text-destructive flex items-center gap-2 px-1 py-3 text-[12px]">
                  <AlertCircle class="size-3.5" /> {detailError}
                </div>
              {:else if !attemptRows.length}
                <div class="text-muted-foreground px-1 py-3 text-[12px]">No provider attempts yet.</div>
              {:else}
                {#each attemptRows as row (row.key)}
                  <div
                    class={cn(
                      'border-border flex items-center gap-3 rounded-md border p-2.5',
                      row.chosen && 'border-primary bg-primary/8',
                      !row.matched && 'opacity-60'
                    )}
                  >
                    <span class={cn('w-10 font-mono text-sm font-semibold', row.chosen ? 'text-primary' : 'text-muted-foreground')}>
                      {row.score !== null ? row.score.toFixed(2) : '—'}
                    </span>
                    <div class="min-w-0 flex-1">
                      <div class="truncate text-[12px] font-medium">
                        {#if row.matched}
                          {row.title || '(untitled)'}{row.artist ? ` — ${row.artist}` : ''}{row.album ? ` (${row.album}${row.year ? `, ${row.year}` : ''})` : ''}
                        {:else}
                          {row.error ?? (row.status === 'NoMatch' ? 'No match' : row.status)}
                        {/if}
                      </div>
                      <div class="text-muted-foreground mt-0.5 flex items-center gap-1.5 text-[11px]">
                        <span>{row.source}</span>
                        {#if !row.matched}
                          <span class="bg-muted text-muted-foreground rounded px-1 py-px font-mono text-[9px] tracking-wide uppercase">
                            {row.status}
                          </span>
                        {/if}
                      </div>
                    </div>
                    {#if row.chosen}
                      <span class="bg-primary/15 text-primary rounded px-1.5 py-0.5 font-mono text-[9px] font-semibold tracking-wider">
                        CHOSEN
                      </span>
                    {/if}
                  </div>
                {/each}
              {/if}
            </div>
          </div>
        </div>
      </ScrollArea>
    </Tabs.Content>

    <Tabs.Content value="enrichment" class="flex min-h-0 flex-1 flex-col">
      <ScrollArea class="min-h-0 flex-1">
        <div class="mx-auto w-full max-w-2xl space-y-3 py-2 text-xs">
          {#if timelineHref}
            <Button href={timelineHref} variant="outline" size="sm" class="w-full">
              <History class="mr-1.5 size-3.5" />
              View timeline
            </Button>
          {/if}

          {#if song.matchedBy}
            <div class="bg-muted/50 rounded-lg px-3 py-2">
              <p class="text-muted-foreground mb-0.5 text-[10px] tracking-wider uppercase">Matched via</p>
              <p class="text-[12.5px] font-medium">{song.matchedBy}</p>
            </div>
          {/if}

          <!-- AI quality grade -->
          <div class="border-border rounded-lg border px-3 py-2.5">
            <div class="mb-1.5 flex items-center justify-between gap-2">
              <p class="text-muted-foreground text-[10px] tracking-wider uppercase">AI quality</p>
              {#if quality?.graded}
                <span class={cn('rounded-md border px-1.5 py-0.5 text-[10px] font-semibold', verdictTint(quality.verdict))}>
                  {quality.verdict} · {quality.score}
                </span>
              {/if}
            </div>
            {#if quality?.graded}
              {#if quality.summary}
                <p class="text-muted-foreground mb-1.5 text-[11.5px] leading-snug">{quality.summary}</p>
              {/if}
              {#if quality.issues && quality.issues.length > 0}
                <div class="mb-1.5 flex flex-wrap gap-1">
                  {#each quality.issues as issue, i (i)}
                    <code class="bg-muted/60 rounded px-1 py-px font-mono text-[10px]">{issue.code}</code>
                  {/each}
                </div>
              {/if}
              {#if quality.model || quality.gradedAtUtc}
                <p class="text-muted-foreground/70 text-[10px]">
                  {quality.model ?? ''}{#if quality.model && quality.gradedAtUtc} · {/if}{#if quality.gradedAtUtc}{new Date(quality.gradedAtUtc).toLocaleString()}{/if}
                </p>
              {/if}
            {:else}
              <p class="text-muted-foreground/70 text-[11.5px]">Not graded yet.</p>
            {/if}
            <div class="mt-2 flex gap-1.5">
              <Button variant="outline" size="sm" class="h-7 flex-1 text-[11px]" disabled={gradeBusy} onclick={handleGradeNow}>
                {#if gradeBusy}
                  <Loader2 class="mr-1 size-3 animate-spin" />
                {:else}
                  <Sparkles class="mr-1 size-3" />
                {/if}
                {quality?.graded ? 'Re-grade' : 'Grade now'}
              </Button>
              <Button
                variant="outline"
                size="sm"
                class="h-7 text-[11px]"
                aria-label="Copy dossier"
                onclick={handleCopyDossier}
              >
                {#if copied}<Check class="size-3" />{:else}<Copy class="size-3" />{/if}
              </Button>
            </div>
          </div>

          <div class="space-y-2">
            {#each enrichmentSources as src (src.key)}
              <SourceRow name={src.name} connected={src.connected} url={src.url} label={src.label} />
            {/each}
          </div>

          <Button
            variant="outline"
            class={cn(
              'mt-2 w-full',
              resetState === 'success' && 'border-primary/50 text-primary',
              resetState === 'error' && 'border-destructive/50 text-destructive'
            )}
            size="sm"
            disabled={resetState === 'loading'}
            onclick={handleResetEnrichment}
          >
            {#if resetState === 'loading'}
              <Loader2 class="mr-1.5 size-3.5 animate-spin" />
              Resetting…
            {:else if resetState === 'success'}
              <CheckCircle2 class="mr-1.5 size-3.5" />
              Metadata reset
            {:else if resetState === 'error'}
              <AlertCircle class="mr-1.5 size-3.5" />
              Reset failed
            {:else}
              <RotateCcw class="mr-1.5 size-3.5" />
              Reset metadata
            {/if}
          </Button>
          {#if resetError}
            <p class="text-destructive text-[11px]">{resetError}</p>
          {:else if resetState === 'idle'}
            <p class="text-muted-foreground/70 text-[10.5px]">Clears matches and lyrics; re-enrichment runs automatically.</p>
          {/if}

          <Button
            variant="secondary"
            class={cn(
              'mt-2 w-full',
              enrichState === 'success' && 'text-primary',
              enrichState === 'error' && 'text-destructive'
            )}
            size="sm"
            disabled={enrichState === 'loading'}
            onclick={handleEnrichNow}
          >
            {#if enrichState === 'loading'}
              <Loader2 class="mr-1.5 size-3.5 animate-spin" />
              {inLibrary ? 'Updating…' : 'Adding…'}
            {:else if enrichState === 'success'}
              <CheckCircle2 class="mr-1.5 size-3.5" />
              {enrichOutcome ?? 'Done'}
            {:else if enrichState === 'error'}
              <AlertCircle class="mr-1.5 size-3.5" />
              {inLibrary ? 'Update failed' : 'Add failed'}
            {:else}
              <Sparkles class="mr-1.5 size-3.5" />
              {inLibrary ? 'Update in library' : 'Add to library'}
            {/if}
          </Button>
          {#if enrichError}
            <p class="text-destructive text-[11px]">{enrichError}</p>
          {/if}

          {#if soulseekConfigured}
            <Button
              variant="outline"
              class="mt-2 w-full"
              size="sm"
              disabled={upgradeRequesting || enrichmentDetail?.upgrade?.active === true}
              onclick={handleFindBetterQuality}
            >
              {#if upgradeRequesting || enrichmentDetail?.upgrade?.active}
                <Loader2 class="mr-1.5 size-3.5 animate-spin" />
              {:else}
                <Search class="mr-1.5 size-3.5" />
              {/if}
              {upgradeActiveLabel ?? 'Find better quality'}
            </Button>
            {#if upgradeError}
              <p class="text-destructive text-[11px]">{upgradeError}</p>
            {:else if upgradeTerminalNote}
              <p class="text-muted-foreground/70 text-[10.5px]">{upgradeTerminalNote}</p>
            {:else}
              <p class="text-muted-foreground/70 text-[10.5px]">
                Searches Soulseek for a higher-quality copy and swaps it in place.
              </p>
            {/if}
          {/if}
        </div>
      </ScrollArea>
    </Tabs.Content>
      </div>

      <!-- Mobile transport pinned at the bottom (the Lyrics tab's hero has its own) -->
      <div class={cn('shrink-0 lg:hidden', activeTab === 'lyrics' && 'hidden')}>
        {@render transport()}
      </div>
    </div>
  </Tabs.Root>

  <!-- Mobile fullscreen lyrics overlay (shared with the public share page): only the
       lyrics + scrubber + play/pause, over the track's ambient artwork. -->
  {#if lyricsExpanded}
    <LyricsFullscreen
      title={trackTitle}
      artist={trackArtist}
      coverTitle={album.title}
      {coverUrl}
      {ambientUrl}
      isActive={isCurrentlyLoaded}
      isPlaying={isCurrentlyPlaying}
      fallbackDuration={song.durationSeconds ?? 0}
      onPlayToggle={handlePlayToggle}
      onClose={() => (lyricsExpanded = false)}
    >
      {#key `${showAiInViewer ? `ai-${aiLyrics?.at}` : 'lrclib'}-${lyricsView}`}
        <LyricsPanel
          variant="theater"
          songId={song.id}
          syncedLyrics={showAiInViewer ? aiLyrics?.synced : (song.syncedLyrics ?? undefined)}
          plainLyrics={showAiInViewer ? aiLyrics?.plain : (song.plainLyrics ?? undefined)}
          lyricsStatus={showAiInViewer ? 'Fetched' : lyricsStatus}
          hasSyncedLyrics={showAiInViewer ? Boolean(aiLyrics?.synced) : (song.hasSyncedLyrics ?? false)}
          hasPlainLyrics={showAiInViewer ? Boolean(aiLyrics?.plain) : (song.hasPlainLyrics ?? false)}
          isInstrumental={song.isInstrumental ?? undefined}
          currentTimeMs={isCurrentlyLoaded ? playerStore.currentTime * 1000 : null}
          onSeek={isCurrentlyLoaded ? (timeMs: number) => playerStore.seek(timeMs / 1000) : undefined}
          {secondarySynced}
          {secondaryPlain}
        />
      {/key}
    </LyricsFullscreen>
  {/if}
</div>

<style>
  .mh-track-panel-enter {
    animation: mh-tp-rise 0.25s ease-out both;
  }
  @keyframes mh-tp-rise {
    from {
      transform: translateY(12px);
      opacity: 0;
    }
    to {
      transform: translateY(0);
      opacity: 1;
    }
  }
  @media (prefers-reduced-motion: reduce) {
    .mh-track-panel-enter {
      animation: none;
    }
  }
</style>
