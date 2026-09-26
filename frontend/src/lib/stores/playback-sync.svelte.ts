/**
 * The account's playback session ("Connect"): one session per account, held by one device at a
 * time, seen by every signed-in tab and phone.
 *
 * This store owns the stream (`GET /api/playback/stream`), what it said last, and this tab's side
 * of the protocol:
 *
 *  • REPORTING — a local play intent claims the session (`claim: true`); every other change to
 *    local playback is reported while this device holds it, coalesced, plus a heartbeat every 20 s.
 *    Nothing is ever awaited before `play()`: the player starts, then the report goes out.
 *  • STEPPING ASIDE — when the session moves to another device while this one plays, this one
 *    pauses and says where the music went ("Now playing on iPhone", with Play here).
 *  • STEERING — while another reachable device holds it, the transport sends that device commands
 *    and watches for them to land (5 s), saying so when one does not.
 *  • OBEYING — commands addressed to this tab are carried out on the local element, then
 *    acknowledged; a transfer picks the session up here and claims it.
 *  • PICKING UP — "This device", Play here, or Play on a remembered session adopts it here at its
 *    position, same queue, same station, without counting a new listen (Next or Previous onto
 *    another song starts one, and counts it).
 *
 * It plugs into the player (`setPlaybackSync`) so every component that reads `playerStore` shows
 * the session wherever it plays. It never starts for the demo account (strangers share it) and is
 * never started by the anonymous share page; there the player is exactly what it always was. The
 * rules themselves are in `$lib/playback-sync/*`, pure and tested, and ported by the Android client.
 */

import { toast } from 'svelte-sonner';
import { browser } from '$app/environment';
import {
  ApiError,
  fetchPlayback,
  getSongStreamUrl,
  openPlaybackStream,
  reportPlaybackState,
  sendPlaybackCommand,
  toPlayerSong
} from '$lib/api-client';
import { isDemo } from '$lib/auth/capabilities';
import type { SessionUser } from '$lib/auth/session-types';
import { isInstalledApp } from '$lib/hooks/viewport-insets.svelte';
import {
  claimTabDeviceId,
  describeDevice,
  readOrCreateId,
  DEVICE_CHANNEL_NAME,
  INSTALL_ID_KEY,
  type ChannelLike,
  type TabIdentity
} from '$lib/playback-sync/device';
import {
  adoptStart,
  deriveMode,
  deviceLabel,
  extrapolatePositionMs,
  isSameBrowser,
  isSuperseded,
  landedCommands,
  mayAutoResume,
  nowPlayingElsewhere,
  resolveAdoptQueue,
  sessionLine,
  shouldApplySession,
  showsSession,
  snapshotReportFor,
  trimQueue,
  withLocalClaim,
  type PendingCommand,
  type PlaybackMode
} from '$lib/playback-sync/session';
import type {
  DeviceKind,
  PlaybackCommandEvent,
  PlaybackCommandName,
  PlaybackDevice,
  PlaybackOverview,
  PlaybackSession,
  PlaybackStateReport,
  PlaybackStateResponse
} from '$lib/playback-sync/wire';
import {
  localPlayback,
  setPlaybackSync,
  type LocalPlaybackState,
  type PlaybackSyncHooks,
  type PlayerRemote,
  type PlayerSong,
  type PlayOutcome
} from '$lib/stores/player.svelte';
import { songsStore } from '$lib/stores/songs.svelte';
import { artistOf } from '$lib/track-list-view.svelte';

/** While this device holds the session, it reports at least this often (the contract's 20 s). */
export const HEARTBEAT_MS = 20_000;
/** Ordinary changes wait this long, so a track change's pause → play reads as one report. */
export const REPORT_COALESCE_MS = 400;
/** Claims and command acknowledgements go out almost at once (just past the synchronous burst). */
export const REPORT_PROMPT_MS = 50;
/** A command that has not landed by now did not reach its device. */
export const COMMAND_TIMEOUT_MS = 5_000;
/** How long a reloaded tab waits for the stream's first snapshot before resuming by itself. */
export const RESTORE_GATE_MS = 3_000;
/**
 * A claim that could not be delivered (no network) keeps riding on later reports for this long.
 * Past it, whoever claimed since then should win — this device then learns it was superseded.
 */
export const CLAIM_RETRY_WINDOW_MS = 60_000;
/** A dragged scrubber sends one seek, this long after it settles. */
export const SEEK_SETTLE_MS = 250;
/** How often a remote song's position moves on screen (the local player writes at 10 Hz). */
const TICK_MS = 200;

type StepAction = 'none' | 'next' | 'previous';

/** One row of the device picker. */
export interface DeviceEntry {
  deviceId: string;
  /** "This device", "Another tab", or the device's name. */
  label: string;
  /** The device's own name under "This device"; otherwise null. */
  detail: string | null;
  kind: DeviceKind;
  isThis: boolean;
  /** Where the music is: this device in local mode, else the device holding the session. */
  current: boolean;
  status: 'playing' | 'paused' | null;
}

