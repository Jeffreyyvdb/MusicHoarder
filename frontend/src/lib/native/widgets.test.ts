import { afterEach, describe, expect, it, vi } from 'vitest';
import type { AlbumSummary, ApiSong } from '#lib/api-client.ts';
import { runLaunchAction, type LaunchDeps } from './widget-actions.ts';
import {
  buildShelf,
  clearWidgets,
  onLaunchAction,
  parseLaunchAction,
  publishShelf,
  widgetsAvailable,
  type ShelfItem
} from './widgets.ts';

afterEach(() => {
  vi.unstubAllGlobals();
  vi.useRealTimers();
});

/** The bridge Capacitor injects into the shell's web view, with the plugins it carries. */
function shellWith(plugins: string[]) {
  const nativePromise = vi.fn((..._args: unknown[]) => Promise.resolve<unknown>(undefined));
  const listeners: ((data: unknown) => void)[] = [];
  const visibility: (() => void)[] = [];
  vi.stubGlobal('window', {
    Capacitor: {
      isNativePlatform: () => true,
      getPlatform: () => 'ios',
      PluginHeaders: plugins.map((name) => ({ name })),
      nativePromise,
      addListener: (_plugin: string, _event: string, cb: (data: unknown) => void) =>
        listeners.push(cb)
    }
  });
  vi.stubGlobal('document', {
    visibilityState: 'visible',
    addEventListener: (_: string, cb: () => void) => visibility.push(cb),
    removeEventListener: (_: string, cb: () => void) => {
      visibility.splice(visibility.indexOf(cb), 1);
    }
  });
  return { nativePromise, nudge: () => listeners.forEach((l) => l({})), visibility };
}

const item = (id: string): ShelfItem => ({
  id,
  title: id,
  subtitle: 'artist',
  artwork: null,
  path: `/library?album=${id}`
});

describe('without the Widgets plugin', () => {
  it('does nothing and throws nothing in a browser', async () => {
    vi.stubGlobal('window', {});
    expect(widgetsAvailable()).toBe(false);
    expect(() => publishShelf([item('a')])).not.toThrow();
    await expect(clearWidgets()).resolves.toBeUndefined();
    const stop = onLaunchAction(vi.fn(), () => true);
    expect(() => stop()).not.toThrow();
  });

  it('does nothing in a build from before the widgets', async () => {
    const { nativePromise } = shellWith(['NativeAudio', 'ShellServer']);
    expect(widgetsAvailable()).toBe(false);
    publishShelf([item('old')]);
    await clearWidgets();
    onLaunchAction(vi.fn(), () => true);
    expect(nativePromise).not.toHaveBeenCalled();
  });
});

describe('publishing the shelf', () => {
  it('sends once for an equal shelf, again after a change and after a clear', async () => {
    const { nativePromise } = shellWith(['Widgets']);
    publishShelf([item('equal-1')]);
    publishShelf([item('equal-1')]);
    expect(nativePromise.mock.calls.filter((c) => c[1] === 'setShelf')).toHaveLength(1);
    publishShelf([item('equal-1'), item('equal-2')]);
    expect(nativePromise.mock.calls.filter((c) => c[1] === 'setShelf')).toHaveLength(2);
    await clearWidgets();
    expect(nativePromise).toHaveBeenCalledWith('Widgets', 'clear', {});
    publishShelf([item('equal-1'), item('equal-2')]);
    expect(nativePromise.mock.calls.filter((c) => c[1] === 'setShelf')).toHaveLength(3);
  });

  it('tries again after a refused send', async () => {
    const { nativePromise } = shellWith(['Widgets']);
    nativePromise.mockRejectedValueOnce(new Error('no'));
    publishShelf([item('retry')]);
    await Promise.resolve();
    await Promise.resolve();
    publishShelf([item('retry')]);
    expect(nativePromise.mock.calls.filter((c) => c[1] === 'setShelf')).toHaveLength(2);
  });
});

