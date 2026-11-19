import { describe, expect, it, vi } from 'vitest';
import {
  fetchTrackLyrics,
  lastFetchedTrackLyrics,
  transcribeSongLyrics,
  type TrackLyricsResponse
} from './api-client';

/*
 * Now Playing shows the last lyrics answer for a track while its fresh fetch runs, so reopening it
 * does not flash "Loading lyrics…". What it remembers must never be older than what the panel has
 * already seen: a refusal forgets it, a write forgets it, and a read that set off before a write
 * landed is not remembered.
 */

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });

const lyrics = (id: number, synced: string): TrackLyricsResponse => ({
  id,
  lyricsStatus: 'Fetched',
  synced
});

describe('the remembered track lyrics', () => {
  it('remembers an answer, and forgets it on a refusal', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => json(lyrics(1, '[00:01.00] one')))
    );
    await fetchTrackLyrics(1);
    expect(lastFetchedTrackLyrics(1)?.synced).toBe('[00:01.00] one');

    vi.stubGlobal(
      'fetch',
      vi.fn(async () => json({ error: 'Not found' }, 404))
    );
    await expect(fetchTrackLyrics(1)).rejects.toThrow();
    expect(lastFetchedTrackLyrics(1)).toBeUndefined();
  });

  it('keeps it through a failure that asking again may fix', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => json(lyrics(2, '[00:01.00] two')))
    );
    await fetchTrackLyrics(2);
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => json({ error: 'Unavailable' }, 503))
    );
    await expect(fetchTrackLyrics(2)).rejects.toThrow();
    expect(lastFetchedTrackLyrics(2)?.synced).toBe('[00:01.00] two');
  });

  it('forgets it after a write, and does not remember a read that started before the write', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => json(lyrics(3, '[00:01.00] remembered')))
    );
    await fetchTrackLyrics(3);
    let answerRead: (r: Response) => void = () => {};
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        if (url.includes('/lyrics/transcribe')) return json({ id: 3, synced: '[00:01.00] new' });
        return new Promise<Response>((resolve) => (answerRead = resolve));
      })
    );
    const read = fetchTrackLyrics(3);
    await transcribeSongLyrics(3);
    expect(lastFetchedTrackLyrics(3)).toBeUndefined();
    answerRead(json(lyrics(3, '[00:01.00] old')));
    await expect(read).resolves.toMatchObject({ synced: '[00:01.00] old' });
    expect(lastFetchedTrackLyrics(3)).toBeUndefined();
  });

  it('keeps a newer answer when a read older than a write is refused', async () => {
    let refuseOld: (r: Response) => void = () => {};
    let reads = 0;
    vi.stubGlobal(
      'fetch',
      vi.fn(async (url: string) => {
        if (url.includes('/lyrics/transcribe')) return json({ id: 4, synced: '[00:01.00] new' });
        reads += 1;
        return reads === 1
          ? new Promise<Response>((resolve) => (refuseOld = resolve))
          : json(lyrics(4, '[00:01.00] new'));
      })
    );
    const old = fetchTrackLyrics(4);
    await transcribeSongLyrics(4);
    await fetchTrackLyrics(4);
    refuseOld(json({ error: 'Forbidden' }, 403));
    await expect(old).rejects.toThrow();
    expect(lastFetchedTrackLyrics(4)?.synced).toBe('[00:01.00] new');
  });
});
