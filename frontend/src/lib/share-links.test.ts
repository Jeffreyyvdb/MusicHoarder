import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { SongShareView } from './api-client';

// The module under test talks to the API client and the toaster; both are stubbed so the tests can
// count what reaches the server.
const api = vi.hoisted(() => ({
  createSongShare: vi.fn(),
  listSongShares: vi.fn(),
  shareUrl: (token: string) => `https://mh.test/share/${token}`
}));
vi.mock('$lib/api-client', () => api);
vi.mock('svelte-sonner', () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn(), loading: vi.fn(() => 'pending') }
}));

const { findShareLink, pickExistingShare, resetShareLinkCache, shareLink } =
  await import('./share-links');
const { toast } = await import('svelte-sonner');

function view(over: Partial<SongShareView>): SongShareView {
  return {
    id: 1,
    token: 'tok',
    scope: 'Song',
    songId: 1,
    createdAtUtc: '2026-09-01T00:00:00Z',
    title: 'Nightswim',
    artist: 'Aurora Vale',
    ...over
  };
}

describe('pickExistingShare', () => {
  it('matches the scope, ignoring links for the other scope', () => {
    const shares = [view({ id: 1, songId: 5, scope: 'Album' }), view({ id: 2, songId: 5 })];
    expect(pickExistingShare(shares, [5], 'song')?.id).toBe(2);
    expect(pickExistingShare(shares, [5], 'album')?.id).toBe(1);
  });

  it('finds an album link created from any of its tracks, preferring earlier ids', () => {
    const shares = [
      view({ id: 1, songId: 9, scope: 'Album' }),
      view({ id: 2, songId: 7, scope: 'Album' })
    ];
    expect(pickExistingShare(shares, [3, 7, 9], 'album')?.id).toBe(2);
    expect(pickExistingShare(shares, [3, 4], 'album')).toBeNull();
  });
});

describe('findShareLink', () => {
  beforeEach(() => {
    resetShareLinkCache();
    api.listSongShares.mockReset();
    api.createSongShare.mockReset();
  });

  it('never creates a link — opening a menu must not publish one', async () => {
    api.listSongShares.mockResolvedValue([]);
    expect(await findShareLink([48], 'song')).toBeNull();
    expect(api.createSongShare).not.toHaveBeenCalled();
  });

  it('hands back the existing link as a URL and a title', async () => {
    api.listSongShares.mockResolvedValue([view({ songId: 48, token: 'abc' })]);
    expect(await findShareLink([48], 'song')).toEqual({
      url: 'https://mh.test/share/abc',
      title: 'Nightswim — Aurora Vale'
    });
  });

  it('asks the server once for a run of menu opens', async () => {
    api.listSongShares.mockResolvedValue([]);
    await findShareLink([1], 'song');
    await findShareLink([2], 'song');
    await findShareLink([3], 'album');
    expect(api.listSongShares).toHaveBeenCalledTimes(1);
  });

  it('treats a failed lookup as no link rather than an error', async () => {
    api.listSongShares.mockRejectedValue(new Error('403'));
    expect(await findShareLink([1], 'song')).toBeNull();
  });
});

describe('shareLink', () => {
  const share = vi.fn();
  const writeText = vi.fn();

  beforeEach(() => {
    resetShareLinkCache();
    api.createSongShare.mockReset();
    api.listSongShares.mockReset();
    share.mockReset().mockResolvedValue(undefined);
    writeText.mockReset().mockResolvedValue(undefined);
    vi.mocked(toast.loading).mockClear();
    vi.mocked(toast.success).mockClear();
    vi.mocked(toast.info).mockClear();
    vi.mocked(toast.error).mockClear();
    vi.stubGlobal('navigator', { share, canShare: () => true, clipboard: { writeText } });
  });
  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it('opens the share sheet straight from the tap when the link already exists', () => {
    shareLink({ known: { url: 'u', title: 't' }, songId: 1, scope: 'song' });
    // Synchronously: nothing may be awaited before navigator.share, or iOS refuses the sheet.
    expect(share).toHaveBeenCalledWith({ url: 'u', title: 't' });
    expect(api.createSongShare).not.toHaveBeenCalled();
    // Nothing to wait for, so nothing to announce.
    expect(toast.loading).not.toHaveBeenCalled();
  });

  it('says it is working the moment Share… is picked, before the link exists', () => {
    api.createSongShare.mockReturnValue(new Promise(() => {}));
    shareLink({ known: null, songId: 48, scope: 'album' });
    // The menu has closed by now; without this the round trip passes in silence.
    expect(toast.loading).toHaveBeenCalledTimes(1);
    expect(vi.mocked(toast.loading).mock.calls[0][0]).toBe('Creating share link…');
    expect(toast.success).not.toHaveBeenCalled();
    expect(toast.info).not.toHaveBeenCalled();
  });

  it('turns the pending toast into the copied one when the clipboard takes the link', async () => {
    const write = vi.fn().mockResolvedValue(undefined);
    vi.stubGlobal(
      'ClipboardItem',
      class {
        constructor(readonly items: Record<string, Promise<Blob>>) {}
      }
    );
    vi.stubGlobal('navigator', { share, canShare: () => true, clipboard: { writeText, write } });
    api.createSongShare.mockResolvedValue(view({ songId: 48, token: 'new', scope: 'Album' }));
    shareLink({ known: null, songId: 48, scope: 'album' });
    // The write is started inside the tap, before the link exists.
    expect(write).toHaveBeenCalledTimes(1);
    await vi.waitFor(() => expect(toast.success).toHaveBeenCalled());
    const [title, opts] = vi.mocked(toast.success).mock.calls[0];
    expect(title).toBe('Share link copied');
    // Same toast, not a second one, and back on the Toaster's default lifetime.
    expect(opts?.id).toBe('pending');
    expect(opts).toHaveProperty('duration', undefined);
    expect(opts?.description).toBe('Anyone with the link can play this album and see its lyrics.');
    expect((opts?.action as { label: string }).label).toBe('Share…');
  });

  it('creates the link only when Share… is picked and nothing exists yet', async () => {
    api.createSongShare.mockResolvedValue(view({ songId: 48, token: 'new' }));
    shareLink({ known: null, songId: 48, scope: 'song' });
    expect(api.createSongShare).toHaveBeenCalledWith(48, 'song');
    // No promise-valued ClipboardItem here (as in an older browser): the toast shows the link, and
    // the share sheet is one activated tap away on it.
    await vi.waitFor(() => expect(toast.info).toHaveBeenCalled());
    const [title, opts] = vi.mocked(toast.info).mock.calls[0];
    expect(title).toBe('Share link created');
    expect(opts?.id).toBe('pending');
    expect(opts?.description).toBe('https://mh.test/share/new');
    expect((opts?.action as { label: string }).label).toBe('Share…');
    expect(share).not.toHaveBeenCalled();
  });

  it('says so when the link cannot be created', async () => {
    api.createSongShare.mockRejectedValue(new Error('Song with id 48 not found.'));
    shareLink({ known: null, songId: 48, scope: 'song' });
    await vi.waitFor(() =>
      expect(toast.error).toHaveBeenCalledWith('Song with id 48 not found.', {
        id: 'pending',
        duration: undefined
      })
    );
  });
});