describe('building the shelf', () => {
  const song = (id: number, at: string | null) => ({ id, lastPlayedAtUtc: at }) as ApiSong;
  const album = (key: string, at: string | null, coverUrl: string | null = null) =>
    ({
      key,
      title: `T ${key}`,
      artist: 'A',
      coverUrl,
      songs: [song(key.charCodeAt(0), at)]
    }) as unknown as AlbumSummary;

  it('orders like the Overview, caps at 8 and makes covers absolute', () => {
    const albums = Array.from({ length: 12 }, (_, i) =>
      album(
        String.fromCharCode(97 + i),
        `2026-01-${String(i + 1).padStart(2, '0')}T00:00:00Z`,
        '/api/mh/songs/1/cover'
      )
    );
    albums.push(album('never', null));
    const shelf = buildShelf(albums, (s) => s.lastPlayedAtUtc, 'https://mh.example');
    expect(shelf).toHaveLength(8);
    expect(shelf.map((s) => s.id)).toEqual(['l', 'k', 'j', 'i', 'h', 'g', 'f', 'e']);
    expect(shelf[0]).toEqual({
      id: 'l',
      title: 'T l',
      subtitle: 'A',
      artwork: 'https://mh.example/api/mh/songs/1/cover?size=512',
      path: '/library?album=l'
    });
  });

  it('uses the supplied play stats, not the row stamp, and encodes the key', () => {
    const a = album('x/y z', '2020-01-01T00:00:00Z');
    const b = album('b', '2021-01-01T00:00:00Z');
    const stats = new Map([[a.songs[0].id, '2026-01-01T00:00:00Z']]);
    const shelf = buildShelf(
      [a, b],
      (s) => stats.get(s.id) ?? s.lastPlayedAtUtc,
      'https://mh.example'
    );
    expect(shelf[0].path).toBe('/library?album=x%2Fy%20z');
    expect(shelf[0].artwork).toBeNull();
  });
});

describe('parsing a launch action', () => {
  const any = () => true;

  it('takes the three kinds', () => {
    expect(parseLaunchAction({ kind: 'now-playing' }, any)).toEqual({ kind: 'now-playing' });
    expect(parseLaunchAction({ kind: 'resume', extra: 1 }, any)).toEqual({ kind: 'resume' });
    expect(parseLaunchAction({ kind: 'open', path: '/library?album=x' }, any)).toEqual({
      kind: 'open',
      path: '/library?album=x'
    });
  });

  it.each([
    null,
    undefined,
    'resume',
    42,
    [],
    {},
    { kind: 'launch-missiles' },
    { kind: 'open' },
    { kind: 'open', path: 7 },
    { kind: 'open', path: 'library' },
    { kind: 'open', path: '//evil.example/x' },
    { kind: 'open', path: '/\\evil.example' },
    { kind: 'open', path: 'https://evil.example/' },
    { kind: 'open', path: 'javascript:alert(1)' },
    { kind: 'open', path: '/a\nb' },
    { kind: 'open', path: '' }
  ])('refuses %j', (raw) => {
    expect(parseLaunchAction(raw, any)).toBeNull();
  });

  it('refuses a path this account may not open, judged on its pathname', () => {
    const allowed = (p: string) => p.startsWith('/library');
    expect(parseLaunchAction({ kind: 'open', path: '/manage?x=/library' }, allowed)).toBeNull();
    expect(parseLaunchAction({ kind: 'open', path: '/library?album=a' }, allowed)).not.toBeNull();
  });
});

