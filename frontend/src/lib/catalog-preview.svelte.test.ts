import { catalogPreview } from '#lib/catalog-preview.svelte.ts';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

/**
 * The preview controller against a fake audio element and a fake main player: what a person can
 * observe (what is playing, what the main player was told, what is offered) and not how the
 * controller keeps it.
 */

const main = vi.hoisted(() => ({
  playing: vi.fn(() => false),
  loading: vi.fn(() => null),
  pause: vi.fn()
}));
vi.mock('#lib/stores/player.svelte.ts', () => ({ localPlayback: main }));

const shell = vi.hoisted(() => ({ native: false }));
vi.mock('#lib/native/shell.ts', () => ({ isNativeShell: () => shell.native }));

const api = vi.hoisted(() => ({ resolve: vi.fn() }));
vi.mock('#lib/api-client.ts', () => ({
  catalogPreviewUrl: (id: string) => `/clip/${id}`,
  catalogFullUrl: (id: string) => `/full/${id}`,
  resolveCatalogFullSong: api.resolve
}));

class FakeAudio extends EventTarget {
  static instances: FakeAudio[] = [];
  src = '';
  preload = '';
  paused = true;
  currentTime = 0;
  duration = NaN;
  constructor() {
    super();
    FakeAudio.instances.push(this);
  }
  play() {
    this.paused = false;
    return Promise.resolve();
  }
  pause() {
    this.paused = true;
    this.dispatchEvent(new Event('pause'));
  }
  removeAttribute() {
    this.src = '';
  }
  load() {}
  emit(type: string) {
    this.dispatchEvent(new Event(type));
  }
}

// Fresh ids per test: the controller remembers which tracks already resolved.
let runs = 0;
let one = { deezerId: '', title: 'One', artist: 'A' };
let two = { deezerId: '', title: 'Two', artist: 'B' };

let preview: typeof catalogPreview;
const el = () => FakeAudio.instances.at(-1)!;
const settle = () => new Promise((r) => setTimeout(r, 0));
/** The current clip reaches a second, as the element would report it. */
const reach = (seconds: number) => {
  el().currentTime = seconds;
  el().emit('timeupdate');
};

beforeEach(() => {
  FakeAudio.instances = [];
  shell.native = false;
  main.playing.mockReturnValue(false);
  main.pause.mockClear();
  api.resolve.mockReset();
  api.resolve.mockResolvedValue({ youTubeVideoId: 'v', videoTitle: 't', durationMs: 1000 });
  vi.stubGlobal('Audio', FakeAudio);
  runs += 1;
  one = { ...one, deezerId: `one-${runs}` };
  two = { ...two, deezerId: `two-${runs}` };
  preview = catalogPreview;
});

afterEach(() => preview.release());

describe('starting a preview', () => {
  it('plays the clip and pauses the main player first when it is playing', () => {
    main.playing.mockReturnValue(true);
    preview.toggle(one);
    expect(main.pause).toHaveBeenCalledOnce();
    expect(el().src).toBe(`/clip/${one.deezerId}`);
    el().emit('playing');
    expect(preview.state).toBe('playing');
    expect(preview.track).toEqual(one);
  });

  it('leaves an idle main player alone', () => {
    preview.toggle(one);
    expect(main.pause).not.toHaveBeenCalled();
  });

  it('stops the first preview in favour of the second', () => {
    preview.toggle(one);
    el().emit('playing');
    preview.toggle(two);
    expect(el().src).toBe(`/clip/${two.deezerId}`);
    expect(preview.track).toEqual(two);
    expect(preview.state).toBe('loading');
  });

  it('pauses and resumes the same row on a second tap', () => {
    preview.toggle(one);
    el().emit('playing');
    preview.toggle(one);
    expect(preview.state).toBe('paused');
    preview.toggle(one);
    el().emit('playing');
    expect(preview.state).toBe('playing');
  });

  it('is not offered in the native app shell', () => {
    shell.native = true;
    preview.toggle(one);
    expect(preview.state).toBe('idle');
    expect(FakeAudio.instances).toHaveLength(0);
  });
});

describe('the main player', () => {
  it('ends the preview when the main player starts a song', () => {
    preview.toggle(one);
    el().emit('playing');
    preview.yieldToMainPlayer();
    expect(preview.state).toBe('idle');
    expect(preview.track).toBeNull();
  });
});

