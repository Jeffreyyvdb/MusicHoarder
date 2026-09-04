import {
  fetchTrackLyrics,
  setPreferredLyricsSource,
  transcribeSongLyrics,
  translateSongLyrics,
  type ApiSong,
  type TrackLyricsResponse,
  type TranscribeLyricsResponse,
  type TranslateLyricsResponse
} from '$lib/api-client';
import { computeLyricsProvenance } from '$lib/lyrics/provenance';
import type { LyricsProvenance, LyricsStatus } from '$lib/types';

export type LyricsSource = 'lrclib' | 'transcribed';
export type LyricsViewMode = 'original' | 'pronunciation' | 'translation';
export type EnhanceState = 'idle' | 'transcribing' | 'translating' | 'success' | 'error';

export type AiTranscription = {
  synced?: string;
  plain?: string;
  model?: string;
  at?: string;
  /**
   * True when the transcription re-timed the song's OWN official lyrics rather than inventing its
   * words — the bit that separates an "AI Enhanced" label from an "AI Generated" one.
   */
  alignedToReference?: boolean;
};

/** Pronunciation (romanization) + English translation — display-only documents. */
export type AiTranslation = {
  romanizedSynced?: string;
  romanizedPlain?: string;
  translatedSynced?: string;
  translatedPlain?: string;
  language?: string;
  model?: string;
  at?: string;
};

/** The lyrics document the big synced viewer should show — AI or LRCLIB, resolved in one place. */
export type ViewerLyricsDoc = {
  synced: string | undefined;
  plain: string | undefined;
  status: LyricsStatus;
  hasSynced: boolean;
  hasPlain: boolean;
};

/**
 * The server calls this module makes, injectable so the state machine is unit-testable
 * without a network. Production callers omit it and get the real api-client functions.
 */
export type AiLyricsApi = {
  fetchTrackLyrics: (trackId: number) => Promise<TrackLyricsResponse>;
  transcribeSongLyrics: (songId: number) => Promise<TranscribeLyricsResponse>;
  translateSongLyrics: (songId: number) => Promise<TranslateLyricsResponse>;
  setPreferredLyricsSource: (
    songId: number,
    source: LyricsSource
  ) => Promise<{ id: number; preferredLyricsSource: string; lyricsTranslationStale?: boolean | null }>;
};

const realApi: AiLyricsApi = {
  fetchTrackLyrics,
  transcribeSongLyrics,
  translateSongLyrics,
  setPreferredLyricsSource
};

export type AiLyrics = ReturnType<typeof createAiLyrics>;

/**
 * The AI-lyrics subsystem of the track panel: the "Enhance with AI" two-stage run
 * (transcribe/re-sync, then pronunciation + translation), which lyrics document the
 * viewer shows, the LRCLIB-vs-AI player default, and the staleness rule that keeps a
 * generated translation aligned with whatever lyrics are on screen.
 *
 * This used to live inline in TrackPanel.svelte, where none of it was reachable from a
 * test. Like `createTrackListView`, it is deliberately component-free: the caller owns
 * the `$effect` that drives `syncToSong()` on song changes, and rendering reads the
 * getters — so the whole decision surface runs under vitest with a stubbed API.
 */
