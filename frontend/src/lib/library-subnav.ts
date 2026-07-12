import { Disc3, Heart, LayoutGrid, ListMusic, Music2, Users } from '@lucide/svelte';

export type LibrarySubNavId = 'overview' | 'albums' | 'artists' | 'tracks' | 'liked' | 'spotify';

// Order + labels + hrefs + icons for the v2 Library sub-nav. Shared so the bar
// stays identical wherever it's rendered (LibraryV2 and the Spotify page) — the
// two must never drift. Counts are applied by the caller: LibraryV2 has the song
// set and attaches live counts; the Spotify page renders it count-less.
export const LIBRARY_SUBNAV = [
  { id: 'overview', label: 'Overview', href: '/overview', icon: LayoutGrid },
  { id: 'albums', label: 'Albums', href: '/library', icon: Disc3 },
  { id: 'artists', label: 'Artists', href: '/artists', icon: Users },
  { id: 'tracks', label: 'All tracks', href: '/tracks', icon: ListMusic },
  { id: 'liked', label: 'Liked', href: '/liked', icon: Heart },
  { id: 'spotify', label: 'Spotify', href: '/spotify', icon: Music2 }
] as const;
