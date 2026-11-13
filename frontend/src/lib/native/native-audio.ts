import { nativePlatform, nativePlugin, type NativePlatform, type NativePlugin } from './shell.ts';

/**
 * The native player of the app shell, wearing the part of an audio element the player store uses.
 *
 * In a browser the store drives an `<audio>` element, and a song's end is the page's to handle:
 * load the next source, call `play()`. An iOS page in the background gets seconds for that, and
 * none once it is suspended (see "Background playback" in `player.svelte.ts`); an Android web view
 * has no foreground service to keep it alive at all. In the shell the store drives this instead,
 * which forwards to the device's own queue player (`mobile/plugin`: an AVQueuePlayer on iOS,
 * Media3 in a media session service on Android). The store still decides what plays: it loads a
 * track here and hands over the tracks that follow it. The difference is that the next track
 * starts on the device at the moment the current one ends, whether or not the page is running.
 *
 * So the device can be ahead of the store, by one track or by ten. It reports the tracks it has
 * started since the last load (its `trail`), and for each one the store has not taken yet this
 * shows the store what it already understands as a song's end: a played source jumping back to
 * its start (`cameRoundToStart`). The store advances as it always does, sets the `src` the device
 * is already on, and that load is adopted instead of sent. Each step counts its listen, moves the
 * queue and reports to the account's session exactly as if the page had been awake for it.
 *
 * This file and the two native plugins are one protocol and change together.
 */

/** The audio element as the player store uses it; `HTMLAudioElement` fits as it is. */
export interface PlayerAudio {
  src: string;
  currentTime: number;
  volume: number;
  playbackRate: number;
  defaultPlaybackRate: number;
  preservesPitch: boolean;
  loop: boolean;
  preload: string;
  readonly duration: number;
  readonly paused: boolean;
  readonly readyState: number;
  readonly error: { readonly code: number } | null;
  load(): void;
  play(): Promise<void>;
  pause(): void;
  canPlayType(type: string): string;
  setAttribute(name: string, value: string): void;
  removeAttribute(name: string): void;
  addEventListener(type: string, listener: (event: Event) => void): void;
}

/** A track as the device needs it: where it streams from, and the lock screen's words for it. */
export interface NativeTrack {
  url: string;
  title: string;
  artist: string;
  album: string;
  artwork: string | null;
}

/** A press on the lock screen, Control Center, a headset or a car display. */
export type RemoteCommand = 'play' | 'pause' | 'toggle' | 'next' | 'previous' | 'seek';

interface NativeState {
  loadId: number;
  seq: number;
  /** Which entry of `trail` is on the device now. */
  index: number;
  trail: string[];
  status: 'playing' | 'waiting' | 'paused';
  position: number;
  duration: number;
  /** The last track played out with nothing queued after it. */
  ended: boolean;
}

const MEDIA_ERR_NETWORK = 2;
const MEDIA_ERR_SRC_NOT_SUPPORTED = 4;
const HAVE_ENOUGH_DATA = 4;

/**
 * What each system's player cannot open; the server decodes these (see `#lib/audio-formats`).
 * AVFoundation has no Ogg or WebM; ExoPlayer plays every format the library holds but WMA.
 */
const UNPLAYABLE_TYPE: Record<NativePlatform, RegExp> = {
  ios: /ogg|opus|webm|x-ms-wma/i,
  android: /x-ms-wma/i
};

/** How far a reported position may sit from the one just asked for and still be that seek. */
const SEEK_TOLERANCE_S = 3;
/** How long a seek is waited for before the device's own position is believed again. */
const SEEK_WAIT_MS = 5000;
/** A backwards jump this large that nobody here asked for is the device's own seek. */
const REWIND_S = 1.5;

export class NativeAudio extends EventTarget implements PlayerAudio {
  loop = false;
  preload = '';
  preservesPitch = true;
  defaultPlaybackRate = 1;
  /** A command from the lock screen, to be carried out the way the store's own controls are. */
  onRemote: ((command: RemoteCommand, time?: number) => void) | null = null;

  private url = '';
  /** Random, so a reloaded page never mistakes the last page's news for its own. */
  private loadId = Math.floor(Math.random() * 1_000_000_000);
  private seq = 0;
  private trail: string[] = [];
  /** How many of `trail` the store has loaded. Behind `trail.length`, the device is ahead. */
  private adopted = 0;
  /** The store is being walked along the trail; the device's reports wait until it is there. */
  private catchingUp = false;
  /** Inside the dispatch that shows the store a song's end. */
  private inFinish = false;
  /** The store answered that dispatch by loading the track the device is on. */
  private followed = false;
  /** The device is on a track the store did not take; `url` has to be loaded again to play. */
  private stale = false;
  /** A load not sent yet: the start position and `play()` that follow it travel with it. */
  private pending: { position: number; autoplay: boolean } | null = null;
  private adoptedNow = false;
  private settlers: { resolve: () => void; reject: (reason: Error) => void }[] = [];
  private flushQueued = false;

