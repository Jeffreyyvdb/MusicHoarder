import { describe, expect, it, vi } from 'vitest';
import { createLyricsDoc, type LyricsDocApi } from './lyrics-doc.svelte';
import type { LyricsStatus } from '$lib/types';

/**
 * The lyrics document moved out of LyricsPanel so Now Playing's ⋯ menu and lyrics status row can
 * drive "Check LRCLIB again" / "Check the timing" with no panel mounted. What is pinned here is
 * what used to be implicit in the panel: the reset is keyed on the song (not on the text props),
 * a document that was handed its text never fetches, and a result that lands after the song
 * changed is dropped instead of painted onto the next one.
 */

type Input = {
  songId: number | null;
  synced?: string;
  plain?: string;
  hasSynced?: boolean;
  hasPlain?: boolean;
  status?: LyricsStatus;
};

const lrc = '[00:01.00] one\n[00:02.00] two';

function stubApi(over: Partial<LyricsDocApi> = {}): LyricsDocApi {
  return {
    fetchTrackLyrics: vi.fn(async (id: number) => ({
      id,
      lyricsStatus: 'Fetched',
      synced: lrc,
      plain: 'one\ntwo',
      lyricsSyncStatus: 'Suspect' as const,
      lyricsSyncIssue: 'Lines run 4s early.'
    })),
    recheckSongLyrics: vi.fn(async (id: number) => ({
      id,
      updated: false,
      lyricsStatus: 'NotFound',
      hasSyncedLyrics: false,
      hasPlainLyrics: false
    })),
    verifyLyricsTiming: vi.fn(async (id: number) => ({
      id,
      lyricsSyncStatus: 'Ok' as const
    })),
    ...over
  } as LyricsDocApi;
}

function setup(initial: Input, opts: { fetchable?: boolean; api?: Partial<LyricsDocApi> } = {}) {
  let input = $state<Input>(initial);
  const api = stubApi(opts.api);
  const doc = createLyricsDoc({
    songId: () => input.songId,
    synced: () => input.synced,
    plain: () => input.plain,
    hasSynced: () => input.hasSynced,
    hasPlain: () => input.hasPlain,
    status: () => input.status,
    fetchable: opts.fetchable,
    api
  });
  // The owner's effects, played by hand.
  const drive = () => {
    doc.syncToSong();
    doc.ensureLoaded();
  };
  return {
    doc,
    api,
    drive,
    set(next: Input) {
      input = next;
    }
  };
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0));