// ── state ─────────────────────────────────────────────────────────────────────

let enabled = $state(false);
let deviceId = $state<string | null>(null);
let deviceName = $state('');
let deviceKind = $state<DeviceKind>('unknown');
let installId: string | null = null;

/** The session as the server last described it (never edited locally). */
let session = $state<PlaybackSession | null>(null);
/** Monotonic time `session` arrived — what its `positionMs` is measured from. */
let receivedAt = $state(0);
let devices = $state<PlaybackDevice[]>([]);
/** A local play intent's claim is on its way (or failed to go and rides on the next report). */
let claimPending = $state(false);
/**
 * What a command this tab sent is expected to do (pause, resume, a seek), shown until it lands or
 * fails — the other device's next heartbeat must not flip the button back for a second.
 */
let optimistic = $state<{ isPlaying: boolean; positionMs: number; at: number } | null>(null);
/** A scrub on a remembered session: where Play will pick it up. */
let rememberedSeekMs = $state<number | null>(null);
/** The clock the remote position is extrapolated to; ticks only while a remote song plays. */
let now = $state(0);

let startedFor: string | null = null;
let identity: TabIdentity | null = null;
let closeStream: (() => void) | null = null;
let disposeEffects: (() => void) | null = null;
let heartbeat: ReturnType<typeof setInterval> | null = null;
let lastVersion: number | null = null;
let hasSnapshot = false;
let gateWaiters: ((ok: boolean) => void)[] = [];
let claimSince = 0;
/** A transfer this tab asked for: the session moving there is not news worth a toast. */
let transferringTo: string | null = null;
/**
 * A session sent here (a transfer) is being picked up: `play()` has been called and has not
 * answered. The claim goes out only once it plays, so until then the session still names the
 * sender — and the sender's heartbeat must not make this tab step aside from its own pick-up.
 */
let adopting: object | null = null;
/** Commands this tab sent and is waiting to see land, by id; `seq` is the order they were sent. */
const pending = new Map<
  string,
  {
    command: PendingCommand;
    name: string;
    seq: number;
    timer: ReturnType<typeof setTimeout> | null;
  }
>();
let commandSeq = 0;
/**
 * Per device, the newest command seen to land. One sent before it whose own answer comes back
 * later has landed too: the device carries its commands out in order.
 */
const reachedSeq = new Map<string | null, number>();
let seekTimer: ReturnType<typeof setTimeout> | null = null;

/**
 * The report waiting to go. `stopped`: this device holds the session as playing with nothing of it
 * loaded, and says it stopped (see onOverview) — anything else queued with it describes the player.
 */
let queuedReport: { claim: boolean; inResponseTo: string | null; stopped: boolean } | null = null;
let reportTimer: ReturnType<typeof setTimeout> | null = null;
let reportDueAt = 0;
let reportInFlight = false;

function monotonic(): number {
  return typeof performance !== 'undefined' ? performance.now() : Date.now();
}

// ── what the screen shows ────────────────────────────────────────────────────

/** The session with this device's own claim applied while it is in flight. */
const effective = $derived(
  claimPending && session && deviceId ? withLocalClaim(session, deviceId, deviceName) : session
);
const mode = $derived<PlaybackMode>(deriveMode(effective, deviceId, enabled));
/**
 * A share link's track loaded here (still playing after a soft navigation into the app) is what
 * is heard, and nobody's session: the player keeps showing it until something is started from the
 * library.
 */
const shareTrackHere = $derived(localPlayback.song !== null && !isSessionTrack(localPlayback.song));
const showing = $derived(
  enabled && !shareTrackHere && showsSession(mode, effective, localPlayback.song !== null)
);

/** The session as displayed: optimistic while a command is out, paused when nothing is live. */
const view = $derived.by(() => {
  const s = effective;
  if (!s) return null;
  if (mode !== 'remote') {
    return { ...s, isPlaying: false, positionMs: rememberedSeekMs ?? s.positionMs, base: receivedAt };
  }
  if (optimistic) {
    return { ...s, isPlaying: optimistic.isPlaying, positionMs: optimistic.positionMs, base: optimistic.at };
  }
  return { ...s, base: receivedAt };
});

const positionMs = $derived(view ? extrapolatePositionMs(view, view.base, now) : 0);

const shownSong = $derived.by((): PlayerSong | null => {
  const s = effective;
  if (!s) return null;
  const row = songsStore.songsById.get(s.songId);
  return row ? toPlayerSong(row, artistOf(row)) : hintSong(s);
});

const durationSec = $derived.by(() => {
  if (!view) return 0;
  if (view.durationMs) return view.durationMs / 1000;
  return songsStore.songsById.get(view.songId)?.durationSeconds ?? 0;
});

const activeDevice = $derived(
  effective ? (devices.find((d) => d.deviceId === effective.activeDeviceId) ?? null) : null
);

const line = $derived(
  showing ? sessionLine(mode, view, isSameBrowser(activeDevice, deviceId, installId)) : null
);

