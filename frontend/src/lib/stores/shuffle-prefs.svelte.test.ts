import { beforeEach, describe, expect, it, vi } from 'vitest';

function fakeStorage() {
  const items = new Map<string, string>();
  return {
    items,
    getItem: (key: string) => items.get(key) ?? null,
    setItem: (key: string, value: string) => void items.set(key, value)
  };
}

let storage: ReturnType<typeof fakeStorage>;

beforeEach(() => {
  vi.resetModules();
  storage = fakeStorage();
  vi.stubGlobal('window', { localStorage: storage });
});

const load = async () => (await import('./shuffle-prefs.svelte')).shufflePrefs;

describe('shufflePrefs', () => {
  it('keeps an account’s settings across a restart', async () => {
    const before = await load();
    before.init('user-1');
    before.set('library', true);
    before.set('album:a', true);

    vi.resetModules();
    const after = await load();
    after.init('user-1');
    expect(after.isOn('library')).toBe(true);
    expect(after.isOn('album:a')).toBe(true);
    expect(after.isOn('album:b')).toBe(false);
  });

  it('never hands one account’s settings to another', async () => {
    const prefs = await load();
    prefs.init('user-1');
    prefs.set('library', true);

    vi.resetModules();
    const other = await load();
    other.init('user-2');
    expect(other.isOn('library')).toBe(false);
  });

  it('keeps what another tab of the account turned on', async () => {
    const tab1 = await load();
    tab1.init('user-1');
    tab1.isOn('library');

    // Tab 2 writes while tab 1 still holds what it read.
    storage.setItem('mh:shuffle:user-1', JSON.stringify(['playlist:5']));
    tab1.set('album:a', true);
    expect(JSON.parse(storage.items.get('mh:shuffle:user-1')!)).toEqual(['playlist:5', 'album:a']);
    expect(tab1.isOn('playlist:5')).toBe(true);
  });

  it('holds a setting in memory when storage refuses it', async () => {
    vi.stubGlobal('window', {
      localStorage: {
        getItem: () => null,
        setItem: () => {
          throw new Error('QuotaExceededError');
        }
      }
    });
    const prefs = await load();
    prefs.init('user-1');
    prefs.set('playlist:3', true);
    expect(prefs.isOn('playlist:3')).toBe(true);
  });
});
