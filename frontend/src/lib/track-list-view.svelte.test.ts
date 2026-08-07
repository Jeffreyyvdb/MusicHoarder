import { describe, expect, it } from 'vitest';
import { flushSync } from 'svelte';
import { createTrackListView } from './track-list-view.svelte';
import type { ApiSong } from './api-client';

/**
 * The filter/sort state used to live inside TrackList.svelte, where none of this
 * was reachable from a test. The behaviour worth pinning is the coupling between
 * the "From Spotify" chip and the sort key — it is the one filter that reorders
 * the list, and turning it off has to restore the list's own default rather than
 * strand the user on a sort key no column header can reach.
 */
function song(id: number, over: Partial<ApiSong> = {}): ApiSong {
  return {
    id,
    fileName: `track-${id}.flac`,
    title: `Track ${id}`,
    artist: 'Artist',
    album: 'Album',
    durationSeconds: 100,
    fileSizeBytes: 1000,
    ...over
  } as ApiSong;
}

const spotifySong = (id: number) => song(id, { originSource: 'SpotifyLiked' });

function view(songs: ApiSong[], initialSortKey?: 'added' | 'liked') {
  return createTrackListView({
    songs: () => songs,
    searchQuery: () => '',
    ...(initialSortKey ? { initialSortKey } : {})
  });
}

describe('createTrackListView', () => {
  it('sorts by the Spotify save date while the Spotify filter is on', () => {
    const cleanup = $effect.root(() => {
      const v = view([song(1), spotifySong(2)]);
      expect(v.sortKey).toBe('added');

      v.toggleSpotifyOnly();
      flushSync();
      expect(v.spotifyOnly).toBe(true);
      expect(v.sortKey).toBe('spotify');
      expect(v.sortDir).toBe('desc');
      expect(v.sorted.map((s) => s.id)).toEqual([2]);
    });
    cleanup();
  });

  it('restores the list default sort when the Spotify filter is turned off', () => {
    const cleanup = $effect.root(() => {
      // 'liked' is the Liked-songs page's default — turning the chip off must
      // land back there, not on the generic 'added'.
      const v = view([song(1), spotifySong(2)], 'liked');
      v.toggleSpotifyOnly();
      flushSync();
      v.toggleSpotifyOnly();
      flushSync();
      expect(v.spotifyOnly).toBe(false);
      expect(v.sortKey).toBe('liked');
    });
    cleanup();
  });

  it('clears both filters and restores the sort in one call', () => {
    const cleanup = $effect.root(() => {
      const v = view([song(1), spotifySong(2)], 'liked');
      v.lyricsOnly = true;
      v.toggleSpotifyOnly();
      flushSync();
      expect(v.hasFilters).toBe(true);

      v.clearFilters();
      flushSync();
      expect(v.hasFilters).toBe(false);
      expect(v.lyricsOnly).toBe(false);
      expect(v.spotifyOnly).toBe(false);
      expect(v.sortKey).toBe('liked');
    });
    cleanup();
  });

  it('toggles direction on a repeat sort click and defaults strings ascending', () => {
    const cleanup = $effect.root(() => {
      const v = view([song(1), song(2)]);
      v.toggleSort('title');
      flushSync();
      expect(v.sortKey).toBe('title');
      expect(v.sortDir).toBe('asc');

      v.toggleSort('title');
      flushSync();
      expect(v.sortDir).toBe('desc');

      v.toggleSort('size');
      flushSync();
      expect(v.sortDir).toBe('desc');
    });
    cleanup();
  });

  it('reports stats over the filtered set, not the whole list', () => {
    const cleanup = $effect.root(() => {
      const v = view([song(1), spotifySong(2)]);
      expect(v.stats.count).toBe(2);
      expect(v.stats.totalBytes).toBe(2000);

      v.toggleSpotifyOnly();
      flushSync();
      expect(v.stats.count).toBe(1);
      expect(v.stats.totalSec).toBe(100);
      expect(v.spotifyCount).toBe(1);
    });
    cleanup();
  });
});
