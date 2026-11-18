import type { ApiOverviewActivity } from '#lib/api-client.ts';

const SONG_LABEL: Record<ApiOverviewActivity['kind'], string> = {
  added: 'Added to library',
  matched: 'Matched',
  review: 'Needs review',
  failed: 'Failed'
};

const ALBUM_VERB: Record<ApiOverviewActivity['kind'], string> = {
  added: 'added',
  matched: 'matched',
  review: 'need review',
  failed: 'failed'
};

/** The row's headline: what happened. */
export function activityLabel(a: ApiOverviewActivity): string {
  if (a.subject.type === 'song') return SONG_LABEL[a.kind];
  return `${a.subject.count} songs ${ALBUM_VERB[a.kind]}`;
}

/** The row's second line: what it happened to, "Title · Artist" or "Album · Artist". */
export function activitySubline(a: ApiOverviewActivity): string {
  const name = a.subject.type === 'song' ? a.subject.title : a.subject.album;
  const artist = a.subject.artist?.trim();
  return artist ? `${name} · ${artist}` : name;
}
