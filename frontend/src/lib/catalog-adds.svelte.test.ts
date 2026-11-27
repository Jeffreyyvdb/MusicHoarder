import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { CatalogTrack } from '#lib/api-client.ts';

const api = vi.hoisted(() => ({ importTrack: vi.fn() }));
vi.mock('#lib/api-client.ts', () => ({ importTrack: api.importTrack }));
const toasts = vi.hoisted(() => ({ plain: vi.fn(), success: vi.fn(), error: vi.fn() }));
vi.mock('svelte-sonner', () => ({
  toast: Object.assign(toasts.plain, { success: toasts.success, error: toasts.error })
}));

let runs = 0;
const trackWith = (wishlistStatus: string | null = null): CatalogTrack => ({
  deezerId: `t-${(runs += 1)}`,
  title: 'Song',
  artist: 'Artist',
  album: 'Album',
  durationMs: 200_000,
  coverUrl: null,
  explicit: false,
  hasPreview: true,
  wishlistStatus
});

let adds: typeof import('#lib/catalog-adds.svelte.ts').catalogAdds;

beforeEach(async () => {
  vi.resetModules();
  api.importTrack.mockReset();
  toasts.plain.mockReset();
  toasts.success.mockReset();
  toasts.error.mockReset();
  ({ catalogAdds: adds } = await import('#lib/catalog-adds.svelte.ts'));
});

describe('adding a catalog track', () => {
  it('queues it once and then says so', async () => {
    api.importTrack.mockResolvedValue({
      wishlistItemId: 1,
      jobStarted: true,
      jobId: null,
      status: 'Pending'
    });
    const track = trackWith();
    expect(adds.canAdd(track)).toBe(true);

    await adds.add(track);
    await adds.add(track);

    expect(api.importTrack).toHaveBeenCalledTimes(1);
    expect(api.importTrack).toHaveBeenCalledWith(
      expect.objectContaining({ deezerTrackId: track.deezerId })
    );
    expect(adds.labelOf(track)).toBe('Queued');
    expect(toasts.success).toHaveBeenCalledOnce();
  });

  it('shows what the server already had when the recording was on the wishlist', async () => {
    api.importTrack.mockResolvedValue({
      wishlistItemId: 1,
      jobStarted: false,
      jobId: null,
      status: 'Downloaded'
    });
    const track = trackWith();

    await adds.add(track);

    expect(adds.labelOf(track)).toBe('Downloaded');
    expect(toasts.success).not.toHaveBeenCalled();
    expect(toasts.plain).toHaveBeenCalledOnce();
  });

  it('reads as queued against an older server that sends no status', async () => {
    api.importTrack.mockResolvedValue({ wishlistItemId: 1, jobStarted: true, jobId: null });
    const track = trackWith();

    await adds.add(track);

    expect(adds.labelOf(track)).toBe('Queued');
  });

  it('stays addable after a failed request, and reports it', async () => {
    api.importTrack.mockRejectedValue(new Error('Downloads are disabled.'));
    const track = trackWith();

    await adds.add(track);

    expect(adds.canAdd(track)).toBe(true);
    expect(toasts.error).toHaveBeenCalledWith('Downloads are disabled.');
  });

  it('does not add a track already on the wishlist, but does retry one that failed', async () => {
    api.importTrack.mockResolvedValue({
      wishlistItemId: 1,
      jobStarted: true,
      jobId: null,
      status: 'Pending'
    });

    await adds.add(trackWith('Downloading'));
    expect(api.importTrack).not.toHaveBeenCalled();

    await adds.add(trackWith('NotFound'));
    expect(api.importTrack).toHaveBeenCalledOnce();
  });

  it('finds the last search’s tracks by id for the preview bar', () => {
    const track = trackWith();
    adds.remember([track]);
    expect(adds.track(track.deezerId)).toBe(track);
    expect(adds.track('unknown')).toBeUndefined();
  });
});