const entries = $derived.by((): DeviceEntry[] => {
  if (!enabled || !deviceId) return [];
  const s = effective;
  const here = mode === 'local';
  const holdsHere = here && s !== null && s.activeDeviceId === deviceId;
  const self: DeviceEntry = {
    deviceId,
    label: 'This device',
    detail: deviceName,
    kind: deviceKind,
    isThis: true,
    current: here,
    status: holdsHere && localPlayback.song ? (localPlayback.isPlaying ? 'playing' : 'paused') : null
  };
  const others = devices
    .filter((d) => d.deviceId !== deviceId && d.online)
    .map((d): DeviceEntry => {
      const holds = mode === 'remote' && s?.activeDeviceId === d.deviceId;
      return {
        deviceId: d.deviceId,
        label: deviceLabel(d, deviceId, installId),
        detail: null,
        kind: d.kind,
        isThis: false,
        current: holds,
        status: holds ? (view?.isPlaying ? 'playing' : 'paused') : null
      };
    })
    .sort((a, b) => Number(b.current) - Number(a.current) || a.label.localeCompare(b.label));
  return [self, ...others];
});

function hintSong(s: PlaybackSession): PlayerSong {
  return {
    ...standInSong(s.songId),
    title: s.title?.trim() || 'Unknown title',
    artist: s.artist?.trim() || 'Unknown artist',
    album: s.album
  };
}

/** A queue id whose row has not arrived yet: it plays by its id, and becomes its row later. */
function standInSong(id: number): PlayerSong {
  return {
    id,
    title: 'Unknown title',
    artist: 'Unknown artist',
    streamUrl: getSongStreamUrl(id),
    coverUrl: null,
    standIn: true
  };
}

/** What a device is called in a sentence: its name, or "the other tab". */
function nameOf(id: string | null): string {
  const device = devices.find((d) => d.deviceId === id);
  if (device) return isSameBrowser(device, deviceId, installId) ? 'the other tab' : device.name;
  if (id && session?.activeDeviceId === id && session.activeDeviceName) return session.activeDeviceName;
  return 'the other device';
}

function holdsSessionHere(): boolean {
  return claimPending || (session !== null && deviceId !== null && session.activeDeviceId === deviceId);
}

/** An account-library track, streamed by its id — not a share link's, which is nobody's session. */
function isSessionTrack(song: PlayerSong): boolean {
  return song.streamUrl === getSongStreamUrl(song.id);
}

/**
 * This device is playing the account's session: the one thing another device's claim stops. A
 * pick still in its pre-flight counts — it is about to play (the report already says so), and
 * after an `ended` the element holds nothing playing to notice.
 */
function playsSessionHere(): boolean {
  const song = localPlayback.song;
  if (song !== null && isSessionTrack(song) && localPlayback.playing()) return true;
  const pick = localPlayback.loading();
  return pick !== null && isSessionTrack(pick);
}

/** A claim of ours on its way, or a pick-up about to claim: either wins over what the server says. */
function claiming(): boolean {
  return claimPending || adopting !== null;
}

// ── applying what the server says ─────────────────────────────────────────────

function applySession(next: PlaybackSession | null, kind: 'snapshot' | 'session'): void {
  if (kind === 'session') {
    if (!next || !shouldApplySession(next.version, lastVersion, 'session')) return;
  }
  // A reconnect's snapshot (the server ends every stream after a few minutes) mostly repeats the
  // session already shown; only a change clears a scrub waiting on a remembered session.
  if (next?.version !== session?.version) rememberedSeekMs = null;
  lastVersion = next?.version ?? null;
  session = next;
  const at = monotonic();
  receivedAt = at;
  now = at;
  expireClaim();
  settleCommands();
  checkSuperseded();
}

function onOverview(overview: PlaybackOverview): void {
  devices = overview.devices;
  applySession(overview.session, 'snapshot');
  if (!hasSnapshot) {
    hasSnapshot = true;
    releaseGate(decideRestore());
  }
  // A claim that could not go out while the stream was down goes now, with the connection back.
  if (claimPending) {
    scheduleReport({ claim: true });
    return;
  }
  // Still named as the holder (a reconnect, an API restart that forgot it was playing): say now
  // what is true here rather than at the next heartbeat — or never, with nothing loaded to
  // heartbeat about.
  const say = snapshotReportFor(session, deviceId, sessionLoadedHere());
  if (say === 'heartbeat') scheduleReport({ prompt: true });
  else if (say === 'stopped') scheduleReport({ stopped: true });
}

/** Something of the session is loaded here to report: an account-library track (see flushReport). */
function sessionLoadedHere(): boolean {
  const state = localPlayback.state();
  return state !== null && isSessionTrack(state.song);
}

function decideRestore(): boolean {
  return !deviceId || mayAutoResume(session, deviceId);
}

function releaseGate(ok: boolean): void {
  const waiters = gateWaiters;
  gateWaiters = [];
  for (const resolve of waiters) resolve(ok);
}

