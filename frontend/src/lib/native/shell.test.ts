import { afterEach, describe, expect, it, vi } from 'vitest';
import { canChangeServer, changeServer } from './shell.ts';

afterEach(() => {
  vi.unstubAllGlobals();
});

/** The bridge Capacitor injects into a page the app shell loads, with the plugins it carries. */
function shellWith(plugins: string[]) {
  const nativePromise = vi.fn(() => Promise.resolve(undefined));
  vi.stubGlobal('window', {
    Capacitor: {
      isNativePlatform: () => true,
      getPlatform: () => 'ios',
      PluginHeaders: plugins.map((name) => ({ name })),
      nativePromise,
      addListener: vi.fn()
    }
  });
  return nativePromise;
}

describe('changing the server the app loads', () => {
  it('is not offered in a browser', () => {
    vi.stubGlobal('window', {});
    expect(canChangeServer()).toBe(false);
  });

  it('is not offered by an app built before it could ask for a server', () => {
    shellWith(['NativeAudio']);
    expect(canChangeServer()).toBe(false);
  });

  it('is offered, and forgets the server through the plugin, in an app that asks', async () => {
    const nativePromise = shellWith(['NativeAudio', 'ShellServer']);
    expect(canChangeServer()).toBe(true);
    await changeServer();
    expect(nativePromise).toHaveBeenCalledWith('ShellServer', 'clearServer', {});
  });
});
