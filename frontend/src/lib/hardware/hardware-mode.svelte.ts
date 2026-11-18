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
import { isNativeShell } from '#lib/native/shell.ts';
import { ensureSkinStylesheet } from './skin-stylesheet.ts';

export const STORAGE_KEY = 'mh:hardware';
/** behaviors/sound.ts keeps its own switch under this key; read here so it need not load. */
const SOUND_KEY = 'spec01:sound';

/**
 * The page-wide behaviours (haptics, sounds, keyboard-only focus rings, the block caret) load only
 * once the skin is on: nobody who never finds the egg downloads them.
 */
function loadBehaviors(): void {
  void import('./behaviors/install.ts')
    .then((m) => m.installHardware())
    .catch(() => {
      // A failed chunk (offline, a stale deploy): the skin still shows, just without haptics.
    });
}

/** Hex mirrors of the skin's `--background` (app-skin.css), for the theme-color meta. */
export const HARDWARE_THEME_COLOR = { light: '#f3f2ee', dark: '#22221f' } as const;

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
    // Usually there already (app.html adds it on a reload); without it the attribute styles nothing,
    // so the page stays plain until it lands rather than half-dressed.
    ensureSkinStylesheet();
    root.dataset.mhSkin = 'hardware';
    loadBehaviors();
  } else {
    delete root.dataset.mhSkin;
  }
}

/**
 * A look value that differs under the skin: a class, an attribute, a size. This and
 * {@link mediaAppearance} are all a component outside `#lib/hardware` may ask the skin (ESLint
 * keeps `hardwareMode` itself in here and in the settings that switch it). A skin changes how the
 * app looks, never what it offers, so the answer is a string or a number and not a flag to branch
 * on. Reactive: read it in markup or a `$derived`.
 */
export function skinLook<T extends string | number | undefined>(plain: T, skinned: T): T {
  return enabled ? skinned : plain;
}

/**
 * A housing while the skin is on: a frame the skin draws *around* content a component still
 * renders itself (VideoWatchTab's `housing`: the MH-2 around the picture). The component hands
 * the frame its content as a snippet, so a housing can dress it and cannot replace it.
 */
export function skinHousing<Frame>(frame: Frame): Frame | undefined {
  return enabled ? frame : undefined;
}

/**
 * The class that gives a surface Now Playing's media appearance (forced dark), for the sheets and
 * menus opened from it: `'dark'`, or nothing under the skin, where Now Playing sits on the skin's
 * desk in the app's own theme (SongDetailHost). Reactive: read it in markup.
 */
export function mediaAppearance(): 'dark' | undefined {
  return skinLook('dark', undefined);
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
  /**
   * Whether key sounds are offered here: not in the app shell, where they would let WebKit take
   * over the native player's audio session (behaviors/sound.ts, `soundsAvailable`).
   */
  get soundsAvailable(): boolean {
    return !isNativeShell();
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
    void import('./behaviors/sound.ts')
      .then((m) => m.setSoundEnabled(on))
      .catch(() => {});
  }
};