/** A claim that could not be delivered for a minute stops asserting itself. */
function expireClaim(): void {
  if (claimPending && !reportInFlight && Date.now() - claimSince > CLAIM_RETRY_WINDOW_MS) {
    claimPending = false;
  }
}

/** The session moved on while this device still plays it: step aside, and say where it went. */
function checkSuperseded(): void {
  const s = session;
  if (!s || !deviceId) return;
  if (!isSuperseded(s, deviceId, playsSessionHere(), claiming())) return;
  localPlayback.pause();
  announceElsewhere(s);
}

function announceElsewhere(s: PlaybackSession): void {
  if (transferringTo !== null && s.activeDeviceId === transferringTo) {
    transferringTo = null; // this tab sent it there
    return;
  }
  const active = devices.find((d) => d.deviceId === s.activeDeviceId);
  toast(nowPlayingElsewhere(s.activeDeviceName, isSameBrowser(active, deviceId, installId)), {
    action: { label: 'Play here', onClick: () => void playHere() }
  });
}

function settleCommands(): void {
  const waiting = [...pending.values()].sort((a, b) => a.seq - b.seq);
  const commands = waiting.map((entry) => entry.command);
  const landed = new Set(landedCommands(session, commands));
  for (const entry of waiting) {
    const target = entry.command.targetDeviceId;
    if (!landed.has(entry.command.commandId) && entry.seq > (reachedSeq.get(target) ?? 0)) continue;
    if (entry.timer !== null) clearTimeout(entry.timer);
    pending.delete(entry.command.commandId);
    reachedSeq.set(target, Math.max(reachedSeq.get(target) ?? 0, entry.seq));
    // A transfer away from a device that was not playing leaves nothing to step aside from. The
    // same test the step-aside uses: a pick in its pre-flight (after an `ended`, with the element
    // paused) is still stepped aside from, and the toast it would bring is this tab's own move.
    if (entry.command.command === 'transfer' && !playsSessionHere()) transferringTo = null;
  }
  if (pending.size === 0 && seekTimer === null) optimistic = null;
}

/** Catch up after a command went nowhere: the server's answer replaces the guesses. */
async function resync(): Promise<void> {
  try {
    const overview = await fetchPlayback();
    if (!enabled) return;
    devices = overview.devices;
    applySession(overview.session, 'snapshot');
  } catch (err) {
    if (featureOff(err)) disable();
  }
}

/** 401/403 (signed out, demo) or a 404 without a known code (an API without the feature). */
function featureOff(err: unknown): boolean {
  if (!(err instanceof ApiError)) return false;
  if (err.status === 401 || err.status === 403) return true;
  return err.status === 404 && err.code !== 'no_session';
}

// ── reporting ────────────────────────────────────────────────────────────────

function scheduleReport(request: {
  claim?: boolean;
  inResponseTo?: string;
  prompt?: boolean;
  stopped?: boolean;
}): void {
  if (!enabled) return;
  queuedReport = {
    claim: (queuedReport?.claim ?? false) || request.claim === true,
    inResponseTo: request.inResponseTo ?? queuedReport?.inResponseTo ?? null,
    stopped: (queuedReport?.stopped ?? false) || request.stopped === true
  };
  const prompt = request.claim || request.inResponseTo || request.prompt || request.stopped;
  armReport(prompt ? REPORT_PROMPT_MS : REPORT_COALESCE_MS);
}

function armReport(delay: number): void {
  const due = Date.now() + delay;
  if (reportTimer !== null) {
    if (reportDueAt <= due) return;
    clearTimeout(reportTimer);
  }
  reportDueAt = due;
  reportTimer = setTimeout(() => {
    reportTimer = null;
    void flushReport();
  }, delay);
}

async function flushReport(): Promise<void> {
  const request = queuedReport;
  if (!request || reportInFlight) return;
  if (!enabled) {
    queuedReport = null;
    return;
  }
  if (!deviceId) return; // sent once the tab's id has settled (see start)
  expireClaim();
  const state = localPlayback.state();
  const claim = request.claim || claimPending;
  let report: PlaybackStateReport;
  // Nothing loaded here: nothing to claim with. A share page's track (still playing after a soft
  // navigation into the app) streams through its share token — it is nobody's session.
  if (!state || !isSessionTrack(state.song)) {
    queuedReport = null;
    claimPending = false;
    // Held here as playing all the same: the session itself says what stopped.
    const stopped = request.stopped && !claim ? stoppedReport() : null;
    if (!stopped) return;
    report = stopped;
  } else {
    // Only the device holding the session reports; an acknowledgement always goes (the server,
    // which addressed the command here, is the judge of whether it still counts).
    if (!claim && !request.inResponseTo && !holdsSessionHere()) {
      queuedReport = null;
      return;
    }
    report = buildReport(state, claim, request.inResponseTo);
  }
  queuedReport = null;
  reportInFlight = true;
  try {
    const response = await reportPlaybackState(report);
    // A newer claim queued meanwhile keeps the claim pending until it too has gone.
    if (claim && !claimQueued()) claimPending = false;
    handleStateResponse(response, claim);
  } catch (err) {
    if (featureOff(err)) {
      disable();
    } else if (err instanceof ApiError && err.status < 500) {
      claimPending = false; // refused as such: repeating it would be refused again
      // Whoever holds the session still does; this device, playing, must not play on beside it.
      if (claim) checkSuperseded();
    }
    // Otherwise (offline, a 5xx) a claim stays pending and rides on the next report.
  } finally {
    reportInFlight = false;
    if (queuedReport && enabled) armReport(REPORT_PROMPT_MS);
  }
}