describe('taking launch actions', () => {
  it('asks at once, when visible and on the nudge, and handles what comes back', async () => {
    const shell = shellWith(['Widgets']);
    shell.nativePromise.mockResolvedValue({ action: { kind: 'resume' } });
    const handle = vi.fn();
    const stop = onLaunchAction(handle, () => true);
    shell.nudge();
    shell.visibility.forEach((cb) => cb());
    await new Promise((r) => setTimeout(r));
    expect(shell.nativePromise.mock.calls.filter((c) => c[1] === 'takeLaunchAction')).toHaveLength(
      3
    );
    expect(handle).toHaveBeenCalledWith({ kind: 'resume' });
    stop();
    handle.mockClear();
    shell.nativePromise.mockClear();
    shell.nudge();
    await new Promise((r) => setTimeout(r));
    expect(handle).not.toHaveBeenCalled();
    expect(shell.nativePromise).not.toHaveBeenCalled();
    expect(shell.visibility).toHaveLength(0);
  });

  it('leaves the tap to the live subscription after an earlier one stopped', async () => {
    const shell = shellWith(['Widgets']);
    const stale = vi.fn();
    onLaunchAction(stale, () => true)();
    await new Promise((r) => setTimeout(r));
    const live = vi.fn();
    onLaunchAction(live, () => true);
    await new Promise((r) => setTimeout(r));
    let pending: { kind: string } | null = { kind: 'resume' };
    shell.nativePromise.mockImplementation(() => {
      const action = pending;
      pending = null;
      return Promise.resolve({ action });
    });
    shell.nudge();
    await new Promise((r) => setTimeout(r));
    expect(stale).not.toHaveBeenCalled();
    expect(live).toHaveBeenCalledWith({ kind: 'resume' });
  });

  it('drops a hostile or disallowed action', async () => {
    const shell = shellWith(['Widgets']);
    shell.nativePromise.mockResolvedValue({ action: { kind: 'open', path: '//evil.example' } });
    const handle = vi.fn();
    onLaunchAction(handle, () => true);
    await new Promise((r) => setTimeout(r));
    expect(handle).not.toHaveBeenCalled();
  });

  it('survives the app refusing the read', async () => {
    const shell = shellWith(['Widgets']);
    shell.nativePromise.mockRejectedValue(new Error('nope'));
    expect(() => onLaunchAction(vi.fn(), () => true)).not.toThrow();
    await new Promise((r) => setTimeout(r));
  });
});

describe('running a launch action', () => {
  function deps(over: Partial<LaunchDeps> = {}): LaunchDeps {
    return {
      loadedSongId: () => 7,
      isPlaying: () => false,
      openNowPlaying: vi.fn(),
      play: vi.fn(),
      open: vi.fn(),
      ...over
    };
  }

  it('opens Now Playing only with a song loaded', () => {
    const loaded = deps();
    runLaunchAction({ kind: 'now-playing' }, loaded);
    expect(loaded.openNowPlaying).toHaveBeenCalledWith(7);
    const none = deps({ loadedSongId: () => null });
    runLaunchAction({ kind: 'now-playing' }, none);
    expect(none.openNowPlaying).not.toHaveBeenCalled();
  });

  it('navigates for open', () => {
    const d = deps();
    runLaunchAction({ kind: 'open', path: '/library?album=a' }, d);
    expect(d.open).toHaveBeenCalledWith('/library?album=a');
  });

  it('plays what is loaded, and leaves a playing song alone', () => {
    const paused = deps();
    runLaunchAction({ kind: 'resume' }, paused);
    expect(paused.play).toHaveBeenCalledTimes(1);
    const playing = deps({ isPlaying: () => true });
    runLaunchAction({ kind: 'resume' }, playing);
    expect(playing.play).not.toHaveBeenCalled();
  });

  it('waits for a fresh page to have something to resume, then gives up after 10 s', () => {
    vi.useFakeTimers();
    let song: number | null = null;
    const d = deps({ loadedSongId: () => song });
    runLaunchAction({ kind: 'resume' }, d);
    vi.advanceTimersByTime(2000);
    expect(d.play).not.toHaveBeenCalled();
    song = 3;
    vi.advanceTimersByTime(300);
    expect(d.play).toHaveBeenCalledTimes(1);

    const late = deps({ loadedSongId: () => null });
    runLaunchAction({ kind: 'resume' }, late);
    vi.advanceTimersByTime(11_000);
    expect(late.play).not.toHaveBeenCalled();
    expect(vi.getTimerCount()).toBe(0);
  });

  it('can cancel a Play that is still waiting', () => {
    vi.useFakeTimers();
    const d = deps({ loadedSongId: () => null });
    const cancel = runLaunchAction({ kind: 'resume' }, d);
    cancel();
    expect(vi.getTimerCount()).toBe(0);
  });
});
