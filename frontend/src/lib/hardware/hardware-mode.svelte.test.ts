import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createKonamiMatcher, isSecretPhrase } from './hardware-mode.svelte.ts';

const { install } = vi.hoisted(() => ({ install: vi.fn() }));
vi.mock('./behaviors/install.ts', () => ({ installHardware: install }));

const CODE = [
  'ArrowUp',
  'ArrowUp',
  'ArrowDown',
  'ArrowDown',
  'ArrowLeft',
  'ArrowRight',
  'ArrowLeft',
  'ArrowRight',
  'b',
  'a'
];

function feed(keys: string[]): boolean[] {
  const match = createKonamiMatcher();
  return keys.map(match);
}

describe('createKonamiMatcher', () => {
  it('fires on the last key of the code, once', () => {
    const hits = feed(CODE);
    expect(hits.slice(0, -1).every((h) => !h)).toBe(true);
    expect(hits.at(-1)).toBe(true);
  });

  it('takes B and A in either case (Caps Lock, Shift)', () => {
    expect(feed([...CODE.slice(0, 8), 'B', 'A']).at(-1)).toBe(true);
  });

  it('restarts on a wrong key', () => {
    expect(feed(['ArrowUp', 'ArrowUp', 'x', ...CODE.slice(2)]).some(Boolean)).toBe(false);
  });

  it('still matches after a stray third up, as on the console', () => {
    expect(feed(['ArrowUp', ...CODE]).at(-1)).toBe(true);
  });

  it('matches again after a completed code', () => {
    expect(feed([...CODE, ...CODE]).filter(Boolean)).toHaveLength(2);
  });

  it('counts a wrong key that is the first of the code as a fresh start', () => {
    expect(feed(['ArrowUp', 'ArrowDown', ...CODE]).at(-1)).toBe(true);
  });
});

describe('isSecretPhrase', () => {
  it.each(['spec-01', 'SPEC-01', 'spec01', ' Spec 01 ', 'hardware mode', 'uuddlrlrba'])(
    'opens on %j',
    (q) => expect(isSecretPhrase(q)).toBe(true)
  );

  it.each(['spec', 'hardware', 'spec-01 remix', 'Specs', ''])('stays shut on %j', (q) =>
    expect(isSecretPhrase(q)).toBe(false)
  );
});

function memoryStorage(): Storage {
  const data = new Map<string, string>();
  return {
    get length() {
      return data.size;
    },
    clear: () => data.clear(),
    getItem: (k) => data.get(k) ?? null,
    key: (i) => [...data.keys()][i] ?? null,
    removeItem: (k) => void data.delete(k),
    setItem: (k, v) => void data.set(k, String(v))
  };
}

type FakeLink = { rel: string; href: string; attrs: Record<string, string> };

/** Just enough of a document for the skin's stylesheet link: head, createElement, querySelector. */
function fakeDocument(root: { dataset: Record<string, string | undefined> }) {
  const links: FakeLink[] = [];
  return {
    links,
    documentElement: root,
    head: { appendChild: (link: FakeLink) => void links.push(link) },
    createElement: (): FakeLink & { setAttribute(k: string, v: string): void } => {
      const link = {
        rel: '',
        href: '',
        attrs: {} as Record<string, string>,
        setAttribute: (k: string, v: string) => void (link.attrs[k] = v)
      };
      return link;
    },
    querySelector: (selector: string) =>
      selector === 'link[data-mh-skin-css]'
        ? (links.find((l) => 'data-mh-skin-css' in l.attrs) ?? null)
        : null
  };
}