/** Read through a function: TypeScript would otherwise keep the pre-`await` narrowing. */
function claimQueued(): boolean {
  return queuedReport?.claim ?? false;
}

function buildReport(
  state: LocalPlaybackState,
  claim: boolean,
  inResponseTo: string | null
): PlaybackStateReport {
  const trimmed = trimQueue(state.queue, state.queueIndex);
  const seconds = songsStore.songsById.get(state.song.id)?.durationSeconds;
  return {
    deviceId: deviceId as string,
    installId,
    deviceName,
    deviceKind,
    client: 'web',
    claim,
    inResponseTo,
    songId: state.song.id,
    title: state.song.title || null,
    artist: state.song.artist || null,
    album: state.song.album ?? null,
    // Always the whole (capped) queue: a few kB, and it keeps "up next" exact for a device that
    // picks the session up.
    queue: trimmed.queue,
    queueIndex: trimmed.queueIndex,
    positionMs: state.positionMs,
    durationMs: state.durationMs ?? (seconds ? Math.round(seconds * 1000) : null),
    // A track still in its pre-flight is about to play; it is not paused.
    isPlaying: state.isPlaying || state.loading,
    playbackRate: state.playbackRate,
    radioSeedId: state.radioSeedId,
    shuffle: false
  };
}

/**
 * The session as the server holds it, stopped where it has got to: what a device holding it as
 * playing, with nothing of it loaded, can truthfully say. Not a claim, and not an acknowledgement.
 * Null once the session no longer names this device as playing.
 */
function stoppedReport(): PlaybackStateReport | null {
  const s = session;
  if (!s || !deviceId || snapshotReportFor(s, deviceId, false) !== 'stopped') return null;
  return {
    deviceId,
    installId,
    deviceName,
    deviceKind,
    client: 'web',
    claim: false,
    inResponseTo: null,
    songId: s.songId,
    title: s.title,
    artist: s.artist,
    album: s.album,
    queue: null, // unchanged
    queueIndex: s.queueIndex,
    positionMs: Math.round(extrapolatePositionMs(s, receivedAt, monotonic())),
    durationMs: s.durationMs,
    isPlaying: false,
    playbackRate: s.playbackRate,
    radioSeedId: s.radioSeedId,
    shuffle: s.shuffle
  };
}

function handleStateResponse(response: PlaybackStateResponse, wasClaim: boolean): void {
  if (response.session) applySession(response.session, 'session');
  if (wasClaim) {
    // The claim is settled, so a session naming another device counts again. The answer itself
    // may be older than one the stream already brought (another device claimed just after this
    // one) and then was not applied — the newer one is the judge, and this device steps aside.
    checkSuperseded();
    return;
  }
  if (response.accepted) return;
  // Not accepted: another device holds the session and this one missed the news (a suspended
  // iPhone, say). Step aside now — unless there is no session at all, when claiming it back is
  // the only way anyone will see what is playing.
  const s = response.session;
  if (s && s.activeDeviceId && s.activeDeviceId !== deviceId) {
    if (deviceId && isSuperseded(s, deviceId, playsSessionHere(), claiming())) {
      localPlayback.pause();
      announceElsewhere(s);
    }
  } else if (!s && playsSessionHere()) {
    claim();
  }
}

// ── local events (from the player) ───────────────────────────────────────────

function claim(inResponseTo?: string): void {
  claimPending = true;
  claimSince = Date.now();
  optimistic = null;
  rememberedSeekMs = null;
  scheduleReport({ claim: true, inResponseTo });
}

function onPlayIntent(): void {
  if (enabled) claim();
}

function onLocalChange(): void {
  if (enabled && holdsSessionHere()) scheduleReport({});
}

function restoreGate(): Promise<boolean> {
  if (!enabled) return Promise.resolve(true);
  if (hasSnapshot) return Promise.resolve(decideRestore());
  return new Promise((resolve) => {
    let settled = false;
    const finish = (ok: boolean) => {
      if (settled) return;
      settled = true;
      resolve(ok);
    };
    gateWaiters.push(finish);
    // No snapshot in time: behave as the player always did.
    setTimeout(() => finish(true), RESTORE_GATE_MS);
  });
}

// ── picking the session up here ──────────────────────────────────────────────

