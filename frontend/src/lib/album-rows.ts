import type { AlbumTracklist, ApiSong } from '$lib/api-client';

/**
 * A single row in the album track list: either a song the user owns, or a canonical track they're
 * missing (greyed out). `key` is the identity used by the keyed `{#each}` in AlbumPage and MUST be
 * unique across the whole list — a duplicate key crashes Svelte with `each_key_duplicate`.
 */
export type DisplayRow =
  | { kind: 'owned'; key: string; disc: number; n: number; song: ApiSong; durationSeconds: number | null }
  | {
      kind: 'missing';
      key: string;
      disc: number;
      n: number;
      title: string;
      durationSeconds: number | null;
      contested: boolean;
    };

/**
 * Merge the user's owned songs with the reconciled canonical tracklist into display rows.
 *
 * Compilations ("Various Artists") are the stress case for key uniqueness:
 *   - a contested/merged canonical tracklist can carry two entries with the same disc+track number,
 *     so missing rows are keyed on the canonical index (`miss:${idx}`), never disc+track;
 *   - imperfect matching can point two canonical tracks at the same owned song, so a song already
 *     claimed by an earlier track falls through to a canonical (missing) slot rather than emitting a
 *     second `song:${id}` row;
 *   - the owned-only fallback (no tracklist yet) and the bonus-track tail both dedupe on song id.
 * Every `key` this returns is therefore unique.
 */
export function buildDisplayRows(songs: ApiSong[] | undefined, tracklist: AlbumTracklist | null): DisplayRow[] {
  const owned = songs ?? [];
  const used = new Set<number>();

  if (!tracklist) {
    // No canonical tracklist yet: owned-only. Dedupe by id so a repeated song can't dup its key.
    const rows: DisplayRow[] = [];
    owned.forEach((song, idx) => {
      if (used.has(song.id)) return;
      used.add(song.id);
      rows.push({
        kind: 'owned',
        key: `song:${song.id}`,
        disc: 1,
        n: song.trackNumber ?? idx + 1,
        song,
        durationSeconds: song.durationSeconds ?? null
      });
    });
    return rows;
  }

  const byId = new Map(owned.map((s) => [s.id, s]));
  const rows: DisplayRow[] = tracklist.tracks.map((t, idx) => {
    const song = t.ownedSongId != null ? (byId.get(t.ownedSongId) ?? null) : null;
    if (song && !used.has(song.id)) {
      used.add(song.id);
      return {
        kind: 'owned',
        key: `song:${song.id}`,
        disc: t.discNumber,
        n: t.trackNumber,
        song,
        durationSeconds: song.durationSeconds ?? (t.durationMs != null ? t.durationMs / 1000 : null)
      };
    }
    return {
      kind: 'missing',
      key: `miss:${idx}`,
      disc: t.discNumber,
      n: t.trackNumber,
      title: (t.title ?? '').trim() || 'Unknown track',
      durationSeconds: t.durationMs != null ? t.durationMs / 1000 : null,
      contested: t.isContested
    };
  });

  // Owned songs not matched to any canonical track (bonus tracks, alternate versions) — append so
  // nothing the user actually owns disappears from the view.
  let bonusN = tracklist.tracks.length;
  for (const song of owned) {
    if (used.has(song.id)) continue;
    used.add(song.id);
    bonusN += 1;
    rows.push({
      kind: 'owned',
      key: `song:${song.id}`,
      disc: 1,
      n: song.trackNumber ?? bonusN,
      song,
      durationSeconds: song.durationSeconds ?? null
    });
  }
  return rows;
}
