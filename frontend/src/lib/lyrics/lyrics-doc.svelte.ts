import {
  fetchTrackLyrics,
  recheckSongLyrics,
  verifyLyricsTiming,
  type RecheckLyricsResponse,
  type TrackLyricsResponse,
  type VerifyLyricsTimingResponse
} from '$lib/api-client';
import type { LyricsProvenance, LyricsStatus, LyricsSyncStatus } from '$lib/types';

/**
 * The server calls this module makes, injectable so the rules are unit-testable without a
 * network. Production callers omit it and get the real api-client functions.
 */
export type LyricsDocApi = {
  fetchTrackLyrics: (trackId: number) => Promise<TrackLyricsResponse>;
  recheckSongLyrics: (songId: number) => Promise<RecheckLyricsResponse>;
  verifyLyricsTiming: (songId: number) => Promise<VerifyLyricsTimingResponse>;
};

const realApi: LyricsDocApi = { fetchTrackLyrics, recheckSongLyrics, verifyLyricsTiming };

export type LyricsDoc = ReturnType<typeof createLyricsDoc>;

/**
 * One lyrics document on screen: its text (as passed in, or fetched when only the flags are
 * known), the Synced/Plain choice, and the two maintenance actions on the stored LRCLIB copy —
 * "Check LRCLIB again" and "Check the timing" — with their results.
 *
 * This used to be LyricsPanel's own state. It moved out so Now Playing can offer those actions
 * from its ⋯ menu and show the timing verdict in the lyrics status row while no panel is mounted
 * (the artwork view), and so a remounted panel (a view change re-keys it) keeps what was loaded.
 * A panel that is handed no document still makes its own from its props — the share page and the
 * compare columns work exactly as before.
 *
 * Component-free like `createAiLyrics`: the owner's `$effect`s call `syncToSong()` and
 * `ensureLoaded()`; everything else is getters and actions.
 */