/**
 * Start the shown session on this device (a local play intent). `play()` is called inside, in
 * the same task as the tap; the claim goes out alongside it. Null when nothing could start.
 */
function adoptHere(s: PlaybackSession, step: StepAction): Promise<PlayOutcome> | null {
  // Every `/songs` row this tab holds, built or not — Android resolves against its whole `/songs`
  // dump too, so a session one client can pick up, the other can.
  const rows = songsStore.songsById;
  const resolved = resolveAdoptQueue(
    s,
    (id) => {
      const row = rows.get(id);
      return row ? toPlayerSong(row, artistOf(row)) : null;
    },
    // Rows not loaded yet (a tab that just opened): every id is kept under a stand-in — the
    // session's own hints for its song — and becomes its row once they arrive, so the claim below
    // carries the account's whole queue rather than the one song this tab could name.
    (id) => (rows.size > 0 ? null : id === s.songId ? hintSong(s) : standInSong(id))
  );
  if (!resolved) {
    toast.error('This song isn’t available here');
    return null;
  }
  const at = view && view.songId === s.songId ? extrapolatePositionMs(view, view.base, monotonic()) : s.positionMs;
  const start = adoptStart(resolved.index, resolved.queue.length, at, step);
  return localPlayback.adopt({
    queue: resolved.queue,
    index: start.index,
    positionSec: start.positionMs / 1000,
    radioSeedId: s.radioSeedId,
    // Next or Previous onto another song starts it from 0:00 — a listen, as it would be locally.
    listen: start.index !== resolved.index
  });
}

/** "This device", Play here, Play on a remembered session, a media key here. */
function playHere(step: StepAction = 'none'): Promise<PlayOutcome> {
  if (!enabled) return Promise.resolve('failed');
  if (mode === 'local' && localPlayback.song) {
    // Already here: just make sure it plays.
    if (localPlayback.playing()) return Promise.resolve('played');
    const outcome = localPlayback.resume();
    claim();
    return outcome;
  }
  const s = effective;
  if (!s) return Promise.resolve('failed');
  const started = adoptHere(s, step);
  if (!started) return Promise.resolve('failed');
  claim();
  void started.then((outcome) => {
    if (outcome === 'blocked') offerTapToPlay(null);
  });
  return started;
}

function offerTapToPlay(fromName: string | null): void {
  toast('Tap to play here', {
    description: fromName ? `${fromName} sent the music to this device.` : undefined,
    action: {
      label: 'Play here',
      onClick: () => {
        void localPlayback.resume();
        claim();
      }
    }
  });
}

// ── steering another device ──────────────────────────────────────────────────

async function send(
  command: PlaybackCommandName,
  targetDeviceId: string,
  positionMs: number | null
): Promise<void> {
  if (!deviceId) return;
  const name = nameOf(targetDeviceId);
  const seq = ++commandSeq;
  // As the session stood when it went: a transfer to the device already named there (a remembered
  // holder) must not count as landed before it has done anything (see commandLanded).
  const targetHeldSession = session?.activeDeviceId === targetDeviceId;
  try {
    const commandId = await sendPlaybackCommand({
      fromDeviceId: deviceId,
      targetDeviceId,
      command,
      positionMs
    });
    watch({ commandId, command, targetDeviceId, targetHeldSession }, name, seq);
  } catch (err) {
    if (featureOff(err)) {
      disable();
      return;
    }
    if (command === 'transfer') transferringTo = null;
    optimistic = null;
    // The session moved or ended since this screen last heard: catch up rather than complain.
    const stale =
      err instanceof ApiError && (err.code === 'not_active_device' || err.code === 'no_session');
    if (!stale) couldNotReach(command, name);
    void resync();
  }
}

function watch(command: PendingCommand, name: string, seq: number): void {
  const entry = { command, name, seq, timer: null as ReturnType<typeof setTimeout> | null };
  pending.set(command.commandId, entry);
  // The acknowledgement (or a later command's) can beat the command's own response here.
  settleCommands();
  if (!pending.has(command.commandId)) return;
  entry.timer = setTimeout(() => {
    pending.delete(command.commandId);
    if (command.command === 'transfer' && transferringTo === command.targetDeviceId) {
      transferringTo = null;
    }
    if (pending.size === 0 && seekTimer === null) optimistic = null;
    couldNotReach(command.command, name);
    void resync();
  }, COMMAND_TIMEOUT_MS);
}

function couldNotReach(command: PlaybackCommandName, name: string): void {
  // Resume and transfer were asking for music; offer it here instead (not while it plays here).
  const offer = (command === 'resume' || command === 'transfer') && !localPlayback.playing();
  toast.error(
    `Couldn’t reach ${name}`,
    offer ? { action: { label: 'Play here', onClick: () => void playHere() } } : undefined
  );
}