describe('hardwareMode', () => {
  let root: { dataset: Record<string, string | undefined> };
  let doc: ReturnType<typeof fakeDocument>;

  beforeEach(() => {
    vi.resetModules();
    install.mockClear();
    root = { dataset: {} };
    doc = fakeDocument(root);
    vi.stubGlobal('window', {});
    vi.stubGlobal('document', doc);
    vi.stubGlobal('localStorage', memoryStorage());
  });
  afterEach(() => vi.unstubAllGlobals());

  async function load() {
    const { hardwareMode } = await import('./hardware-mode.svelte.ts');
    hardwareMode.init();
    return hardwareMode;
  }

  it('starts locked and off, touching nothing', async () => {
    const hw = await load();
    expect(hw.unlocked).toBe(false);
    expect(hw.enabled).toBe(false);
    expect(root.dataset.mhSkin).toBeUndefined();
    expect(install).not.toHaveBeenCalled();
    // Nobody who never finds the egg downloads its stylesheet.
    expect(doc.links).toHaveLength(0);
  });

  it('asks for the skin stylesheet when switched on, once', async () => {
    const hw = await load();
    hw.unlock();
    expect(doc.links).toHaveLength(1);
    expect(doc.links[0]).toMatchObject({ rel: 'stylesheet' });
    hw.setEnabled(false);
    hw.setEnabled(true);
    expect(doc.links).toHaveLength(1);
  });

  it('reuses the stylesheet app.html already asked for on a reload', async () => {
    localStorage.setItem('mh:hardware', 'on');
    doc.links.push({ rel: 'stylesheet', href: '/x.css', attrs: { 'data-mh-skin-css': '' } });
    await load();
    expect(doc.links).toHaveLength(1);
  });

  it('unlocking switches the skin on and is remembered', async () => {
    const hw = await load();
    expect(hw.unlock()).toBe(true);
    expect(hw.enabled).toBe(true);
    expect(root.dataset.mhSkin).toBe('hardware');
    await vi.waitFor(() => expect(install).toHaveBeenCalled());
    expect(localStorage.getItem('mh:hardware')).toBe('on');
    // A second find is not news.
    expect(hw.unlock()).toBe(false);
  });

  it('switching off keeps it found, and takes the attribute away', async () => {
    const hw = await load();
    hw.unlock();
    hw.setEnabled(false);
    expect(hw.unlocked).toBe(true);
    expect(root.dataset.mhSkin).toBeUndefined();
    expect(localStorage.getItem('mh:hardware')).toBe('off');
  });

  it('restores a stored on at startup', async () => {
    localStorage.setItem('mh:hardware', 'on');
    const hw = await load();
    expect(hw.unlocked).toBe(true);
    expect(hw.enabled).toBe(true);
    expect(root.dataset.mhSkin).toBe('hardware');
  });

  it('restores a stored off as found but plain', async () => {
    localStorage.setItem('mh:hardware', 'off');
    const hw = await load();
    expect(hw.unlocked).toBe(true);
    expect(hw.enabled).toBe(false);
    expect(root.dataset.mhSkin).toBeUndefined();
  });

  it('ignores a value it did not write', async () => {
    localStorage.setItem('mh:hardware', 'yes please');
    const hw = await load();
    expect(hw.unlocked).toBe(false);
  });

  it('key sounds stay off until asked for', async () => {
    const hw = await load();
    expect(hw.sounds).toBe(false);
    hw.setSounds(true);
    expect(hw.sounds).toBe(true);
    await vi.waitFor(() => expect(localStorage.getItem('spec01:sound')).toBe('on'));
  });

  it('restores key sounds someone turned on', async () => {
    localStorage.setItem('spec01:sound', 'on');
    const hw = await load();
    expect(hw.sounds).toBe(true);
  });

  it('claims the skin temporarily without unlocking or persisting, and releases cleanly', async () => {
    const mod = await import('./hardware-mode.svelte.ts');
    mod.hardwareMode.init();
    expect(mod.hardwareMode.active).toBe(false);
    expect(mod.skinLook('plain', 'hw')).toBe('plain');

    const releaseA = mod.claimHardwareSkin();
    const releaseB = mod.claimHardwareSkin();
    expect(mod.hardwareMode.active).toBe(true);
    expect(mod.hardwareMode.enabled).toBe(false);
    expect(mod.hardwareMode.unlocked).toBe(false);
    expect(mod.shareSkin()).toBe('default');
    expect(mod.skinLook('plain', 'hw')).toBe('hw');
    expect(root.dataset.mhSkin).toBe('hardware');
    expect(localStorage.getItem('mh:hardware')).toBeNull();
    expect(doc.links).toHaveLength(1);

    // Releasing one overlapping claim (or calling release twice) keeps the skin active.
    releaseA();
    releaseA();
    expect(mod.hardwareMode.active).toBe(true);
    expect(root.dataset.mhSkin).toBe('hardware');

    releaseB();
    expect(mod.hardwareMode.active).toBe(false);
    expect(mod.skinLook('plain', 'hw')).toBe('plain');
    expect(root.dataset.mhSkin).toBeUndefined();
  });

  it('keeps the skin active after releasing a claim when the user enabled it themselves', async () => {
    localStorage.setItem('mh:hardware', 'on');
    const mod = await import('./hardware-mode.svelte.ts');
    mod.hardwareMode.init();
    expect(mod.shareSkin()).toBe('hardware');
    const release = mod.claimHardwareSkin();
    release();
    expect(mod.hardwareMode.active).toBe(true);
    expect(root.dataset.mhSkin).toBe('hardware');
  });
});

describe('page-wide haptics', () => {
  let root: { dataset: Record<string, string | undefined> };
  const vibrate = vi.fn();

  beforeEach(() => {
    vi.resetModules();
    vibrate.mockClear();
    root = { dataset: {} };
    vi.stubGlobal('document', { documentElement: root });
    vi.stubGlobal('navigator', { vibrate });
    vi.stubGlobal('performance', { now: () => Date.now() + Math.random() * 1e6 });
    vi.stubGlobal('localStorage', memoryStorage());
  });
  afterEach(() => vi.unstubAllGlobals());

  it('do nothing while the skin is off, even once installed', async () => {
    const { haptic } = await import('./behaviors/haptics.ts');
    haptic('light');
    expect(vibrate).not.toHaveBeenCalled();
  });

  it('buzz while it is on', async () => {
    root.dataset.mhSkin = 'hardware';
    const { haptic } = await import('./behaviors/haptics.ts');
    haptic('light');
    expect(vibrate).toHaveBeenCalledOnce();
  });
});