  private meta: NativeTrack | null = null;
  private metaDirty = false;
  private upcoming: NativeTrack[] = [];
  private upcomingDirty = false;
  /** The list the device was last sent, and the load and track it was sent for. */
  private upcomingSent = '';
  private commands = { next: false, previous: false };
  private commandsDirty = false;

  private last: NativeState | null = null;
  private lastError: { index: number; kind: string } | null = null;
  private tailHandled = false;
  /**
   * A page that has just started has not asked the device for anything yet, and the device may
   * still be playing what the page before it asked for (the web view reloaded under it: iOS
   * reclaimed its process, or a new version of the app was fetched). Until the device has said
   * where it is, the first load waits, so it can be taken over instead of started again.
   */
  private fresh = true;
  /** How many of the device's trail came before the track this page took over at. */
  private offset = 0;
  private expect: { position: number; until: number } | null = null;
  private quietSeek = false;

  private isPaused = true;
  /** Audio is flowing, so the position moves on between reports. */
  private progressing = false;
  private position = 0;
  private positionAt = 0;
  private length = NaN;
  private ready = 0;
  private failure: { code: number } | null = null;
  private level = 1;
  private speed = 1;

  constructor(
    private readonly plugin: NativePlugin,
    private readonly platform: NativePlatform
  ) {
    super();
    plugin.listen('state', (data) => this.onState(data as NativeState));
    plugin.listen('ended', (data) => this.onEnded(data as { loadId: number; index: number }));
    plugin.listen('error', (data) =>
      this.onError(data as { loadId: number; index: number; kind: string })
    );
    plugin.listen('remote', (data) =>
      this.onRemoteEvent(data as { id: number; command: RemoteCommand; time?: number })
    );
    // A reload destroys a page's audio element, but the device's player outlives the page. The
    // store's reload snapshot loads its song in the same turn this is made in: that load takes
    // over what the device is playing where it can (see `takeOver`). With no such load, nothing
    // here knows the music, so it stops.
    const started = (data: unknown) => {
      const state = data as NativeState | null;
      this.fresh = false;
      if (this.pending) this.takeOver(Array.isArray(state?.trail) ? state : null);
      else if (Array.isArray(state?.trail) && state.trail.length > 0) this.send('stop');
    };
    void plugin.call('getState').then(started, () => started(null));
    if (typeof document !== 'undefined') {
      // Reports sent while the page slept may not have arrived: ask where the device is.
      document.addEventListener('visibilitychange', () => {
        if (document.visibilityState === 'visible') this.refresh();
      });
    }
  }

  // ── The element's surface ────────────────────────────────────────────────

  get src(): string {
    return this.url;
  }
  set src(value: string) {
    this.url = value ? absolute(value) : '';
  }

  get currentTime(): number {
    if (!this.progressing) return this.position;
    const time = this.position + ((performance.now() - this.positionAt) / 1000) * this.speed;
    return Number.isFinite(this.length) ? Math.min(time, this.length) : time;
  }
  set currentTime(time: number) {
    this.position = time;
    this.positionAt = performance.now();
    if (this.quietSeek) return; // the device is there already
    if (this.pending) {
      this.pending.position = time;
      return;
    }
    if (!this.url || this.stale) return;
    this.expect = { position: time, until: performance.now() + SEEK_WAIT_MS };
    this.send('seek', { time, seq: ++this.seq });
  }

  get duration(): number {
    return this.length;
  }
  get paused(): boolean {
    return this.isPaused;
  }
  get readyState(): number {
    return this.ready;
  }
  get error(): { readonly code: number } | null {
    return this.failure;
  }

  get volume(): number {
    return this.level;
  }
  set volume(value: number) {
    this.level = value;
    this.send('setVolume', { volume: value });
  }

  get playbackRate(): number {
    return this.speed;
  }
  set playbackRate(value: number) {
    this.position = this.currentTime;
    this.positionAt = performance.now();
    this.speed = value;
    this.send('setRate', { rate: value });
  }

  canPlayType(type: string): string {
    return UNPLAYABLE_TYPE[this.platform].test(type) ? '' : 'probably';
  }

  setAttribute(): void {}

  removeAttribute(name: string): void {
    if (name === 'src') this.url = '';
  }