describe('the full song', () => {
  it('is not looked up for a clip skipped within its first seconds', () => {
    preview.toggle(one);
    reach(1);
    preview.toggle(two);
    reach(1.5);
    expect(api.resolve).not.toHaveBeenCalled();
  });

  it('becomes available once the lookup succeeds, then plays from the start', async () => {
    preview.toggle(one);
    expect(preview.full.status).toBe('waiting');
    reach(2);
    expect(api.resolve).toHaveBeenCalledTimes(1);
    expect(preview.full.status).toBe('resolving');
    preview.playFull();
    expect(el().src).toBe(`/clip/${one.deezerId}`);
    await settle();
    expect(preview.full.status).toBe('ready');
    preview.playFull();
    expect(el().src).toBe(`/full/${one.deezerId}`);
    expect(preview.source).toBe('full');
  });

  it('explains a failed lookup and never plays the full song', async () => {
    api.resolve.mockRejectedValue(new Error('No full version found.'));
    preview.toggle(one);
    reach(2);
    await settle();
    expect(preview.full).toEqual({ status: 'failed', reason: 'No full version found.' });
    preview.playFull();
    expect(el().src).toBe(`/clip/${one.deezerId}`);
  });

  it('looks up again after coming back to a track whose lookup failed', async () => {
    api.resolve.mockRejectedValueOnce(new Error('nope'));
    preview.toggle(one);
    reach(2);
    await settle();
    preview.toggle(two);
    reach(1);
    preview.toggle(one);
    reach(2);
    expect(api.resolve).toHaveBeenCalledTimes(2);
    expect(api.resolve).toHaveBeenLastCalledWith(one.deezerId);
  });

  it('keeps a found full song when an older lookup for it fails late', async () => {
    let failFirst!: () => void;
    api.resolve.mockImplementationOnce(
      () => new Promise((_, reject) => (failFirst = () => reject(new Error('late'))))
    );
    preview.toggle(one);
    reach(2);
    preview.toggle(two);
    preview.toggle(one);
    reach(2);
    await settle();
    expect(preview.full.status).toBe('ready');
    failFirst();
    await settle();
    expect(preview.full.status).toBe('ready');
  });

  it('ignores a lookup that finishes after another track took over', async () => {
    let finish!: () => void;
    api.resolve.mockImplementationOnce(
      () => new Promise((resolve) => (finish = () => resolve({ youTubeVideoId: 'v' })))
    );
    api.resolve.mockRejectedValueOnce(new Error('nope'));
    preview.toggle(one);
    reach(2);
    preview.toggle(two);
    reach(2);
    finish();
    await settle();
    expect(preview.full.status).toBe('failed');
  });
});

describe('leaving the page', () => {
  it('stops and lets go of the audio', () => {
    preview.toggle(one);
    el().emit('playing');
    const audio = el();
    preview.release();
    expect(preview.state).toBe('idle');
    expect(audio.paused).toBe(true);
    expect(audio.src).toBe('');
  });
});

describe('interruptions', () => {
  it('a resume cut short by a pause is not an error', async () => {
    preview.toggle(one);
    el().emit('playing');
    preview.togglePause();
    let reject!: (err: unknown) => void;
    vi.spyOn(el(), 'play').mockImplementationOnce(() => new Promise((_, r) => (reject = r)));
    preview.togglePause();
    preview.togglePause();
    reject(new DOMException('interrupted by pause()', 'AbortError'));
    await settle();
    expect(preview.state).toBe('paused');
  });

  it('a failed play() of an earlier track does not mark the next one', async () => {
    let reject!: (err: unknown) => void;
    preview.toggle(one);
    vi.spyOn(el(), 'play').mockImplementationOnce(() => new Promise((_, r) => (reject = r)));
    preview.togglePause();
    preview.togglePause();
    preview.toggle(two);
    reject(new DOMException('not supported', 'NotSupportedError'));
    await settle();
    expect(preview.state).toBe('loading');
  });

  it("a released element firing late leaves the next page's preview alone", () => {
    preview.toggle(one);
    const old = el();
    preview.release();
    preview.toggle(two);
    el().emit('playing');
    old.emit('error');
    expect(preview.state).toBe('playing');
  });
});

describe('failures', () => {
  it('shows an error when the stream cannot load, and a tap retries it', () => {
    preview.toggle(one);
    el().emit('error');
    expect(preview.state).toBe('error');
    preview.toggle(one);
    expect(preview.state).toBe('loading');
  });

  it('keeps the progress from timeupdate', () => {
    preview.toggle(one);
    el().currentTime = 12;
    el().duration = 30;
    el().emit('timeupdate');
    expect([preview.position, preview.duration]).toEqual([12, 30]);
  });
});
