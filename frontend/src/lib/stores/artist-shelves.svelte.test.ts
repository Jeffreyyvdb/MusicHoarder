import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { ArtistDiscography, ArtistSuggestion, FollowedArtist } from '#lib/api-client.ts';

const api = vi.hoisted(() => ({
  fetchFollowing: vi.fn(),
  fetchArtistSuggestions: vi.fn(),
  fetchArtistDiscography: vi.fn(),
  followArtist: vi.fn()
}));
vi.mock('#lib/api-client.ts', () => api);

function followed(id: number, name: string): FollowedArtist {
  return {
    id,
    deezerArtistId: String(id),
    name,
    pictureUrl: null,
    releaseKinds: ['album', 'ep'],
    autoDownload: true,
    followedAtUtc: '2026-11-23T00:00:00Z',
    lastCheckedAtUtc: null,
    nextCheckAtUtc: null,
    lastError: null,
    newReleaseCount: 0
  };
}

function suggestion(artist: string, deezerArtistId?: string): ArtistSuggestion {
  return { artist, plays: 5, songs: 3, lastPlayedAtUtc: '2026-11-22T00:00:00Z', deezerArtistId };
}

function discography(
  id: string | null,
  candidates: ArtistDiscography['candidates'] = []
): ArtistDiscography {
  return {
    artist: 'x',
    catalog: {
      status: id ? 'linked' : 'unconfirmed',
      deezerArtistId: id,
      name: null,
      pictureUrl: null,
      fans: 0
    },
    candidates,
    follow: null,
    releases: [],
    releasesUnavailable: false,
    downloadsEnabled: true
  };
}

async function loadShelves(follows: FollowedArtist[], suggestions: ArtistSuggestion[]) {
  vi.resetModules();
  api.fetchFollowing.mockResolvedValue({
    follows,
    recent: [],
    monitorEnabled: true,
    downloadsEnabled: true
  });
  api.fetchArtistSuggestions.mockResolvedValue(suggestions);
  const { artistShelves } = await import('./artist-shelves.svelte.ts');
  await artistShelves.load();
  return artistShelves;
}

describe('artist shelves', () => {
  beforeEach(() => {
    for (const fn of Object.values(api)) fn.mockReset();
  });

  it('follows a proven suggestion directly and moves it onto the Following shelf in name order', async () => {
    const shelves = await loadShelves(
      [followed(1, 'Zed')],
      [suggestion('Abel', '7'), suggestion('Bo')]
    );
    api.followArtist.mockResolvedValue(followed(7, 'Abel'));

    await expect(shelves.follow(suggestion('Abel', '7'))).resolves.toBe('followed');

    expect(api.fetchArtistDiscography).not.toHaveBeenCalled();
    expect(api.followArtist).toHaveBeenCalledWith({ deezerArtistId: '7', artist: 'Abel' });
    expect(shelves.follows?.map((f) => f.name)).toEqual(['Abel', 'Zed']);
    expect(shelves.suggestions?.map((s) => s.artist)).toEqual(['Bo']);
    expect(shelves.pending.size).toBe(0);
  });

  it('proves an unlinked suggestion through the artist lookup before following', async () => {
    const shelves = await loadShelves([], [suggestion('Bo')]);
    api.fetchArtistDiscography.mockResolvedValue(discography('9'));
    api.followArtist.mockResolvedValue(followed(9, 'Bo'));

    await expect(shelves.follow(suggestion('Bo'))).resolves.toBe('followed');

    expect(api.followArtist).toHaveBeenCalledWith({ deezerArtistId: '9', artist: 'Bo' });
    expect(shelves.suggestions).toEqual([]);
  });

  it('asks for a choice when several catalog artists share the name, and follows nothing', async () => {
    const shelves = await loadShelves([], [suggestion('Bo')]);
    const candidate = { id: '1', name: 'Bo', pictureUrl: null, fans: 0, albumCount: 1 };
    api.fetchArtistDiscography.mockResolvedValue(
      discography(null, [candidate, { ...candidate, id: '2' }])
    );

    await expect(shelves.follow(suggestion('Bo'))).resolves.toBe('needs-choice');

    expect(api.followArtist).not.toHaveBeenCalled();
    expect(shelves.suggestions?.map((s) => s.artist)).toEqual(['Bo']);
    expect(shelves.pending.size).toBe(0);
  });

  it('says not-found when the catalog has no artist of that name', async () => {
    const shelves = await loadShelves([], [suggestion('Bo')]);
    api.fetchArtistDiscography.mockResolvedValue(discography(null));

    await expect(shelves.follow(suggestion('Bo'))).resolves.toBe('not-found');
    expect(api.followArtist).not.toHaveBeenCalled();
  });

  it('a refresh asked before a follow does not undo it', async () => {
    const shelves = await loadShelves([followed(1, 'Zed')], [suggestion('Abel', '7')]);
    let answer: (value: unknown) => void = () => {};
    api.fetchFollowing.mockReturnValue(new Promise((resolve) => (answer = resolve)));
    api.fetchArtistSuggestions.mockResolvedValue([suggestion('Abel', '7')]);
    const refresh = shelves.load();
    api.followArtist.mockResolvedValue(followed(7, 'Abel'));

    await shelves.follow(suggestion('Abel', '7'));
    answer({
      follows: [followed(1, 'Zed')],
      recent: [],
      monitorEnabled: true,
      downloadsEnabled: true
    });
    await refresh;

    expect(shelves.follows?.map((f) => f.name)).toEqual(['Abel', 'Zed']);
    expect(shelves.suggestions).toEqual([]);
  });

  it('keeps the last shelves when a refresh fails', async () => {
    const shelves = await loadShelves([followed(1, 'Zed')], [suggestion('Bo')]);
    api.fetchFollowing.mockRejectedValue(new Error('offline'));
    api.fetchArtistSuggestions.mockRejectedValue(new Error('offline'));

    await shelves.load();

    expect(shelves.follows?.map((f) => f.name)).toEqual(['Zed']);
    expect(shelves.suggestions?.map((s) => s.artist)).toEqual(['Bo']);
  });
});