  load(): void {
    this.failure = null;
    this.ready = 0;
    this.length = NaN;
    this.progressing = false;
    this.isPaused = true;
    this.position = 0;
    this.positionAt = performance.now();
    this.expect = null;
    this.metaDirty = false;
    this.tailHandled = false;

    if (!this.url) {
      // Nothing to load: the store's `stop()`.
      this.pending = null;
      this.adoptedNow = false;
      this.resetTrail('');
      this.send('stop');
      return;
    }
    if (this.inFinish && this.catchingUp && this.trail[this.adopted] === this.url) {
      // The track the device moved on to by itself: nothing to send.
      this.adopted += 1;
      this.followed = true;
      this.adoptedNow = true;
    } else {
      this.resetTrail(this.url);
      this.pending = { position: 0, autoplay: false };
      this.adoptedNow = false;
    }
    this.scheduleFlush();
  }

  play(): Promise<void> {
    const wasPaused = this.isPaused;
    this.isPaused = false;
    if (this.stale && !this.pending && this.url) {
      const position = this.position;
      this.resetTrail(this.url);
      this.pending = { position, autoplay: true };
    } else if (this.pending) {
      this.pending.autoplay = true;
    } else if (!this.adoptedNow && this.url) {
      this.send('play', { seq: ++this.seq });
    }
    if (wasPaused) this.later('play');
    return new Promise<void>((resolve, reject) => {
      this.settlers.push({ resolve, reject });
      this.scheduleFlush();
    });
  }

  pause(): void {
    if (this.inFinish && this.catchingUp) {
      // The store pausing the song it takes to have come round; the device is past it, playing.
      this.isPaused = true;
      this.later('pause');
      return;
    }
    if (this.pending) this.pending.autoplay = false;
    else if (this.url && !this.stale) this.send('pause', { seq: ++this.seq });
    if (this.isPaused) return;
    this.position = this.currentTime;
    this.positionAt = performance.now();
    this.progressing = false;
    this.isPaused = true;
    this.later('pause');
  }

  // ── What only the device needs ───────────────────────────────────────────

  /** The lock screen's words for the track about to be loaded (or, with no load, the loaded one). */
  setMetadata(track: NativeTrack): void {
    this.meta = track;
    this.metaDirty = true;
    this.scheduleFlush();
  }

  /** The tracks that follow the loaded one, in order: what plays on while the page sleeps. */
  setUpcoming(tracks: NativeTrack[]): void {
    this.upcoming = tracks.map((track) => ({
      ...track,
      url: absolute(track.url),
      artwork: track.artwork ? absolute(track.artwork) : null
    }));
    this.upcomingDirty = true;
    this.scheduleFlush();
  }

  /** Which of Next and Previous the lock screen offers. */
  setCommands(next: boolean, previous: boolean): void {
    if (this.commands.next === next && this.commands.previous === previous) return;
    this.commands = { next, previous };
    this.commandsDirty = true;
    this.scheduleFlush();
  }

  /**
   * Whether there is a route sheet to open. Android's output switcher sits on the media
   * notification instead, where the system puts it.
   */
  get hasRoutePicker(): boolean {
    return this.platform === 'ios';
  }

  /** The system's AirPlay and Bluetooth route sheet. */
  showRoutePicker(): void {
    this.send('showRoutePicker');
  }

  // ── Sending ──────────────────────────────────────────────────────────────

  private send(method: string, options?: Record<string, unknown>): void {
    void this.plugin.call(method, options).catch(() => {});
  }

  private resetTrail(url: string): void {
    this.loadId += 1;
    this.offset = 0;
    this.trail = url ? [url] : [];
    this.adopted = this.trail.length;
    this.catchingUp = false;
    this.stale = false;
    this.last = null;
    this.lastError = null;
  }

  private scheduleFlush(): void {
    if (this.flushQueued) return;
    this.flushQueued = true;
    queueMicrotask(() => this.flush());
  }

