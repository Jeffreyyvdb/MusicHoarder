import {
  fetchArtistDiscography,
  fetchArtistSuggestions,
  fetchFollowing,
  followArtist,
  type ArtistSuggestion,
  type FollowedArtist
} from '#lib/api-client.ts';

/**
 * What following a suggestion came to. `needs-choice`: the catalog lists more than one artist of the
 * name and none is proven, so its page asks. `not-found`: the catalog has no artist of that name.
 */
export type SuggestionFollowResult = 'followed' | 'needs-choice' | 'not-found';

/**
 * The two shelves above the Artists grid (admin only): the artists you follow, and the ones your
 * listening suggests. One module instance, so coming back to the page shows the last answer at once
 * while it refreshes; an account change hard-reloads, so it never outlives its account.
 */
class ArtistShelves {
  follows = $state.raw<FollowedArtist[] | null>(null);
  suggestions = $state.raw<ArtistSuggestion[] | null>(null);
  /** Suggestions with a follow in flight, by artist name. Replaced, never mutated. */
  pending = $state.raw<ReadonlySet<string>>(new Set());

  #token = 0;

  async load(): Promise<void> {
    const token = ++this.#token;
    const [follows, suggestions] = await Promise.allSettled([
      fetchFollowing(),
      fetchArtistSuggestions()
    ]);
    if (token !== this.#token) return;
    if (follows.status === 'fulfilled') this.follows = follows.value.follows;
    if (suggestions.status === 'fulfilled') this.suggestions = suggestions.value;
  }

  /**
   * Follows a suggested artist. The suggestion carries a catalog id only when one is proven; without
   * it the artist page's own lookup proves one (the same request the page makes), and when even that
   * cannot, the caller sends the person to the page to pick.
   *
   * `beforeMove` runs once the follow exists and just before it moves between the shelves, while the
   * suggestion's tile is still on screen: the page's last chance to see where it was.
   */
  async follow(
    suggestion: ArtistSuggestion,
    beforeMove?: (follow: FollowedArtist) => void
  ): Promise<SuggestionFollowResult> {
    const { artist } = suggestion;
    this.pending = new Set([...this.pending, artist]);
    try {
      let id = suggestion.deezerArtistId ?? null;
      let follow: FollowedArtist | null = null;
      let candidates = 0;
      if (!id) {
        const discography = await fetchArtistDiscography(artist);
        follow = discography.follow;
        id = discography.catalog.deezerArtistId;
        candidates = discography.candidates.length;
      }
      if (!follow) {
        if (!id) return candidates > 0 ? 'needs-choice' : 'not-found';
        follow = await followArtist({ deezerArtistId: id, artist });
      }
      beforeMove?.(follow);
      this.#adopt(follow, artist);
      return 'followed';
    } finally {
      const rest = new Set(this.pending);
      rest.delete(artist);
      this.pending = rest;
    }
  }

  #adopt(follow: FollowedArtist, artist: string) {
    // A refresh still in flight was asked before this follow existed; its answer would undo it.
    this.#token++;
    const others = (this.follows ?? []).filter((f) => f.id !== follow.id);
    this.follows = [...others, follow].sort((a, b) =>
      a.name.localeCompare(b.name, undefined, { sensitivity: 'base' })
    );
    this.suggestions = (this.suggestions ?? []).filter((s) => s.artist !== artist);
  }
}

export const artistShelves = new ArtistShelves();
