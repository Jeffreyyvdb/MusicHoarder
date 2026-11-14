/**
 * The hardware skin: an easter egg that dresses the whole app as a SPEC-01 pocket instrument
 * (molded panels, keycaps, LCDs, a mono typeface) and turns Now Playing into a device.
 *
 * Hidden until found: the Konami code on a keyboard, or one of the secret phrases typed into
 * search ({@link isSecretPhrase}) on a phone. Finding it unlocks it for good on this browser and
 * switches it on; from then on the account menu carries a Hardware switch under Appearance.
 *
 * Per browser, like Appearance itself: `mh:hardware` is absent (locked), `on`, or `off` (found,
 * switched off). app.html reads the same key before the first frame, so a reload never flashes
 * the plain skin first; keep {@link STORAGE_KEY} and the theme colours in step with it.
 *
 * Off is exactly the app as it was: the skin is CSS under `:root[data-mh-skin='hardware']`, and the
 * page-wide haptics and sounds check that attribute before they do anything
 * (behaviors/active.ts).
 */
export const STORAGE_KEY = 'mh:hardware';
/** behaviors/sound.ts keeps its own switch under this key; read here so it need not load. */
const SOUND_KEY = 'spec01:sound';

/**
 * The page-wide behaviours (haptics, sounds, keyboard-only focus rings, the block caret) load only
 * once the skin is on: nobody who never finds the egg downloads them.
 */
function loadBehaviors(): void {
  void import('./behaviors/install.ts').then((m) => m.installHardware());
}

/** Hex mirrors of the skin's `--background` (app-skin.css), for the theme-color meta. */
export const HARDWARE_THEME_COLOR = { light: '#efefeb', dark: '#1c1c1a' } as const;

/** ↑ ↑ ↓ ↓ ← → ← → B A, compared case-insensitively. */
const KONAMI = [
  'arrowup',
  'arrowup',
  'arrowdown',
  'arrowdown',
  'arrowleft',
  'arrowright',
  'arrowleft',
  'arrowright',
  'b',
  'a'
] as const;

/**
 * Feed it every `KeyboardEvent.key`; it answers true on the key that completes the Konami code.
 * A wrong key restarts the sequence, counting itself as a fresh start when it is the first key
 * (so ↑ ↑ ↑ ↓ ↓ … still matches, as it does on the console).
 */
export function createKonamiMatcher(): (key: string) => boolean {
  let at = 0;
  return (key) => {
    const k = key.toLowerCase();
    if (k === KONAMI[at]) {
      at += 1;
    } else if (at === 2 && k === 'arrowup') {
      // ↑ ↑ ↑: the last two still count as the start.
      at = 2;
    } else {
      at = k === KONAMI[0] ? 1 : 0;
    }
    if (at === KONAMI.length) {
      at = 0;
      return true;
    }
    return false;
  };
}

/**
 * The phrases that reveal the switch from search, where a phone has no arrow keys: the kit's name
 * in its usual spellings, and the code itself spelled out. Whole-query matches only, folded like
 * search folds text, so no ordinary search ever trips it.
 */
export function isSecretPhrase(query: string): boolean {
  const q = query
    .normalize('NFKD')
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, ' ')
    .trim();
  return (
    q === 'spec 01' ||
    q === 'spec01' ||
    q === 'spec 1' ||
    q === 'hardware mode' ||
    q === 'up up down down left right left right b a' ||
    q === 'uuddlrlrba'
  );
}

type Stored = 'on' | 'off' | null;

function readSounds(): boolean {
  try {
    return localStorage.getItem(SOUND_KEY) === 'on';
  } catch {
    return false;
  }
}

function read(): Stored {
  try {
    const v = localStorage.getItem(STORAGE_KEY);
    return v === 'on' || v === 'off' ? v : null;
  } catch {
    return null;
  }
}

function write(v: 'on' | 'off'): void {
  try {
    localStorage.setItem(STORAGE_KEY, v);
  } catch {
    // Storage blocked (a private window): the skin lasts this page only.
  }
}

let unlocked = $state(false);
let enabled = $state(false);
let sounds = $state(false);
let initialised = false;

function apply(): void {
  if (typeof document === 'undefined') return;
  const root = document.documentElement;
  if (enabled) {
    root.dataset.mhSkin = 'hardware';
    loadBehaviors();
  } else {
    delete root.dataset.mhSkin;
  }
}

export const hardwareMode = {
  /** Found on this browser: the account menu shows the switch. */
  get unlocked(): boolean {
    return unlocked;
  },
  get enabled(): boolean {
    return enabled;
  },
  /** Key sounds (synthesized clicks). Off unless turned on; see behaviors/sound.ts for why. */
  get sounds(): boolean {
    return sounds;
  },
  /** Read the stored state and apply it. Once per page; the root layout calls it. */
  init(): void {
    if (initialised || typeof window === 'undefined') return;
    initialised = true;
    const stored = read();
    unlocked = stored !== null;
    enabled = stored === 'on';
    sounds = readSounds();
    apply();
  },
  /** The egg was found. Returns true the first time, so the caller can make a fuss of it. */
  unlock(): boolean {
    const first = !unlocked;
    unlocked = true;
    enabled = true;
    write('on');
    apply();
    return first;
  },
  setEnabled(on: boolean): void {
    unlocked = true;
    enabled = on;
    write(on ? 'on' : 'off');
    apply();
  },
  toggle(): boolean {
    this.setEnabled(!enabled);
    return enabled;
  },
  setSounds(on: boolean): void {
    sounds = on;
    // Through the module, which stores it too and keeps its own copy in step.
    void import('./behaviors/sound.ts').then((m) => m.setSoundEnabled(on));
  }
};
