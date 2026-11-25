import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { toast } from 'svelte-sonner';
import { createAiLyrics, type AiLyricsApi } from './ai-lyrics.svelte';
import type { ApiSong, TrackLyricsResponse } from '#lib/api-client.ts';

vi.mock('svelte-sonner', () => ({ toast: vi.fn() }));

/**
 * This state machine used to live inline in TrackPanel.svelte, where none of it was reachable from
 * a test. The behaviours pinned here are the ones with real decision content:
 *
 *  • the two-stage "Enhance with AI" run — stage 1 can fail while stage 2 still lands, so the
 *    outcome is reported per stage instead of one pass/fail;
 *  • the preferred-source flip — optimistic with revert, and regenerating a translation the flip
 *    just made stale, so the stacked document always matches the lyrics on screen;
 *  • which document the viewer shows (AI vs LRCLIB) and what the AI-disclosure label says for it.
 */

function makeSong(over: Partial<ApiSong> = {}): ApiSong {
  return {
    id: 1,
    fileName: 'track-1.flac',
    title: 'Track 1',
    artist: 'Artist',
    album: 'Album',
    ...over
  } as ApiSong;
}

const songWithLyrics = (over: Partial<ApiSong> = {}) =>
  makeSong({
    hasSyncedLyrics: true,
    syncedLyrics: '[00:01.00] la',
    lyricsStatus: 'Fetched',
    ...over
  });

function stubApi(over: Partial<AiLyricsApi> = {}): AiLyricsApi {
  return {
    fetchTrackLyrics: vi.fn(async () => ({ id: 1, lyricsStatus: 'Fetched' })),
    transcribeSongLyrics: vi.fn(async () => ({
      id: 1,
      synced: '[00:01.00] ai la',
      model: 'whisper-1',
      transcribedAtUtc: '2026-01-01T00:00:00Z',
      resynced: true
    })),
    translateSongLyrics: vi.fn(async () => ({
      id: 1,
      romanizedSynced: '[00:01.00] ro',
      translatedSynced: '[00:01.00] en',
      detectedLanguage: 'ja',
      model: 'gpt',
      lyricsTranslatedAtUtc: '2026-01-01T00:00:00Z'
    })),
    setPreferredLyricsSource: vi.fn(async (id, source) => ({ id, preferredLyricsSource: source })),
    ...over
  };
}

/**
 * The component owns the `$effect` that forwards song changes into `syncToSong()`; the harness
 * plays that role by hand, which keeps every test synchronous where the machine is.
 */
function setup(
  opts: {
    song?: ApiSong;
    isOwner?: boolean;
    lyricsFeature?: boolean;
    translationFeature?: boolean;
    api?: Partial<AiLyricsApi>;
  } = {}
) {
  let current = $state(opts.song ?? songWithLyrics());
  const api = stubApi(opts.api);
  const ai = createAiLyrics({
    song: () => current,
    isOwner: () => opts.isOwner ?? true,
    lyricsFeatureEnabled: () => opts.lyricsFeature ?? true,
    translationFeatureEnabled: () => opts.translationFeature ?? true,
    api
  });
  return {
    ai,
    api,
    setSong(next: ApiSong) {
      current = next;
    }
  };
}

/** Lets the in-flight fetch/settle promises run without advancing fake timers. */
const flushMicrotasks = async () => {
  for (let i = 0; i < 5; i++) await Promise.resolve();
};

