import { describe, expect, it } from 'vitest';
import type { PlaylistSource } from '$lib/api-client';
import { missingLabel, playlistKindLabel, playlistSubtitle, trackCountLabel } from './playlists';

const source = (type: PlaylistSource['type']): PlaylistSource => ({
  id: 1,
  type,
  name: 'Road trip',
  autoSync: true
});

describe('playlist wording', () => {
  it('counts tracks', () => {
    expect(trackCountLabel(1)).toBe('1 track');
    expect(trackCountLabel(42)).toBe('42 tracks');
  });

  it('names a synced playlist by where it comes from', () => {
    expect(playlistSubtitle({ source: null, songIds: [1, 2] })).toBe('2 tracks');
    expect(playlistSubtitle({ source: source('spotifyPlaylist'), songIds: [1] })).toBe(
      'Spotify · 1 track'
    );
    expect(playlistSubtitle({ source: source('youtube'), songIds: [] })).toBe('YouTube · Empty');
    expect(playlistKindLabel({ source: null })).toBe('Playlist');
    expect(playlistKindLabel({ source: source('spotifyLiked') })).toBe('Spotify Liked Songs');
    expect(playlistKindLabel({ source: source('deezer') })).toBe('Deezer playlist');
  });

  it('only mentions missing tracks for a synced playlist that has some', () => {
    expect(missingLabel({ source: source('spotifyPlaylist'), missingCount: 8 })).toBe(
      '8 not in your library yet'
    );
    expect(missingLabel({ source: source('spotifyPlaylist'), missingCount: 0 })).toBeNull();
    expect(missingLabel({ source: null, missingCount: 3 })).toBeNull();
  });
});
