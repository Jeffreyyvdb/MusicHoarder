import { importTrack, type CatalogTrack } from '#lib/api-client.ts';
import { messageOf } from '#lib/artist-discography.ts';
import { wishlistLabel } from '#lib/catalog-status.ts';
import { toast } from 'svelte-sonner';

// Adding catalog tracks to the wishlist, shared by the palette's results and its preview bar, so
// both show the same state for a track and an add from either is one request.

/** The last search's tracks, so the preview bar can add the track it is playing. */
let known = $state.raw<ReadonlyMap<string, CatalogTrack>>(new Map());
let adding = $state.raw<ReadonlySet<string>>(new Set());
/** The wishlist status each track was left in by an add made here. */
let added = $state.raw<ReadonlyMap<string, string>>(new Map());

function remember(tracks: readonly CatalogTrack[]): void {
  known = new Map(tracks.map((t) => [t.deezerId, t]));
}

/** The word shown instead of Add (a status, or "Queued"), or null when the track can be added. */
function labelOf(track: CatalogTrack): string | null {
  const status = added.get(track.deezerId);
  if (status !== undefined)
    return status === 'Pending' ? 'Queued' : (wishlistLabel(status) ?? 'Queued');
  return wishlistLabel(track.wishlistStatus);
}

function canAdd(track: CatalogTrack): boolean {
  return !adding.has(track.deezerId) && labelOf(track) === null;
}

async function add(track: CatalogTrack): Promise<void> {
  if (!canAdd(track)) return;
  adding = new Set([...adding, track.deezerId]);
  try {
    const result = await importTrack({
      deezerTrackId: track.deezerId,
      title: track.title,
      artist: track.artist,
      album: track.album,
      durationMs: track.durationMs,
      coverUrl: track.coverUrl
    });
    added = new Map([...added, [track.deezerId, result.status ?? 'Pending']]);
    const already = wishlistLabel(result.status);
    if (already && result.status !== 'Pending') {
      toast(`${track.title} is already on the wishlist`, { description: already });
    } else {
      toast.success(`Added ${track.title}`, { description: 'It downloads in the background.' });
    }
  } catch (err) {
    toast.error(messageOf(err, 'Could not add that song.'));
  } finally {
    adding = new Set([...adding].filter((id) => id !== track.deezerId));
  }
}

export const catalogAdds = {
  remember,
  track: (deezerId: string): CatalogTrack | undefined => known.get(deezerId),
  isAdding: (deezerId: string): boolean => adding.has(deezerId),
  labelOf,
  canAdd,
  add
};
