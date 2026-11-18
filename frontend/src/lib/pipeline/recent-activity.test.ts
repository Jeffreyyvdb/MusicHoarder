import { describe, expect, it } from 'vitest';
import type { ApiOverviewActivity } from '#lib/api-client.ts';
import { activityLabel, activitySubline } from '#lib/pipeline/recent-activity.ts';

type Kind = ApiOverviewActivity['kind'];

const song = (kind: Kind, artist: string | null = 'Artist'): ApiOverviewActivity => ({
  id: `${kind}-1`,
  kind,
  at: '2026-11-18T12:00:00Z',
  time: 'just now',
  subject: { type: 'song', songId: 1, title: 'Title', artist }
});

const album = (kind: Kind, artist: string | null = 'Artist'): ApiOverviewActivity => ({
  id: `${kind}-album-1`,
  kind,
  at: '2026-11-18T12:00:00Z',
  time: '3 min ago',
  subject: { type: 'album', album: 'Album', artist, count: 12 }
});

describe('recent activity labels', () => {
  it('names a song row by what happened', () => {
    expect(activityLabel(song('added'))).toBe('Added to library');
    expect(activityLabel(song('matched'))).toBe('Matched');
    expect(activityLabel(song('review'))).toBe('Needs review');
    expect(activityLabel(song('failed'))).toBe('Failed');
  });

  it('counts the songs of an album row', () => {
    expect(activityLabel(album('added'))).toBe('12 songs added');
    expect(activityLabel(album('matched'))).toBe('12 songs matched');
    expect(activityLabel(album('review'))).toBe('12 songs need review');
    expect(activityLabel(album('failed'))).toBe('12 songs failed');
  });

  it('puts the title then the artist on a song subline', () => {
    expect(activitySubline(song('added'))).toBe('Title · Artist');
  });

  it('puts the album then the artist on an album subline', () => {
    expect(activitySubline(album('added'))).toBe('Album · Artist');
  });

  it('leaves out a blank or missing artist', () => {
    expect(activitySubline(song('added', null))).toBe('Title');
    expect(activitySubline(song('added', '  '))).toBe('Title');
    expect(activitySubline(album('added', null))).toBe('Album');
  });
});