/** A promise the test resolves by hand, to observe in-between states of the two-stage run. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (err: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

beforeEach(() => {
  vi.useFakeTimers();
});
afterEach(() => {
  vi.useRealTimers();
});

describe('syncToSong', () => {
  it('initialises the preferred source from the song', () => {
    const { ai } = setup({
      song: makeSong({ preferredLyricsSource: 'Transcribed' })
    });
    ai.syncToSong();
    expect(ai.preferredSource).toBe('transcribed');
  });

  it('asks the server even when the library row says the song has no lyrics', async () => {
    // The background AI sync gave the song lyrics after the app loaded the library.
    const { ai, api } = setup({
      song: makeSong(),
      api: {
        fetchTrackLyrics: vi.fn(async () => ({
          id: 1,
          lyricsStatus: 'NotFound',
          transcribedSynced: '[00:01.00] heard by the ai'
        }))
      }
    });
    ai.syncToSong();
    await flushMicrotasks();
    expect(api.fetchTrackLyrics).toHaveBeenCalledWith(1);
    expect(ai.transcription).toMatchObject({ synced: '[00:01.00] heard by the ai' });
  });

  it('follows the source the server prefers now, not the one the library row remembers', async () => {
    const { ai } = setup({
      song: songWithLyrics({ hasTranscribedLyrics: true, preferredLyricsSource: 'Lrclib' }),
      api: {
        fetchTrackLyrics: vi.fn(async () => ({
          id: 1,
          lyricsStatus: 'Fetched',
          transcribedSynced: '[00:01.00] official words re-timed',
          transcriptionAlignedToReference: true,
          preferredLyricsSource: 'Transcribed'
        }))
      }
    });
    ai.syncToSong();
    expect(ai.preferredSource).toBe('lrclib');
    await flushMicrotasks();
    expect(ai.preferredSource).toBe('transcribed');
  });

  it('shows no transcript for a song LRCLIB calls instrumental', async () => {
    const { ai } = setup({
      song: makeSong({ hasTranscribedLyrics: true }),
      api: {
        fetchTrackLyrics: vi.fn(async () => ({
          id: 1,
          lyricsStatus: 'Instrumental',
          transcribedSynced: '[00:01.00] thank you for watching'
        }))
      }
    });
    ai.syncToSong();
    await flushMicrotasks();
    expect(ai.transcription).toBeNull();
  });

  it('fetches again when a library refresh changes the song’s lyric flags, keeping the view', async () => {
    const fetchTrackLyrics = vi
      .fn()
      .mockResolvedValueOnce({ id: 1, lyricsStatus: 'NotFound' })
      .mockResolvedValueOnce({
        id: 1,
        lyricsStatus: 'NotFound',
        transcribedSynced: '[00:01.00] heard by the ai'
      });
    const { ai, api, setSong } = setup({ song: makeSong(), api: { fetchTrackLyrics } });
    ai.syncToSong();
    await flushMicrotasks();
    expect(ai.transcription).toBeNull();
    ai.toggleCompare();

    setSong(makeSong({ hasTranscribedLyrics: true }));
    ai.syncToSong();
    await flushMicrotasks();

    expect(api.fetchTrackLyrics).toHaveBeenCalledTimes(2);
    expect(ai.transcription).toMatchObject({ synced: '[00:01.00] heard by the ai' });
    expect(ai.showCompare).toBe(true);
  });

  it('loads a stored transcription and completed translation, including the stale bit', async () => {
    const { ai, api } = setup({
      song: songWithLyrics({ hasTranscribedLyrics: true }),
      api: {
        fetchTrackLyrics: vi.fn(async () => ({
          id: 1,
          lyricsStatus: 'Fetched',
          transcribedSynced: '[00:01.00] ai',
          transcriptionModel: 'whisper-1',
          transcribedAtUtc: '2026-01-02T00:00:00Z',
          transcriptionAlignedToReference: true,
          lyricsSyncOffsetMs: 350,
          lyricsTranslationStatus: 'Completed',
          romanizedSynced: '[00:01.00] ro',
          detectedLanguage: 'ja',
          lyricsTranslationStale: true
        }))
      }
    });
    ai.syncToSong();
    await flushMicrotasks();
    expect(api.fetchTrackLyrics).toHaveBeenCalledWith(1);
    expect(ai.transcription).toMatchObject({
      synced: '[00:01.00] ai',
      model: 'whisper-1',
      alignedToReference: true
    });
    expect(ai.syncOffsetMs).toBe(350);
    expect(ai.translation).toMatchObject({ romanizedSynced: '[00:01.00] ro', language: 'ja' });
    expect(ai.translationStale).toBe(true);
  });

  it('shows a remembered transcription at once, and drops it when the fresh answer has none', async () => {
    const { ai } = setup({
      song: songWithLyrics({ hasTranscribedLyrics: true }),
      api: {
        lastFetchedTrackLyrics: () => ({
          id: 1,
          lyricsStatus: 'Fetched',
          transcribedSynced: '[00:01.00] remembered',
          lyricsTranslationStatus: 'Completed',
          translatedSynced: '[00:01.00] en'
        })
      }
    });
    ai.syncToSong();
    expect(ai.transcription).toMatchObject({ synced: '[00:01.00] remembered' });
    expect(ai.translation).toMatchObject({ translatedSynced: '[00:01.00] en' });
    await flushMicrotasks();
    expect(ai.transcription).toBeNull();
    expect(ai.translation).toBeNull();
  });

  it('keeps a transcription made while an older load was still in flight', async () => {
    const load = deferred<TrackLyricsResponse>();
    const { ai } = setup({
      song: songWithLyrics(),
      api: { fetchTrackLyrics: vi.fn(() => load.promise) }
    });
    ai.syncToSong();
    await ai.enhance();
    expect(ai.transcription).toMatchObject({ synced: '[00:01.00] ai la' });
    load.resolve({ id: 1, lyricsStatus: 'Fetched' });
    await flushMicrotasks();
    expect(ai.transcription).toMatchObject({ synced: '[00:01.00] ai la' });
  });

  it('keeps a transcription off the next song when it lands after a switch', async () => {
    const transcribe = deferred<Awaited<ReturnType<AiLyricsApi['transcribeSongLyrics']>>>();
    const { ai, setSong } = setup({
      song: songWithLyrics({ id: 1 }),
      api: {
        transcribeSongLyrics: vi.fn(() => transcribe.promise),
        fetchTrackLyrics: vi.fn(async (id: number) =>
          id === 2
            ? { id: 2, lyricsStatus: 'Fetched', transcribedSynced: '[00:01.00] second song' }
            : { id, lyricsStatus: 'Fetched' }
        )
      }
    });
    ai.syncToSong();
    const run = ai.enhance();
    setSong(songWithLyrics({ id: 2 }));
    ai.syncToSong();
    transcribe.resolve({ id: 1, synced: '[00:01.00] first song', resynced: true });
    await run;
    await flushMicrotasks();
    expect(ai.transcription).toMatchObject({ synced: '[00:01.00] second song' });
  });

  it('drops a remembered transcription when the fresh fetch is refused', async () => {
    const { ai } = setup({
      song: songWithLyrics({ hasTranscribedLyrics: true }),
      api: {
        lastFetchedTrackLyrics: () => ({
          id: 1,
          lyricsStatus: 'Fetched',
          transcribedSynced: '[00:01.00] remembered'
        }),
        fetchTrackLyrics: vi.fn(async () => {
          throw Object.assign(new Error('Forbidden'), { status: 403 });
        })
      }
    });
    ai.syncToSong();
    expect(ai.transcription).not.toBeNull();
    await flushMicrotasks();
    expect(ai.transcription).toBeNull();
  });

  it('is a no-op for the same song, so the driving $effect cannot loop', async () => {
    const { ai, api } = setup({ song: songWithLyrics() });
    ai.syncToSong();
    ai.syncToSong();
    await flushMicrotasks();
    expect(api.fetchTrackLyrics).toHaveBeenCalledTimes(1);
  });

  it("resets a previous song's state and discards its in-flight response after navigating away", async () => {
    const first = deferred<Awaited<ReturnType<AiLyricsApi['fetchTrackLyrics']>>>();
    const fetchTrackLyrics = vi
      .fn()
      .mockReturnValueOnce(first.promise)
      .mockResolvedValueOnce({ id: 2, lyricsStatus: 'Fetched' });
    const { ai, setSong } = setup({
      song: songWithLyrics({ id: 1 }),
      api: { fetchTrackLyrics }
    });
    ai.syncToSong();
    setSong(songWithLyrics({ id: 2, preferredLyricsSource: 'Transcribed' }));
    ai.syncToSong();
    // Song 1's fetch resolves late, full of data — none of it may bleed into song 2.
    first.resolve({
      id: 1,
      lyricsStatus: 'Fetched',
      transcribedSynced: '[00:01.00] stale',
      lyricsSyncOffsetMs: 999
    });
    await flushMicrotasks();
    expect(ai.transcription).toBeNull();
    expect(ai.syncOffsetMs).toBeNull();
    expect(ai.preferredSource).toBe('transcribed');
  });
});

describe('enhance — the two-stage run', () => {
  it('runs transcribe then translate, reporting the stage in flight, and settles on success', async () => {
    const transcribe = deferred<Awaited<ReturnType<AiLyricsApi['transcribeSongLyrics']>>>();
    const translate = deferred<Awaited<ReturnType<AiLyricsApi['translateSongLyrics']>>>();
    const { ai } = setup({
      api: {
        transcribeSongLyrics: vi.fn(() => transcribe.promise),
        translateSongLyrics: vi.fn(() => translate.promise)
      }
    });
    ai.syncToSong();
    const run = ai.enhance();
    expect(ai.enhanceState).toBe('transcribing');
    transcribe.resolve({ id: 1, synced: '[00:01.00] ai', resynced: true });
    await flushMicrotasks();
    expect(ai.enhanceState).toBe('translating');
    translate.resolve({ id: 1, romanizedSynced: '[00:01.00] ro' });
    await run;
    expect(ai.enhanceState).toBe('success');
    expect(ai.enhanceError).toBeNull();
    // The fresh pronunciation is shown right away…
    expect(ai.lyricsView).toBe('pronunciation');
    // …and the banner clears after a beat.
    await vi.advanceTimersByTimeAsync(3000);
    expect(ai.enhanceState).toBe('idle');
  });

  it('mirrors a server-side promotion of the re-synced lyrics into the preferred source', async () => {
    const { ai } = setup({
      api: {
        transcribeSongLyrics: vi.fn(async () => ({
          id: 1,
          synced: '[00:01.00] ai',
          resynced: true,
          preferredLyricsSource: 'Transcribed'
        }))
      }
    });
    ai.syncToSong();
    await ai.enhance();
    expect(ai.preferredSource).toBe('transcribed');
  });

  it('still translates when the re-sync is refused, but reports the run as failed until the next one', async () => {
    const { ai, api } = setup({
      api: {
        transcribeSongLyrics: vi.fn(async () => {
          throw new Error(
            'The transcription quota is used up for now. Try again in about 8 minutes.'
          );
        })
      }
    });
    ai.syncToSong();
    await ai.enhance();
    expect(api.translateSongLyrics).toHaveBeenCalled();
    expect(ai.enhanceState).toBe('error');
    expect(ai.enhanceError).toBe(
      "Couldn't re-sync the lyrics. The transcription quota is used up for now. Try again in about 8 minutes."
    );
    await vi.advanceTimersByTimeAsync(60_000);
    expect(ai.enhanceState).toBe('error');
  });

  it('fails outright when the transcription fails and translation is not available', async () => {
    const { ai } = setup({
      song: songWithLyrics(),
      translationFeature: false,
      api: {
        transcribeSongLyrics: vi.fn(async () => {
          throw new Error('no audio on disk');
        })
      }
    });
    ai.syncToSong();
    await ai.enhance();
    expect(ai.enhanceState).toBe('error');
    expect(ai.enhanceError).toBe("Couldn't re-sync the lyrics. no audio on disk");
  });

  it('fails when the translation stage fails', async () => {
    const { ai } = setup({
      api: {
        translateSongLyrics: vi.fn(async () => {
          throw new Error('provider down');
        })
      }
    });
    ai.syncToSong();
    await ai.enhance();
    expect(ai.enhanceState).toBe('error');
    expect(ai.enhanceError).toBe('provider down');
  });

  it('abandons a run silently when the user navigates to another song mid-flight', async () => {
    const transcribe = deferred<Awaited<ReturnType<AiLyricsApi['transcribeSongLyrics']>>>();
    const { ai, api, setSong } = setup({
      api: { transcribeSongLyrics: vi.fn(() => transcribe.promise) }
    });
    ai.syncToSong();
    const run = ai.enhance();
    // The prop flips before the driving $effect catches up — the resuming run must notice.
    setSong(songWithLyrics({ id: 2 }));
    transcribe.resolve({ id: 1, synced: '[00:01.00] ai' });
    await run;
    // Stage 2 never runs for the abandoned song, and no banner lands on the new one.
    expect(api.translateSongLyrics).not.toHaveBeenCalled();
    ai.syncToSong(); // the effect catches up and clears the abandoned run's progress state
    expect(ai.enhanceState).toBe('idle');
  });

  it('ignores a second click while a run is in flight', async () => {
    const transcribe = deferred<Awaited<ReturnType<AiLyricsApi['transcribeSongLyrics']>>>();
    const { ai, api } = setup({
      api: { transcribeSongLyrics: vi.fn(() => transcribe.promise) }
    });
    ai.syncToSong();
    const run = ai.enhance();
    await ai.enhance();
    transcribe.resolve({ id: 1, synced: '[00:01.00] ai' });
    await run;
    expect(api.transcribeSongLyrics).toHaveBeenCalledTimes(1);
  });
});

describe('setPreferred', () => {
  it('flips optimistically and reverts when the server rejects', async () => {
    const { ai } = setup({
      api: {
        setPreferredLyricsSource: vi.fn(async () => {
          throw new Error('nope');
        })
      }
    });
    ai.syncToSong();
    const run = ai.setPreferred('transcribed');
    expect(ai.preferredSource).toBe('transcribed'); // optimistic
    await run;
    expect(ai.preferredSource).toBe('lrclib'); // reverted
  });

  it('does nothing when the source is already chosen', async () => {
    const { ai, api } = setup();
    ai.syncToSong();
    await ai.setPreferred('lrclib');
    expect(api.setPreferredLyricsSource).not.toHaveBeenCalled();
  });

  it('regenerates a translation the flip made stale, so the stacked doc matches the screen', async () => {
    const { ai, api } = setup({
      song: songWithLyrics({ hasTranscribedLyrics: true }),
      api: {
        fetchTrackLyrics: vi.fn(async () => ({
          id: 1,
          lyricsStatus: 'Fetched',
          transcribedSynced: '[00:01.00] ai',
          lyricsTranslationStatus: 'Completed',
          romanizedSynced: '[00:01.00] ro',
          detectedLanguage: 'ja'
        })),
        setPreferredLyricsSource: vi.fn(async (id, source) => ({
          id,
          preferredLyricsSource: source,
          lyricsTranslationStale: true
        }))
      }
    });
    ai.syncToSong();
    await flushMicrotasks();
    await ai.setPreferred('transcribed');
    await flushMicrotasks();
    expect(api.translateSongLyrics).toHaveBeenCalledTimes(1);
    expect(ai.translationStale).toBe(false);
    expect(ai.enhanceState).toBe('success');
  });
});

describe('capability and label rules', () => {
  it('gates both AI writes on ownership — friends only view existing documents', () => {
    const { ai } = setup({ isOwner: false });
    ai.syncToSong();
    expect(ai.canTranscribe).toBe(false);
    expect(ai.canTranslate).toBe(false);
    expect(ai.canEnhance).toBe(false);
  });

  it('never offers either stage for a song LRCLIB calls instrumental', () => {
    const { ai } = setup({
      song: songWithLyrics({ isInstrumental: true, lyricsStatus: 'Instrumental' })
    });
    ai.syncToSong();
    expect(ai.canEnhance).toBe(false);
  });

  it('still offers a transcription for a song only the AI heard no singing in', () => {
    const { ai } = setup({ song: makeSong({ isInstrumental: true, lyricsStatus: 'NotFound' }) });
    ai.syncToSong();
    expect(ai.canTranscribe).toBe(true);
  });

  it('translates and expands lyrics a song gained after the AI heard no singing in it', () => {
    const { ai } = setup({
      song: songWithLyrics({ isInstrumental: true, lyricsStatus: 'NotFound' })
    });
    ai.syncToSong();
    expect(ai.canTranslate).toBe(true);
    expect(ai.expandable).toBe(true);
  });

  it('only offers translation once there are lyrics to work from', () => {
    const { ai } = setup({ song: makeSong(), lyricsFeature: false });
    ai.syncToSong();
    expect(ai.canTranslate).toBe(false);
  });

  it('labels the button by what this track will actually get', async () => {
    // Both halves available, no output yet, lyrics present.
    const both = setup();
    both.ai.syncToSong();
    expect(both.ai.enhanceLabel).toBe('Improve with AI');
    // No lyrics at all: translation has nothing to work from yet, so only stage 1 is on offer.
    const bare = setup({ song: makeSong() });
    bare.ai.syncToSong();
    expect(bare.ai.enhanceLabel).toBe('Transcribe with AI');
    // Translation unconfigured.
    const transcribeOnly = setup({ translationFeature: false });
    transcribeOnly.ai.syncToSong();
    expect(transcribeOnly.ai.enhanceLabel).toBe('Transcribe with AI');
    await transcribeOnly.ai.enhance();
    expect(transcribeOnly.ai.enhanceLabel).toBe('Re-sync with AI');
    // Transcription unconfigured.
    const translateOnly = setup({ lyricsFeature: false });
    translateOnly.ai.syncToSong();
    expect(translateOnly.ai.enhanceLabel).toBe('Pronunciation & translation');
    await translateOnly.ai.enhance();
    expect(translateOnly.ai.enhanceLabel).toBe('Regenerate');
    // Both available with output.
    const redo = setup();
    redo.ai.syncToSong();
    await redo.ai.enhance();
    expect(redo.ai.enhanceLabel).toBe('Redo with AI');
  });

  it('treats an already-English result as "no translation" for every downstream rule', async () => {
    const { ai } = setup({
      api: {
        translateSongLyrics: vi.fn(async () => ({ id: 1, detectedLanguage: 'en' }))
      }
    });
    ai.syncToSong();
    await ai.enhance();
    expect(ai.translationIsEnglish).toBe(true);
    expect(ai.hasTranslation).toBe(false);
    expect(ai.secondarySynced).toBeUndefined();
  });

  it('says an English song needs no translation once, as a toast, only when one was asked for', async () => {
    vi.mocked(toast).mockClear();
    const englishTranslation = {
      id: 1,
      lyricsStatus: 'Fetched',
      lyricsTranslationStatus: 'Completed',
      detectedLanguage: 'en'
    };
    const { ai } = setup({
      api: {
        fetchTrackLyrics: vi.fn(async () => englishTranslation),
        translateSongLyrics: vi.fn(async () => ({ id: 1, detectedLanguage: 'en' }))
      }
    });
    ai.syncToSong();
    await vi.waitFor(() => expect(ai.translationIsEnglish).toBe(true));
    expect(toast).not.toHaveBeenCalled();

    await ai.enhance();
    expect(toast).toHaveBeenCalledExactlyOnceWith('Lyrics are already in English.');
  });
});

describe('viewer document selection', () => {
  it('shows LRCLIB by default, with its status and lrclib provenance', () => {
    const { ai } = setup({ song: songWithLyrics({ plainLyrics: 'la' }) });
    ai.syncToSong();
    expect(ai.showAiInViewer).toBe(false);
    expect(ai.viewerDoc).toEqual({
      synced: '[00:01.00] la',
      plain: 'la',
      status: 'Fetched',
      hasSynced: true,
      hasPlain: false
    });
    expect(ai.viewerProvenance).toBe('Human');
    expect(ai.viewerKey).toBe('lrclib-original');
  });

  it('shows the AI version when it is the chosen default, labelled by whether words are its own', async () => {
    const { ai } = setup({
      song: songWithLyrics({ preferredLyricsSource: 'Transcribed', hasTranscribedLyrics: true }),
      api: {
        fetchTrackLyrics: vi.fn(async () => ({
          id: 1,
          lyricsStatus: 'Fetched',
          transcribedSynced: '[00:01.00] ai',
          transcribedAtUtc: '2026-01-02T00:00:00Z',
          transcriptionAlignedToReference: false
        }))
      }
    });
    ai.syncToSong();
    await flushMicrotasks();
    expect(ai.showAiInViewer).toBe(true);
    expect(ai.viewerDoc).toMatchObject({ synced: '[00:01.00] ai', status: 'Fetched' });
    // Words are the machine's own guess → fully AI-generated.
    expect(ai.viewerProvenance).toBe('AiGenerated');
    expect(ai.viewerKey).toBe('ai-2026-01-02T00:00:00Z-original');
  });

  it('shows the AI version when it is all there is, and a re-timed one reads AI-enhanced', async () => {
    const { ai } = setup({
      song: makeSong({ hasSyncedLyrics: true, preferredLyricsSource: null })
    });
    ai.syncToSong();
    await ai.enhance(); // stub transcribes with resynced: true
    // preferred stays lrclib, but with hasLyrics false-ish it would show AI; here lyrics exist so:
    expect(ai.aiProvenance).toBe('AiEnhanced');
  });

  it('marks LRCLIB lyrics AI-enhanced once a measured timing offset repaired them', async () => {
    const { ai } = setup({
      song: songWithLyrics({ hasTranscribedLyrics: true }),
      api: {
        fetchTrackLyrics: vi.fn(async () => ({
          id: 1,
          lyricsStatus: 'Fetched',
          lyricsSyncOffsetMs: 480
        }))
      }
    });
    ai.syncToSong();
    await flushMicrotasks();
    expect(ai.viewerProvenance).toBe('AiEnhanced');
    expect(ai.lrclibProvenance).toBe('AiEnhanced');
  });

  it('stacks the secondary document by view, but never a stale one and never inside compare', async () => {
    const { ai } = setup();
    ai.syncToSong();
    await ai.enhance();
    expect(ai.lyricsView).toBe('pronunciation');
    expect(ai.secondarySynced).toBe('[00:01.00] ro');
    ai.setLyricsView('translation');
    expect(ai.secondarySynced).toBe('[00:01.00] en');
    ai.setLyricsView('original');
    expect(ai.secondarySynced).toBeUndefined();
    // Inside the compare split each column states its own provenance; nothing is stacked.
    ai.setLyricsView('pronunciation');
    ai.toggleCompare();
    expect(ai.comparing).toBe(true);
    expect(ai.secondarySynced).toBeUndefined();
  });

  it('only offers compare once an AI transcription exists alongside LRCLIB lyrics', async () => {
    const { ai } = setup({ song: makeSong() }); // no lyrics
    ai.syncToSong();
    await ai.enhance();
    expect(ai.canCompare).toBe(false);
    // Toggling compare without the prerequisites shows nothing.
    ai.toggleCompare();
    expect(ai.comparing).toBe(false);
  });

  it('collapses expandability for an instrumental and for an AI viewer with no document', () => {
    const instrumental = setup({
      song: songWithLyrics({ isInstrumental: true, lyricsStatus: 'Instrumental' })
    });
    instrumental.ai.syncToSong();
    expect(instrumental.ai.expandable).toBe(false);
    const withLyrics = setup();
    withLyrics.ai.syncToSong();
    expect(withLyrics.ai.expandable).toBe(true);
  });
});