  /** One message per turn of the store: a `src`, its start position and its `play()` go together. */
  private flush(): void {
    this.flushQueued = false;
    const settlers = this.settlers;
    this.settlers = [];
    let landedPaused = false;

    if (this.pending && this.fresh) {
      // The first load of a new page: the device says where it is before anything is sent.
      for (const settler of settlers) settler.resolve();
      return;
    }

    if (this.pending) {
      const { position, autoplay } = this.pending;
      this.pending = null;
      if (position > 0) this.expect = { position, until: performance.now() + SEEK_WAIT_MS };
      this.send('loadTrack', {
        ...this.describe(),
        position,
        autoplay,
        loadId: this.loadId,
        seq: ++this.seq
      });
      this.upcomingDirty = true;
      this.commandsDirty = true;
    } else if (this.adoptedNow) {
      this.adoptedNow = false;
      if (this.adopted < this.trail.length) queueMicrotask(() => this.catchUp());
      else landedPaused = this.arrive();
    } else if (this.metaDirty && this.url && !this.stale) {
      this.send('setMetadata', { ...this.describe(), loadId: this.loadId });
    }
    this.metaDirty = false;

    if (this.commandsDirty) {
      this.commandsDirty = false;
      this.send('setCommands', this.commands);
    }
    // While the store is behind, its idea of what follows is behind too.
    if (this.upcomingDirty && this.url && !this.catchingUp && !this.stale) {
      this.upcomingDirty = false;
      // `at` names the track this list follows, as the device counts them: one that has moved on
      // by itself since must not take the list as following where it is now.
      const at = this.offset + this.adopted - 1;
      const sent = `${this.loadId}:${at}:${this.upcoming.map((track) => track.url).join('\n')}`;
      // The store says what follows on every load and queue change; mostly it is what the device
      // already holds, and 20 tracks need not cross the bridge again to say so.
      if (sent !== this.upcomingSent) {
        this.upcomingSent = sent;
        this.send('setUpcoming', { items: this.upcoming, loadId: this.loadId, at });
      }
    }

    for (const settler of settlers) {
      if (landedPaused) settler.reject(new Error('Paused on the device.'));
      else settler.resolve();
    }
  }

  private describe(): NativeTrack {
    return {
      url: this.url,
      title: this.meta?.title ?? '',
      artist: this.meta?.artist ?? '',
      album: this.meta?.album ?? '',
      artwork: this.meta?.artwork ? absolute(this.meta.artwork) : null
    };
  }

  /**
   * The first load of a new page, now that the device has said where it is. If it is playing the
   * track being loaded, or has played on past it, nothing is loaded: this page follows the device
   * from that track on, as it would have had it been awake all along. Otherwise the load is sent.
   */
  private takeOver(state: NativeState | null): void {
    const at =
      state && state.status !== 'paused' && this.url ? state.trail.lastIndexOf(this.url) : -1;
    if (!state || at < 0 || at > state.index) {
      this.scheduleFlush(); // sent as any load is, replacing whatever the device holds
      return;
    }
    this.pending = null;
    this.expect = null;
    this.loadId = state.loadId;
    this.offset = at;
    this.trail = [this.url];
    this.adopted = 1;
    if (at === state.index) {
      this.position = state.position;
      this.positionAt = performance.now();
    }
    this.upcomingDirty = true;
    this.commandsDirty = true;
    this.onState(state);
    this.scheduleFlush();
  }

  // ── Following the device ─────────────────────────────────────────────────

  private refresh(): void {
    void this.plugin.call('getState').then(
      (data) => this.onState(data as NativeState),
      () => {}
    );
  }

  private onState(report: NativeState): void {
    if (!report || report.loadId !== this.loadId || this.pending || !Array.isArray(report.trail)) {
      return;
    }
    const state =
      this.offset > 0
        ? { ...report, trail: report.trail.slice(this.offset), index: report.index - this.offset }
        : report;
    this.last = state;
    if (state.trail.length > this.trail.length) this.trail = state.trail;
    if (this.adopted < this.trail.length) {
      if (!this.catchingUp && !this.stale) this.catchUp();
      return;
    }
    if (this.catchingUp || this.stale || state.index !== this.adopted - 1) return;
    this.apply(state);
    if (state.ended) this.tail();
  }

  private onEnded(data: { loadId: number; index: number }): void {
    if (data.loadId !== this.loadId || this.pending || this.catchingUp || this.stale) return;
    if (data.index - this.offset === this.adopted - 1) this.tail();
  }

  private onError(data: { loadId: number; index: number; kind: string }): void {
    if (data.loadId !== this.loadId || this.pending) return;
    const index = data.index - this.offset;
    this.lastError = { index, kind: data.kind };
    if (this.catchingUp || this.stale || index !== this.adopted - 1) return;
    this.fail(data.kind);
  }

  private onRemoteEvent(data: { id: number; command: RemoteCommand; time?: number }): void {
    // Answered first: unanswered, the device carries the command out itself. Its answer says
    // whether it already has (this page was too slow), and then it must not happen twice.
    const carryOut = () => this.onRemote?.(data.command, data.time);
    void this.plugin.call('ackRemote', { id: data.id }).then((answer) => {
      if ((answer as { expired?: boolean } | null)?.expired !== true) carryOut();
    }, carryOut);
  }