export function createLyricsDoc(opts: {
  songId: () => number | null;
  /**
   * Resets the document when it changes. Defaults to the song id; a document whose text can be
   * swapped wholesale for the same song (a fresh AI transcription) adds that to the key.
   */
  key?: () => string;
  synced: () => string | undefined;
  plain: () => string | undefined;
  hasSynced: () => boolean | undefined;
  hasPlain: () => boolean | undefined;
  status: () => LyricsStatus | undefined;
  provenance?: () => LyricsProvenance | null | undefined;
  /**
   * The caller computes provenance for exactly this document (Now Playing's LRCLIB and AI
   * documents), so it wins over the server's song-level value in the lyrics response — that one
   * describes whatever the player's default source is, which is the other document half the time.
   * A timing check's own verdict still wins over both.
   */
  trustProvidedProvenance?: boolean;
  /**
   * False for a document that is only ever what it was handed (an AI transcription): it must not
   * fetch, because the lyrics endpoint returns the LRCLIB text, not this document's.
   */
  fetchable?: boolean;
  api?: LyricsDocApi;
}) {
  const api = opts.api ?? realApi;
  const fetchable = opts.fetchable ?? true;
  const keyOf = () => (opts.key ? opts.key() : String(opts.songId()));

  let showSynced = $state(true);
  let loadedSynced = $state<string | null | undefined>(undefined);
  let loadedPlain = $state<string | null | undefined>(undefined);
  let loadState = $state<'idle' | 'loading' | 'error'>('idle');
  let loadedProvenance = $state<LyricsProvenance | null>(null);
  let verifiedProvenance = $state<LyricsProvenance | null>(null);
  let loadedSyncStatus = $state<LyricsSyncStatus | null>(null);
  let loadedSyncIssue = $state<string | null>(null);
  // LRCLIB keeps gaining lyrics, so a track that had none (or only unsynced ones) when it was
  // enriched can have an LRC today. The background sweep picks that up on a multi-day backoff;
  // this is for when the user can already see the lyrics on lrclib.net. The endpoint only ever
  // adds lyrics, so there is nothing to undo.
  let recheckState = $state<'idle' | 'checking' | 'unchanged'>('idle');
  let statusOverride = $state<LyricsStatus | null>(null);
  // LRCLIB lyrics can belong to a different recording of the same song, in which case the words
  // are right and the timestamps are not. The endpoint runs the free checks first and only pays
  // for an AI listen when they cannot settle it.
  let verifyState = $state<'idle' | 'checking' | 'done'>('idle');
  let verifyMessage = $state<string | null>(null);
  // Plain (non-reactive) guard so the owner's effect can't loop on its own reset.
  let syncedForKey: string | null = null;

  const hasSynced = $derived(Boolean(loadedSynced) || Boolean(opts.hasSynced()));
  const hasPlain = $derived(Boolean(loadedPlain) || Boolean(opts.hasPlain()));
  const hasAny = $derived(hasSynced || hasPlain);
  const effectiveStatus = $derived(statusOverride ?? opts.status());
  // The document's own fetch is the richer source (it carries the timing verdict too), so it wins
  // over the prop once it lands. Before that, the prop is what the caller already had.
  const effectiveProvenance = $derived(
    verifiedProvenance ??
      (opts.trustProvidedProvenance
        ? (opts.provenance?.() ?? loadedProvenance)
        : (loadedProvenance ?? opts.provenance?.())) ??
      null
  );

  function applyLyricsMeta(data: {
    lyricsProvenance?: LyricsProvenance | null;
    lyricsSyncStatus?: LyricsSyncStatus | null;
    lyricsSyncIssue?: string | null;
  }) {
    loadedProvenance = data.lyricsProvenance ?? null;
    loadedSyncStatus = data.lyricsSyncStatus ?? null;
    loadedSyncIssue = data.lyricsSyncIssue ?? null;
  }

  return {
    get showSynced() {
      return showSynced;
    },
    get synced() {
      return loadedSynced ?? undefined;
    },
    get plain() {
      return loadedPlain ?? undefined;
    },
    get loadState() {
      return loadState;
    },
    get hasSynced() {
      return hasSynced;
    },
    get hasPlain() {
      return hasPlain;
    },
    get hasAny() {
      return hasAny;
    },
    get status() {
      return effectiveStatus;
    },
    get provenance() {
      return effectiveProvenance;
    },
    get syncStatus() {
      return loadedSyncStatus;
    },
    get syncIssue() {
      return loadedSyncIssue;
    },
    get recheckState() {
      return recheckState;
    },
    get verifyState() {
      return verifyState;
    },
    get verifyMessage() {
      return verifyMessage;
    },
    /** Both a synced and a plain version are loaded, so the Synced / Plain choice means something. */
    get canToggleSynced() {
      return Boolean(loadedSynced) && Boolean(loadedPlain);
    },

    setShowSynced(value: boolean) {
      showSynced = value;
    },

    /**
     * Re-sync from the inputs when the key (normally the song) changes. The panel instance is reused
     * across tracks, so a one-time guard would freeze the first song's lyrics and the auto-fetch
     * would never re-fire. Keyed on the song — not on the lyric inputs — so an in-place fetch
     * result for the same song doesn't get clobbered by the props it replaced.
     */
    syncToSong() {
      const key = keyOf();
      if (syncedForKey === key) return;
      syncedForKey = key;
      loadedSynced = opts.synced();
      loadedPlain = opts.plain();
      loadedProvenance = null;
      verifiedProvenance = null;
      loadedSyncStatus = null;
      loadedSyncIssue = null;
      loadState = 'idle';
      recheckState = 'idle';
      verifyState = 'idle';
      verifyMessage = null;
      statusOverride = null;
    },

    /** Fetch the text when the flags say lyrics exist but none were handed in. */
    ensureLoaded() {
      const songId = opts.songId();
      if (!fetchable || songId === null) return;
      if (!hasAny || loadedSynced || loadedPlain || loadState !== 'idle') return;
      loadState = 'loading';
      const key = keyOf();
      api
        .fetchTrackLyrics(songId)
        .then((data) => {
          if (syncedForKey !== key) return; // moved on while in flight
          loadedSynced = data.synced ?? undefined;
          loadedPlain = data.plain ?? undefined;
          applyLyricsMeta(data);
          loadState = 'idle';
        })
        .catch(() => {
          if (syncedForKey === key) loadState = 'error';
        });
    },

    /** Back to idle after a failed load, so `ensureLoaded` tries again. */
    retry() {
      if (loadState === 'error') loadState = 'idle';
    },

    async recheck() {
      const songId = opts.songId();
      if (songId === null || recheckState === 'checking') return;
      const key = keyOf();
      recheckState = 'checking';
      try {
        const result = await api.recheckSongLyrics(songId);
        if (syncedForKey !== key) return;
        if (!result.updated) {
          recheckState = 'unchanged';
          return;
        }
        const lyrics = await api.fetchTrackLyrics(songId);
        if (syncedForKey !== key) return;
        loadedSynced = lyrics.synced ?? undefined;
        loadedPlain = lyrics.plain ?? undefined;
        applyLyricsMeta(lyrics);
        statusOverride = result.lyricsStatus as LyricsStatus;
        recheckState = 'idle';
      } catch {
        if (syncedForKey === key) recheckState = 'unchanged';
      }
    },

    async verifyTiming() {
      const songId = opts.songId();
      if (songId === null || verifyState === 'checking') return;
      const key = keyOf();
      verifyState = 'checking';
      verifyMessage = null;
      try {
        const result = await api.verifyLyricsTiming(songId);
        if (syncedForKey !== key) return;
        loadedSyncStatus = result.lyricsSyncStatus;
        loadedSyncIssue = result.lyricsSyncIssue ?? null;
        loadedProvenance = result.lyricsProvenance ?? loadedProvenance;
        verifiedProvenance = result.lyricsProvenance ?? null;

        if (result.repaired) {
          // Every timestamp moved, so the text on screen is stale — pull the repaired LRC.
          const lyrics = await api.fetchTrackLyrics(songId);
          if (syncedForKey !== key) return;
          loadedSynced = lyrics.synced ?? undefined;
          loadedPlain = lyrics.plain ?? undefined;
          const shift = Math.round((result.lyricsSyncOffsetMs ?? 0) / 1000);
          verifyMessage = `Timing repaired — every line moved ${Math.abs(shift)}s ${shift >= 0 ? 'later' : 'earlier'}.`;
        } else if (result.deferred) {
          // Nothing was learned and nothing was spent: the transcription quota is busy. Say that
          // rather than letting the free checks' verdict read as the final answer.
          verifyMessage = 'The AI check is rate-limited right now — try again in a minute.';
        } else if (result.lyricsSyncStatus === 'Ok') {
          verifyMessage = 'The timing matches the audio.';
        } else if (result.lyricsSyncStatus === 'Unverifiable') {
          verifyMessage = 'Could not tell — try a full AI re-sync instead.';
        }
        verifyState = 'done';
      } catch {
        if (syncedForKey !== key) return;
        verifyMessage = 'The timing check could not be completed.';
        verifyState = 'done';
      }
    }
  };
}
