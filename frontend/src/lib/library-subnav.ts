import { Disc3, Users, ListMusic, Music2 } from '@lucide/svelte';

export type LibrarySubNavId = 'albums' | 'artists' | 'tracks' | 'spotify';

// Order + labels + hrefs + icons for the v2 Library sub-nav. Shared so the bar
// stays identical wherever it's rendered (LibraryV2 and the Spotify page) — the
// two must never drift. Counts are applied by the caller: LibraryV2 has the song
// set and attaches live counts; the Spotify page renders it count-less.
export const LIBRARY_SUBNAV = [
  { id: 'albums', label: 'Albums', href: '/library', icon: Disc3 },
  { id: 'artists', label: 'Artists', href: '/artists', icon: Users },
  { id: 'tracks', label: 'All tracks', href: '/tracks', icon: ListMusic },
  { id: 'spotify', label: 'Spotify', href: '/spotify', icon: Music2 }
] as const;