function command(name: 'pause' | 'resume' | 'next' | 'previous' | 'seek', positionMs?: number): void {
  const target = effective?.activeDeviceId;
  if (!target || mode !== 'remote') return;
  if (name === 'pause' || name === 'resume') {
    const at = monotonic();
    optimistic = { isPlaying: name === 'resume', positionMs: currentPositionMs(at), at };
    now = at;
  }
  void send(name, target, positionMs ?? null);
}

function currentPositionMs(at: number): number {
  return view ? extrapolatePositionMs(view, view.base, at) : 0;
}

/** A scrub on another device: the bar follows the finger, one seek goes when it settles. */
function seekRemote(seconds: number): void {
  const ms = Math.max(0, Math.round(seconds * 1000));
  const at = monotonic();
  optimistic = { isPlaying: view?.isPlaying ?? false, positionMs: ms, at };
  now = at;
  if (seekTimer !== null) clearTimeout(seekTimer);
  seekTimer = setTimeout(() => {
    seekTimer = null;
    command('seek', ms);
  }, SEEK_SETTLE_MS);
}

function transferTo(targetDeviceId: string): void {
  if (!enabled || !deviceId || targetDeviceId === deviceId) return;
  transferringTo = targetDeviceId;
  void send('transfer', targetDeviceId, null);
}

// ── obeying another device ───────────────────────────────────────────────────

function acknowledge(event: PlaybackCommandEvent): void {
  scheduleReport({ inResponseTo: event.commandId });
}

function execute(event: PlaybackCommandEvent): void {
  if (!enabled) return;
  switch (event.command) {
    case 'pause':
      localPlayback.pause();
      acknowledge(event);
      break;
    case 'resume':
      // Nothing loaded here to resume (the tab lost its queue): take the session instead.
      if (!localPlayback.song) {
        takeTransfer(event);
        break;
      }
      void localPlayback.resume().then((outcome) => {
        if (outcome === 'played') acknowledge(event);
        else if (outcome === 'blocked') offerTapToPlay(event.fromDeviceName);
      });
      break;
    case 'next':
      localPlayback.next();
      acknowledge(event);
      break;
    case 'previous':
      localPlayback.previous();
      acknowledge(event);
      break;
    case 'seek':
      localPlayback.seek((event.positionMs ?? 0) / 1000);
      acknowledge(event);
      break;
    case 'transfer':
      takeTransfer(event);
      break;
  }
}

/**
 * Another device sent the session here: pick it up, and claim it once it actually plays (the
 * sender plays on until then — no gap, and no silence if this tab cannot start). Until `play()`
 * answers, this tab is `adopting` and does not step aside for the sender's own reports.
 */
function takeTransfer(event: PlaybackCommandEvent): void {
  const s = session;
  if (!s) return;
  const started = adoptHere(s, 'none');
  if (!started) return;
  const pickUp = {};
  adopting = pickUp;
  void started.then((outcome) => {
    if (outcome === 'played') claim(event.commandId);
    if (adopting === pickUp) adopting = null;
    if (outcome === 'blocked') offerTapToPlay(event.fromDeviceName);
    // Nothing started (or something newer took over the element): the session decides again.
    if (outcome !== 'played') checkSuperseded();
  });
}

// ── the player's view of all this ────────────────────────────────────────────

const remote: PlayerRemote = {
  get active() {
    return showing;
  },
  get song() {
    return shownSong;
  },
  get isPlaying() {
    return mode === 'remote' && (view?.isPlaying ?? false);
  },
  get currentTime() {
    return positionMs / 1000;
  },
  get duration() {
    return durationSec;
  },
  get hasNext() {
    if (!view) return false;
    return view.queueIndex < view.queue.length - 1 || view.radioSeedId !== null;
  },
  pause() {
    command('pause');
  },
  resume() {
    if (mode === 'remote') command('resume');
    else void playHere();
  },
  next() {
    if (mode === 'remote') command('next');
    else void playHere('next');
  },
  previous() {
    if (mode === 'remote') command('previous');
    else void playHere('previous');
  },
  seek(seconds: number) {
    if (mode === 'remote') {
      seekRemote(seconds);
      return;
    }
    const limit = durationSec > 0 ? durationSec * 1000 : Number.POSITIVE_INFINITY;
    rememberedSeekMs = Math.max(0, Math.min(limit, Math.round(seconds * 1000)));
  }
};

const hooks: PlaybackSyncHooks = {
  remote,
  playIntent: onPlayIntent,
  changed: onLocalChange,
  restoreGate,
  playHere: (step) => void playHere(step)
};

// ── lifecycle ────────────────────────────────────────────────────────────────

function storage(kind: 'localStorage' | 'sessionStorage'): Storage | null {
  try {
    return window[kind];
  } catch {
    return null;
  }
}

function openChannel(): ChannelLike | null {
  try {
    return typeof BroadcastChannel === 'function' ? new BroadcastChannel(DEVICE_CHANNEL_NAME) : null;
  } catch {
    return null;
  }
}

/**
 * Turn the feature on for a signed-in account (the `(app)` layout calls this before `initPlayer`,
 * so a reload's restore can wait for the first snapshot). Idempotent per account; a no-op on the
 * server, without a user, and for the demo account.
 */