describe('createLyricsDoc', () => {
  it('fetches the text when the flags say lyrics exist but none were handed in', async () => {
    const { doc, api, drive } = setup({ songId: 7, hasSynced: true, status: 'Fetched' });
    drive();
    expect(doc.loadState).toBe('loading');
    await flush();
    expect(api.fetchTrackLyrics).toHaveBeenCalledWith(7);
    expect(doc.synced).toBe(lrc);
    expect(doc.syncStatus).toBe('Suspect');
    expect(doc.syncIssue).toBe('Lines run 4s early.');
    expect(doc.canToggleSynced).toBe(true);
  });

  it('uses handed-in text without fetching', () => {
    const { doc, api, drive } = setup({ songId: 7, synced: lrc, hasSynced: true });
    drive();
    expect(api.fetchTrackLyrics).not.toHaveBeenCalled();
    expect(doc.synced).toBe(lrc);
  });

  it('never fetches a document that is not fetchable (an AI transcription)', () => {
    const { api, drive } = setup({ songId: 7, hasSynced: true }, { fetchable: false });
    drive();
    expect(api.fetchTrackLyrics).not.toHaveBeenCalled();
  });

  it('resets on a new song but keeps an in-place result for the same one', async () => {
    const { doc, drive, set } = setup({ songId: 1, plain: 'old', hasPlain: true });
    drive();
    await doc.verifyTiming();
    expect(doc.verifyMessage).toBe('The timing matches the audio.');
    // Same song, new props (an SSE refresh): nothing resets.
    set({ songId: 1, plain: 'refreshed', hasPlain: true });
    drive();
    expect(doc.plain).toBe('old');
    expect(doc.verifyMessage).not.toBeNull();
    // A different song starts clean.
    set({ songId: 2, plain: 'second', hasPlain: true });
    drive();
    expect(doc.plain).toBe('second');
    expect(doc.verifyMessage).toBeNull();
  });

  it('drops a load that lands after the song changed', async () => {
    let resolveFetch: (v: unknown) => void = () => {};
    const { doc, drive, set } = setup(
      { songId: 1, hasSynced: true },
      {
        api: {
          fetchTrackLyrics: vi.fn(
            () => new Promise((resolve) => (resolveFetch = resolve))
          ) as LyricsDocApi['fetchTrackLyrics']
        }
      }
    );
    drive();
    set({ songId: 2, plain: 'second', hasPlain: true });
    drive();
    resolveFetch({ id: 1, synced: lrc });
    await flush();
    expect(doc.synced).toBeUndefined();
    expect(doc.plain).toBe('second');
  });

  it('reports an unchanged LRCLIB re-check, and swaps the text in when there is news', async () => {
    const quiet = setup({ songId: 3, status: 'NotFound' });
    quiet.drive();
    await quiet.doc.recheck();
    expect(quiet.doc.recheckState).toBe('unchanged');

    const news = setup(
      { songId: 3, status: 'NotFound' },
      {
        api: {
          recheckSongLyrics: vi.fn(async (id: number) => ({
            id,
            updated: true,
            lyricsStatus: 'Fetched',
            hasSyncedLyrics: true,
            hasPlainLyrics: true
          }))
        }
      }
    );
    news.drive();
    await news.doc.recheck();
    expect(news.doc.recheckState).toBe('idle');
    expect(news.doc.status).toBe('Fetched');
    expect(news.doc.synced).toBe(lrc);
  });

  it('lets a caller that knows its own provenance outrank the song-level one', async () => {
    const api = stubApi({
      fetchTrackLyrics: vi.fn(async (id: number) => ({
        id,
        lyricsStatus: 'Fetched',
        synced: lrc,
        lyricsProvenance: 'AiGenerated' as const
      }))
    });
    const make = (trust: boolean) =>
      createLyricsDoc({
        songId: () => 9,
        synced: () => undefined,
        plain: () => undefined,
        hasSynced: () => true,
        hasPlain: () => false,
        status: () => 'Fetched',
        provenance: () => 'Human',
        trustProvidedProvenance: trust,
        api
      });
    const panel = make(false);
    const nowPlaying = make(true);
    for (const doc of [panel, nowPlaying]) {
      doc.syncToSong();
      doc.ensureLoaded();
    }
    await flush();
    // The panel's own fetch wins by default (the share page relies on it)…
    expect(panel.provenance).toBe('AiGenerated');
    // …but Now Playing's LRCLIB document is LRCLIB's, whatever the default source is.
    expect(nowPlaying.provenance).toBe('Human');
  });

  it('says what a timing check did', async () => {
    const repaired = setup(
      { songId: 4, synced: lrc, hasSynced: true },
      {
        api: {
          verifyLyricsTiming: vi.fn(async (id: number) => ({
            id,
            lyricsSyncStatus: 'Ok' as const,
            repaired: true,
            usedAi: false,
            lyricsSyncOffsetMs: -2000
          }))
        }
      }
    );
    repaired.drive();
    await repaired.doc.verifyTiming();
    expect(repaired.doc.verifyMessage).toBe('Timing repaired — every line moved 2s earlier.');

    const deferred = setup(
      { songId: 4, synced: lrc, hasSynced: true },
      {
        api: {
          verifyLyricsTiming: vi.fn(async (id: number) => ({
            id,
            lyricsSyncStatus: 'Suspect' as const,
            repaired: false,
            usedAi: false,
            deferred: true
          }))
        }
      }
    );
    deferred.drive();
    await deferred.doc.verifyTiming();
    expect(deferred.doc.verifyMessage).toMatch(/rate-limited/);
    expect(deferred.doc.syncStatus).toBe('Suspect');
  });
});
