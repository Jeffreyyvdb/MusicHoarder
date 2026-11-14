import type {
  AcquireReleasesResult,
  ArtistRelease,
  ArtistReleaseKind,
  FollowedRelease
} from '#lib/api-client.ts';

/** Where a release stands for you, which decides its card's badge and whether it can be fetched. */
export type ReleaseState = 'complete' | 'partial' | 'missing' | 'queued' | 'upcoming';

export function releaseState(r: ArtistRelease): ReleaseState {
  if (r.upcoming) return 'upcoming';
  if (r.totalTracks != null && r.totalTracks > 0 && r.ownedTracks >= r.totalTracks)
    return 'complete';
  if (r.queuedTracks > 0) return 'queued';
  // Owned, with no tracklist to say what is missing: as far as anyone knows, it is all there.
  if (r.ownedTracks > 0) return r.totalTracks == null ? 'complete' : 'partial';
  return 'missing';
}

/** Whether "Get" has anything to do: something is missing and nothing is on its way already. */
export function canAcquire(r: ArtistRelease): boolean {
  const state = releaseState(r);
  return state === 'missing' || state === 'partial';
}

export const KIND_LABEL: Record<ArtistReleaseKind, string> = {
  album: 'Album',
  ep: 'EP',
  single: 'Single',
  compilation: 'Compilation'
};

/** The plural, for the follow settings ("Albums", "EPs"). */
export const KIND_PLURAL: Record<ArtistReleaseKind, string> = {
  album: 'Albums',
  ep: 'EPs',
  single: 'Singles',
  compilation: 'Compilations'
};

export const ALL_KINDS: readonly ArtistReleaseKind[] = ['album', 'ep', 'single', 'compilation'];

export function releaseYear(r: Pick<ArtistRelease, 'releaseDate'>): number | null {
  const year = r.releaseDate ? Number.parseInt(r.releaseDate.slice(0, 4), 10) : NaN;
  return Number.isFinite(year) ? year : null;
}

/** A card's second line, the way Spotify writes it: "2024 · Album". */
export function releaseCaption(r: Pick<ArtistRelease, 'releaseDate' | 'kind'>): string {
  const year = releaseYear(r);
  return year ? `${year} · ${KIND_LABEL[r.kind]}` : KIND_LABEL[r.kind];
}

function formatDay(iso: string): string {
  const date = new Date(`${iso.slice(0, 10)}T00:00:00Z`);
  return Number.isNaN(date.getTime())
    ? iso
    : date.toLocaleDateString(undefined, { day: 'numeric', month: 'short', timeZone: 'UTC' });
}

/** The badge under a card, or null when there is nothing worth saying (a release you lack). */
export function releaseStatusLabel(r: ArtistRelease): string | null {
  switch (releaseState(r)) {
    case 'upcoming':
      return r.releaseDate ? `Out ${formatDay(r.releaseDate)}` : 'Coming soon';
    case 'complete':
      return 'In your library';
    case 'queued':
      return `Downloading ${r.queuedTracks} track${r.queuedTracks === 1 ? '' : 's'}`;
    case 'partial':
      return `${r.ownedTracks} of ${r.totalTracks} tracks`;
    default:
      return r.failedTracks > 0 ? `${r.failedTracks} not found` : null;
  }
}

/** The chips over the discography: Spotify's three groups, plus everything. */
export type DiscographyFilter = 'all' | 'albums' | 'singles' | 'compilations';

export const DISCOGRAPHY_FILTERS: readonly { id: DiscographyFilter; label: string }[] = [
  { id: 'all', label: 'All' },
  { id: 'albums', label: 'Albums' },
  { id: 'singles', label: 'Singles and EPs' },
  { id: 'compilations', label: 'Compilations' }
];

export function matchesFilter(r: Pick<ArtistRelease, 'kind'>, filter: DiscographyFilter): boolean {
  switch (filter) {
    case 'albums':
      return r.kind === 'album';
    case 'singles':
      return r.kind === 'single' || r.kind === 'ep';
    case 'compilations':
      return r.kind === 'compilation';
    default:
      return true;
  }
}

/** The filters worth a chip: All, and every group that has at least one release. */
export function availableFilters(
  releases: readonly Pick<ArtistRelease, 'kind'>[]
): DiscographyFilter[] {
  return DISCOGRAPHY_FILTERS.map((f) => f.id).filter(
    (id) => id === 'all' || releases.some((r) => matchesFilter(r, id))
  );
}

export function filterReleases(
  releases: readonly ArtistRelease[],
  filter: DiscographyFilter,
  missingOnly = false
): ArtistRelease[] {
  return releases.filter(
    (r) => matchesFilter(r, filter) && (!missingOnly || releaseState(r) !== 'complete')
  );
}

/** What "Get all missing" would fetch among the releases on screen. */
export function acquirableReleases(releases: readonly ArtistRelease[]): ArtistRelease[] {
  return releases.filter(canAcquire);
}