function start(user: SessionUser | null | undefined): void {
  if (!browser || !user || isDemo(user)) return;
  if (startedFor === user.id) return;
  if (startedFor !== null) stop();
  startedFor = user.id;
  enabled = true;
  hasSnapshot = false;
  lastVersion = null;

  const described = describeDevice(navigator.userAgent ?? '', {
    installed: isInstalledApp(),
    maxTouchPoints: navigator.maxTouchPoints ?? 0
  });
  deviceName = described.name;
  deviceKind = described.kind;
  installId = readOrCreateId(storage('localStorage'), INSTALL_ID_KEY);
  const tab = claimTabDeviceId({ storage: storage('sessionStorage'), channel: openChannel() });
  identity = tab;
  setPlaybackSync(hooks);

  disposeEffects = $effect.root(() => {
    // The remote position moves on screen only while a remote song plays.
    $effect(() => {
      if (!showing || !view?.isPlaying) return;
      now = monotonic();
      const id = setInterval(() => (now = monotonic()), TICK_MS);
      return () => clearInterval(id);
    });
    // A session picked up before the library loaded: its stand-ins become rows as they arrive.
    $effect(() => {
      const rows = songsStore.songsById;
      if (rows.size > 0) localPlayback.hydrate(rows);
    });
    // The music moving to another device is news even with the bar hidden: show it.
    let previous: PlaybackMode = 'local';
    $effect(() => {
      const current = mode;
      if (current === 'remote' && previous === 'local') localPlayback.revealMiniPlayer();
      previous = current;
    });
  });

  heartbeat = setInterval(() => {
    expireClaim();
    if (!enabled || !localPlayback.song || !holdsSessionHere()) return;
    scheduleReport({ prompt: true, claim: claimPending });
  }, HEARTBEAT_MS);

  void tab.ready.then((id) => {
    if (identity !== tab || !enabled) return;
    deviceId = id;
    closeStream = openPlaybackStream(
      { deviceId: id, installId, name: deviceName, kind: deviceKind },
      {
        onSnapshot: onOverview,
        onSession: (s) => applySession(s, 'session'),
        onDevices: (list) => (devices = list),
        onCommand: execute,
        // The browser gave up on the stream: find out whether the feature exists at all (403 for
        // demo, 404 on an API without it) and catch up on what was missed meanwhile.
        onDown: () => void resync()
      }
    );
    if (queuedReport) armReport(REPORT_PROMPT_MS); // play intents from before the id settled
  });
}

/** Everything off: the stream, the timers, the hooks. Idempotent. */
function stop(): void {
  if (startedFor === null) return;
  startedFor = null;
  setPlaybackSync(null);
  disable();
  identity?.dispose();
  identity = null;
  disposeEffects?.();
  disposeEffects = null;
  session = null;
  devices = [];
  deviceId = null;
  lastVersion = null;
  hasSnapshot = false;
}

/** The feature is not available (demo, signed out, an API without it) or is being stopped. */
function disable(): void {
  enabled = false;
  closeStream?.();
  closeStream = null;
  if (heartbeat !== null) clearInterval(heartbeat);
  heartbeat = null;
  if (reportTimer !== null) clearTimeout(reportTimer);
  reportTimer = null;
  if (seekTimer !== null) clearTimeout(seekTimer);
  seekTimer = null;
  for (const entry of pending.values()) if (entry.timer !== null) clearTimeout(entry.timer);
  pending.clear();
  queuedReport = null;
  claimPending = false;
  adopting = null;
  reachedSeq.clear();
  optimistic = null;
  rememberedSeekMs = null;
  transferringTo = null;
  releaseGate(true);
}

export const playbackSync = {
  /** On for this signed-in, non-demo account on an API that has the feature. */
  get enabled() {
    return enabled;
  },
  get mode() {
    return mode;
  },
  /** The player on screen is the session (remote or remembered), not this device's own. */
  get showsSession() {
    return showing;
  },
  /** "Playing on iPhone" / "Paused on MacBook" / "Last played on iPhone"; null when local. */
  get line() {
    return line;
  },
  /** Another reachable device holds the session. */
  get isRemote() {
    return showing && mode === 'remote';
  },
  get deviceName() {
    return deviceName;
  },
  /** What kind of device holds the session (for its icon); 'unknown' when it is not listed. */
  get activeKind(): DeviceKind {
    return activeDevice?.kind ?? 'unknown';
  },
  /** The picker's rows: this device first, then the other reachable ones. */
  get entries() {
    return entries;
  },
  /** Anything beyond this device to show in a picker at all. */
  get hasOtherDevices() {
    return entries.length > 1;
  },
  start,
  stop,
  playHere: (): void => void playHere(),
  transferTo,
  /** A picker row was chosen. */
  choose(entry: DeviceEntry): void {
    if (entry.isThis) void playHere();
    else if (!entry.current) transferTo(entry.deviceId);
  }
};