  private fire(type: string): void {
    this.dispatchEvent(new Event(type));
  }

  /** `play` and `pause` follow their call, as an element's do, unless undone in between. */
  private later(type: 'play' | 'pause'): void {
    queueMicrotask(() => {
      if ((type === 'pause') === this.isPaused) this.fire(type);
    });
  }

  private fail(kind: string): void {
    this.failure = { code: kind === 'format' ? MEDIA_ERR_SRC_NOT_SUPPORTED : MEDIA_ERR_NETWORK };
    this.progressing = false;
    this.fire('error');
  }

  /** The device's report on the track the store has loaded, as the element's own events. */
  private apply(state: NativeState): void {
    if (state.duration > 0) {
      this.length = state.duration;
      if (this.ready === 0) {
        this.ready = HAVE_ENOUGH_DATA;
        this.fire('loadedmetadata');
      }
    }
    // Older than the last thing asked of the device: its position and status are yesterday's.
    if (state.seq < this.seq) return;
    // Playing again after the tail (Play on a queue that had run out): its next end is news.
    if (!state.ended) this.tailHandled = false;

    let believe = true;
    if (this.expect) {
      const landed = Math.abs(state.position - this.expect.position) <= SEEK_TOLERANCE_S;
      if (landed || performance.now() > this.expect.until) this.expect = null;
      else believe = false; // still on its way to where it was sent
    }
    if (believe) {
      // A track that played out reports no position worth comparing (iOS has let go of it).
      if (!state.ended && state.position + REWIND_S < this.currentTime && this.onRemote) {
        // The device moved back by itself (Previous or a scrub, carried out while the page
        // slept). Told to the store as the seek it is, or the jump would read as a song's end.
        this.quietSeek = true;
        try {
          this.onRemote('seek', state.position);
        } finally {
          this.quietSeek = false;
        }
      }
      this.position = state.position;
      this.positionAt = performance.now();
    }

    if (state.status === 'paused') {
      this.progressing = false;
      if (!this.isPaused) {
        this.isPaused = true;
        this.fire('pause');
      }
    } else {
      if (this.isPaused) {
        this.isPaused = false;
        this.fire('play');
      }
      const flowing = state.status === 'playing';
      const was = this.progressing;
      this.progressing = flowing;
      if (flowing && !was) this.fire('playing');
      else if (!flowing && was) this.fire('waiting');
    }
    if (this.ready > 0) this.fire('timeupdate');
  }

  /** The store has reached the track the device is on. Returns whether it is paused there. */
  private arrive(): boolean {
    this.catchingUp = false;
    const error = this.lastError;
    if (error && error.index === this.adopted - 1) {
      queueMicrotask(() => this.fail(error.kind));
      return false;
    }
    const state = this.last;
    if (!state || state.index !== this.adopted - 1) return false;
    this.apply(state);
    if (state.ended) queueMicrotask(() => this.tail());
    return state.status === 'paused';
  }

  /** Move the store one track along the trail. */
  private catchUp(): void {
    if (this.pending || this.inFinish || this.adopted >= this.trail.length) return;
    this.catchingUp = true;
    this.finish();
    if (this.followed || this.pending) return;
    // The store did not take the track the device went on to (its queue changed under it). The
    // store's song is over and paused, so the device must not play on with something else.
    this.catchingUp = false;
    this.stale = true;
    this.send('pause', { seq: ++this.seq });
  }

  /** The last track played out and the device has nothing after it: the store supplies it. */
  private tail(): void {
    if (this.tailHandled || this.inFinish) return;
    this.tailHandled = true;
    this.finish();
  }

  /**
   * What the store reads as a song's end: a source that has played, back at its start. A track
   * that came and went while the page slept is given the same history first.
   */
  private finish(): void {
    this.ready = HAVE_ENOUGH_DATA;
    this.position = Math.max(this.currentTime, 1);
    this.progressing = false;
    this.fire('playing');
    this.fire('timeupdate');
    this.position = 0;
    this.positionAt = performance.now();
    this.expect = null;
    this.followed = false;
    this.inFinish = true;
    try {
      this.fire('seeking');
    } finally {
      this.inFinish = false;
    }
  }
}

function absolute(url: string): string {
  try {
    return new URL(url, typeof location === 'undefined' ? undefined : location.href).href;
  } catch {
    return url;
  }
}

/** The shell's player, or null in a browser: the store then uses an audio element as ever. */
export function createNativeAudio(): NativeAudio | null {
  const plugin = nativePlugin('NativeAudio');
  return plugin ? new NativeAudio(plugin, nativePlatform() ?? 'ios') : null;
}