/** One line for the discography header: "14 releases · 9 in your library". */
export function discographySummary(releases: readonly ArtistRelease[]): string {
  const out = releases.filter((r) => !r.upcoming);
  const have = out.filter((r) => releaseState(r) === 'complete').length;
  const noun = `${out.length} release${out.length === 1 ? '' : 's'}`;
  return have > 0 ? `${noun} · ${have} in your library` : noun;
}

/** The toast after a "Get": what was queued, and why the rest was not. */
export function acquireSummary(result: AcquireReleasesResult): string {
  const errors = result.results.filter((r) => r.error).length;
  const owned = result.results.reduce((n, r) => n + r.alreadyOwned, 0);
  const onItsWay = result.results.reduce((n, r) => n + r.alreadyQueued, 0);
  const parts: string[] = [];
  if (result.queued > 0)
    parts.push(`Queued ${result.queued} track${result.queued === 1 ? '' : 's'}`);
  else if (owned > 0 || onItsWay > 0) parts.push('Nothing new to download');
  if (owned > 0 && result.queued > 0) parts.push(`${owned} already in your library`);
  if (onItsWay > 0 && result.queued > 0) parts.push(`${onItsWay} already on the way`);
  if (result.capped > 0) parts.push(`${result.capped} left for another time (request limit)`);
  if (result.skippedReleases > 0)
    parts.push(
      `${result.skippedReleases} release${result.skippedReleases === 1 ? '' : 's'} not started (request limit)`
    );
  if (errors > 0) parts.push(`${errors} release${errors === 1 ? '' : 's'} could not be read`);
  return parts.length > 0 ? parts.join(' · ') : 'Nothing to download';
}

/**
 * A follow's kinds with one switched on or off. Null when that would leave none: a follow that
 * watches nothing is an unfollow, which has its own button.
 */
export function toggleReleaseKind(
  kinds: readonly ArtistReleaseKind[],
  kind: ArtistReleaseKind,
  on: boolean
): ArtistReleaseKind[] | null {
  const next = on ? [...new Set([...kinds, kind])] : kinds.filter((k) => k !== kind);
  return next.length > 0 ? next : null;
}

/** A catalog artist's second line: "5.2M fans · 39 releases". */
export function catalogArtistCaption(artist: { fans: number; albumCount: number }): string {
  const n = artist.fans;
  const fans =
    n >= 1_000_000
      ? `${(n / 1_000_000).toFixed(1).replace(/\.0$/, '')}M fans`
      : n >= 1_000
        ? `${Math.round(n / 1_000)}K fans`
        : `${n} fan${n === 1 ? '' : 's'}`;
  return artist.albumCount > 0 ? `${fans} · ${artist.albumCount} releases` : fans;
}

/** An error's own message, else the fallback. */
export function messageOf(err: unknown, fallback: string): string {
  return err instanceof Error && err.message ? err.message : fallback;
}

/** The settings line on a followed artist: "Albums and EPs · downloads automatically". */
export function followSummary(kinds: readonly ArtistReleaseKind[], autoDownload: boolean): string {
  const names = ALL_KINDS.filter((k) => kinds.includes(k)).map((k) => KIND_PLURAL[k]);
  const list =
    names.length <= 1
      ? (names[0] ?? 'Nothing')
      : `${names.slice(0, -1).join(', ')} and ${names.at(-1)}`;
  return `${list} · ${autoDownload ? 'downloads automatically' : 'notify only'}`;
}

/** What the monitor did with a release, for the Following page's list of new releases. */
export function decisionLabel(r: Pick<FollowedRelease, 'decision' | 'tracksQueued'>): string {
  switch (r.decision) {
    case 'queued':
      return `Downloading ${r.tracksQueued} track${r.tracksQueued === 1 ? '' : 's'}`;
    case 'owned':
      return 'Already in your library';
    case 'noticed':
      return 'New — not downloaded';
    case 'failed':
      return 'Could not be read, trying again';
    default:
      return '';
  }
}

/**
 * The artist page's "Popular": your most-played songs of theirs. Spotify ranks by everyone's plays;
 * here the only listener whose plays count is you. Ties (and a library you never played) fall back to
 * liked first, then the most recently played, so the section is never empty while there are songs.
 */
export function popularSongs<T>(
  songs: readonly T[],
  statsOf: (song: T) => { plays: number; lastPlayed: number; liked: boolean },
  limit: number
): T[] {
  return songs
    .map((song, index) => ({ song, index, ...statsOf(song) }))
    .sort(
      (a, b) =>
        b.plays - a.plays ||
        Number(b.liked) - Number(a.liked) ||
        b.lastPlayed - a.lastPlayed ||
        a.index - b.index
    )
    .slice(0, limit)
    .map((x) => x.song);
}