export function createAiLyrics(opts: {
  song: () => ApiSong;
  /** Enhance/prefer are server writes on the owner's rows; friends only view existing documents. */
  isOwner: () => boolean;
  /** True when a transcription provider is configured server-side. */
  lyricsFeatureEnabled: () => boolean;
  /** True when a translation provider is configured server-side. */
  translationFeatureEnabled: () => boolean;
  api?: AiLyricsApi;
}) {
  const api = opts.api ?? realApi;

  const song = $derived(opts.song());
  const isOwner = $derived(opts.isOwner());
  const lyricsFeatureEnabled = $derived(opts.lyricsFeatureEnabled());
  const translationFeatureEnabled = $derived(opts.translationFeatureEnabled());

  // --- AI lyrics: one action, two stages ---
  //
  // "Enhance with AI" runs the transcription (which re-times the song's own official lyrics when it
  // has them, so it doubles as a re-sync) and then the pronunciation + translation, in one click.
  // The two server calls stay separate so the first result lands on screen while the second runs.
  let transcription = $state<AiTranscription | null>(null);
  /** Non-null when the stored LRC's timestamps were repaired by a measured offset. */
  let syncOffsetMs = $state<number | null>(null);
  // Which version the big synced viewer shows when both exist, the compare-view toggle, and save state.
  let preferredSource = $state<LyricsSource>('lrclib');
  let showCompare = $state(false);
  let preferSaving = $state(false);
  // Plain (non-reactive) guard so re-syncing on song change can't loop the caller's effect.
  let loadedForSongId: number | null = null;

  let translation = $state<AiTranslation | null>(null);
  let lyricsView = $state<LyricsViewMode>('original');
  // True when the stored translation was generated from lyrics that have since changed. Actions
  // that change the display lyrics (a preferred-source flip) regenerate it.
  let translationStale = $state(false);

  // Shared progress for the combined run: which stage is in flight, plus the outcome banner.
  let enhanceState = $state<EnhanceState>('idle');
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

  const lyricsStatus = $derived((song.lyricsStatus ?? 'NotFetched') as LyricsStatus);
  const hasLyrics = $derived(
    Boolean(song.hasSyncedLyrics) || Boolean(song.hasPlainLyrics) || lyricsStatus === 'Fetched'
  );

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
  // which stage 1 may itself have just produced — hence the `transcription` term, read fresh between
  // the two stages (a demo row with no file on disk fails stage 1 and falls through to stage 2).
  // Owner-gated: these are server writes on the owner's rows; friends still SEE existing
  // pronunciation/translation documents (hasTranslation), they just can't generate them.
  const canTranscribe = $derived(isOwner && lyricsFeatureEnabled && song.isInstrumental !== true);
  const canTranslate = $derived(
    isOwner &&
      translationFeatureEnabled &&
      song.isInstrumental !== true &&
      (hasLyrics || transcription != null)
  );
  const canEnhance = $derived(canTranscribe || canTranslate);
  const hasAiOutput = $derived(transcription != null || translation != null);

  // The button says what this particular track will get, since either half can be unconfigured.
  const enhanceLabel = $derived.by(() => {
    if (!canTranslate) return hasAiOutput ? 'Re-sync with AI' : 'Transcribe with AI';
    if (!canTranscribe) return hasAiOutput ? 'Regenerate' : 'Pronunciation & translation';
    if (hasAiOutput) return 'Redo with AI';
    return hasLyrics ? 'Improve with AI' : 'Generate with AI';
  });

  // Comparison only makes sense once an AI transcription exists alongside LRCLIB lyrics.
  const canCompare = $derived(lyricsFeatureEnabled && transcription != null && hasLyrics);
  const comparing = $derived(showCompare && canCompare);
  // The big synced viewer shows the AI version when it's the chosen default (or it's all we have).
  const showAiInViewer = $derived(
    lyricsFeatureEnabled && transcription != null && (!hasLyrics || preferredSource === 'transcribed')
  );

  // Secondary document stacked under the original lines, picked by the current lyrics view. The
  // translation was generated from the *display* lyrics, so it isn't offered inside the compare
  // split, and a STALE document (generated from since-changed lyrics) is never stacked — its lines
  // would misalign with what's on screen.
  const secondarySynced = $derived.by(() => {
    if (comparing || !hasTranslation || translationStale) return undefined;
    if (lyricsView === 'pronunciation') return translation?.romanizedSynced;
    if (lyricsView === 'translation') return translation?.translatedSynced;
    return undefined;
  });
  const secondaryPlain = $derived.by(() => {
    if (comparing || !hasTranslation || translationStale) return undefined;
    if (lyricsView === 'pronunciation') return translation?.romanizedPlain;
    if (lyricsView === 'translation') return translation?.translatedPlain;
    return undefined;
  });

  // The AI disclosure for whatever the viewer is showing right now. Recomputed locally because the
  // compare toggle switches sources without a refetch, so the server's value would go stale the
  // moment the user flips it.
  const viewerProvenance = $derived(
    computeLyricsProvenance({
      showingTranscription: showAiInViewer,
      alignedToReference: transcription?.alignedToReference === true,
      hasSyncedLyrics: song.hasSyncedLyrics === true,
      syncOffsetMs
    })
  );

  // The compare view shows both versions at once, so each column states its own provenance rather
  // than the viewer default's.
  const lrclibProvenance = $derived<LyricsProvenance>(
    computeLyricsProvenance({
      showingTranscription: false,
      alignedToReference: false,
      hasSyncedLyrics: song.hasSyncedLyrics === true,
      syncOffsetMs
    })
  );
  const aiProvenance = $derived(
    computeLyricsProvenance({
      showingTranscription: true,
      alignedToReference: transcription?.alignedToReference === true,
      hasSyncedLyrics: song.hasSyncedLyrics === true,
      syncOffsetMs
    })
  );

  // The mobile lyrics card / fullscreen overlay present the same source as the big viewer.
  const expandable = $derived(
    song.isInstrumental !== true &&
      (showAiInViewer ? Boolean(transcription?.synced || transcription?.plain) : hasLyrics)
  );

  // Every LyricsPanel showing "the viewer's lyrics" (mobile card, desktop theater, fullscreen
  // overlay) renders this one resolved document instead of re-deciding AI-vs-LRCLIB inline.
  const viewerDoc = $derived.by<ViewerLyricsDoc>(() => {
    if (showAiInViewer) {
      return {
        synced: transcription?.synced,
        plain: transcription?.plain,
        status: 'Fetched',
        hasSynced: Boolean(transcription?.synced),
        hasPlain: Boolean(transcription?.plain)
      };
    }
    return {
      synced: song.syncedLyrics ?? undefined,
      plain: song.plainLyrics ?? undefined,
      status: lyricsStatus,
      hasSynced: song.hasSyncedLyrics ?? false,
      hasPlain: song.hasPlainLyrics ?? false
    };
  });
  // {#key} value for those panels: remounts when the shown source (or a fresh transcription's
  // timestamp) or the stacked view changes.
  const viewerKey = $derived(
    `${showAiInViewer ? `ai-${transcription?.at}` : 'lrclib'}-${lyricsView}`
  );

  /** Stage 1 — transcribe/re-sync the audio and reflect the server's (possibly promoted) default. */
  async function runTranscribe() {
    const r = await api.transcribeSongLyrics(song.id);
    transcription = {
      synced: r.synced ?? undefined,
      plain: r.plain ?? undefined,
      model: r.model ?? undefined,
      at: r.transcribedAtUtc ?? undefined,
      alignedToReference: r.resynced === true
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
    const r = await api.translateSongLyrics(song.id);
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

  return {
    get transcription() {
      return transcription;
    },
    get translation() {
      return translation;
    },
    get translationStale() {
      return translationStale;
    },
    get syncOffsetMs() {
      return syncOffsetMs;
    },
    get preferredSource() {
      return preferredSource;
    },
    get lyricsView() {
      return lyricsView;
    },
    get showCompare() {
      return showCompare;
    },
    get preferSaving() {
      return preferSaving;
    },
    get enhanceState() {
      return enhanceState;
    },
    get enhanceError() {
      return enhanceError;
    },
    get enhanceNote() {
      return enhanceNote;
    },
    get enhanceBusy() {
      return enhanceBusy;
    },
    get lyricsStatus() {
      return lyricsStatus;
    },
    get hasLyrics() {
      return hasLyrics;
    },
    get translationIsEnglish() {
      return translationIsEnglish;
    },
    get hasTranslation() {
      return hasTranslation;
    },
    get canTranscribe() {
      return canTranscribe;
    },
    get canTranslate() {
      return canTranslate;
    },
    get canEnhance() {
      return canEnhance;
    },
    get hasAiOutput() {
      return hasAiOutput;
    },
    get enhanceLabel() {
      return enhanceLabel;
    },
    get canCompare() {
      return canCompare;
    },
    get comparing() {
      return comparing;
    },
    get showAiInViewer() {
      return showAiInViewer;
    },
    get secondarySynced() {
      return secondarySynced;
    },
    get secondaryPlain() {
      return secondaryPlain;
    },
    get viewerProvenance() {
      return viewerProvenance;
    },
    get lrclibProvenance() {
      return lrclibProvenance;
    },
    get aiProvenance() {
      return aiProvenance;
    },
    get expandable() {
      return expandable;
    },
    get viewerDoc() {
      return viewerDoc;
    },
    get viewerKey() {
      return viewerKey;
    },

    setLyricsView(view: LyricsViewMode) {
      lyricsView = view;
    },

    toggleCompare() {
      showCompare = !showCompare;
    },

    /**
     * Load any existing AI transcription + pronunciation/translation when the song changes, and
     * reset transient state so a prior song's data never bleeds into the next (the panel instance
     * is reused across songs). Keyed on the plain `loadedForSongId` (not $state) so the caller's
     * `$effect` can't re-trigger itself; a repeat call for the same song is a no-op.
     */
    syncToSong() {
      const current = song;
      const id = current.id;
      if (loadedForSongId === id) return;
      loadedForSongId = id;
      transcription = null;
      syncOffsetMs = null;
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
      preferredSource = current.preferredLyricsSource === 'Transcribed' ? 'transcribed' : 'lrclib';
      // The songs list carries no translation flag, so any song with lyrics may have one — one small
      // fetch covers both the transcription and the translation.
      if (!current.hasTranscribedLyrics && !current.hasSyncedLyrics && !current.hasPlainLyrics) return;
      api
        .fetchTrackLyrics(id)
        .then((d) => {
          if (loadedForSongId !== id) return; // navigated away while in flight
          if (d.transcribedSynced || d.transcribedPlain) {
            transcription = {
              synced: d.transcribedSynced ?? undefined,
              plain: d.transcribedPlain ?? undefined,
              model: d.transcriptionModel ?? undefined,
              at: d.transcribedAtUtc ?? undefined,
              alignedToReference: d.transcriptionAlignedToReference === true
            };
          }
          syncOffsetMs = d.lyricsSyncOffsetMs ?? null;
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
    },

    /**
     * The one AI lyrics action: re-sync the timings, then generate the pronunciation guide and
     * English translation — no second click. Either half can be unavailable (provider not
     * configured, nothing to translate), and a failed transcription still lets the translation run
     * off the existing lyrics, so the outcome is reported per stage rather than as a single
     * pass/fail.
     */
    async enhance() {
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
      if (song.id !== loadedForSongId) return; // navigated away mid-run

      if (canTranslate) {
        enhanceState = 'translating';
        try {
          await runTranslate();
        } catch (err) {
          if (song.id !== loadedForSongId) return;
          enhanceState = 'error';
          enhanceError = err instanceof Error ? err.message : 'Translation failed';
          settleEnhance(6000);
          return;
        }
      }
      if (song.id !== loadedForSongId) return;

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
    },

    /** Choose the player default; optimistic, reverting on failure, regenerating a stale translation. */
    async setPreferred(source: LyricsSource) {
      if (preferredSource === source || preferSaving) return;
      const previous = preferredSource;
      preferredSource = source; // optimistic
      preferSaving = true;
      try {
        const r = await api.setPreferredLyricsSource(song.id, source);
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
  };
}
