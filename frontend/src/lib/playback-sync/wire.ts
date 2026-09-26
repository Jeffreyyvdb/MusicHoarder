/**
 * The playback-sync ("Connect") wire contract: what `GET /api/playback`, its SSE stream and the two
 * POSTs carry, and the validation every incoming message goes through before the store sees it.
 *
 * The rules are small on purpose, because the Android client ports them line for line: identity
 * fields (a version, a song id, the queue and its index, a device id, a command id) are strict and
 * a message missing one is dropped whole; display and optional fields fall back to a safe default
 * instead, so a server that adds or renames a hint never costs anyone their session. Nothing here
 * touches a browser API.
 */

export type DeviceKind = 'computer' | 'phone' | 'tablet' | 'unknown';
export type DeviceClient = 'web' | 'android';
export type PlaybackCommandName = 'pause' | 'resume' | 'next' | 'previous' | 'seek' | 'transfer';

/** One player instance of the account: a browser tab, or the Android app. */
export interface PlaybackDevice {
  deviceId: string;
  /** One per browser profile on the web; two tabs sharing it are "Another tab" to each other. */
  installId: string | null;
  name: string;
  kind: DeviceKind;
  client: DeviceClient;
  online: boolean;
  isActive: boolean;
}

/** The account's one playback session, as the server last described it. */
export interface PlaybackSession {
  version: number;
  songId: number;
  title: string | null;
  artist: string | null;
  album: string | null;
  queue: number[];
  queueIndex: number;
  /** As of the moment the server produced the message; see `extrapolatePositionMs`. */
  positionMs: number;
  durationMs: number | null;
  isPlaying: boolean;
  playbackRate: number;
  radioSeedId: number | null;
  shuffle: boolean;
  activeDeviceId: string | null;
  activeDeviceName: string | null;
  /** The active device is reachable. False: the session is remembered, not playing anywhere. */
  live: boolean;
  /** The most recent command the active device acknowledged. */
  lastCommandId: string | null;
  updatedAtUtc: string | null;
}

export interface PlaybackOverview {
  session: PlaybackSession | null;
  devices: PlaybackDevice[];
}

/** A `command` event: only ever sent to the target device's own streams. */
export interface PlaybackCommandEvent {
  commandId: string;
  command: PlaybackCommandName;
  positionMs: number | null;
  fromDeviceId: string | null;
  fromDeviceName: string | null;
}

/** `POST /api/playback/state`: this device's report. */
export interface PlaybackStateReport {
  deviceId: string;
  installId: string | null;
  deviceName: string;
  deviceKind: DeviceKind;
  client: DeviceClient;
  /** A local play intent happened here: take the session. `queue` is required with it. */
  claim: boolean;
  inResponseTo: string | null;
  songId: number;
  title: string | null;
  artist: string | null;
  album: string | null;
  /** Null means "unchanged" — only ever on a non-claim. */
  queue: number[] | null;
  queueIndex: number;
  positionMs: number;
  durationMs: number | null;
  isPlaying: boolean;
  playbackRate: number;
  radioSeedId: number | null;
  shuffle: boolean;
}

export interface PlaybackStateResponse {
  /** False: this device is not the active one, so the report changed nothing — stop playing. */
  accepted: boolean;
  session: PlaybackSession | null;
}

/** `POST /api/playback/command`. */
export interface PlaybackCommandRequest {
  fromDeviceId: string;
  targetDeviceId: string | null;
  command: PlaybackCommandName;
  positionMs: number | null;
}

const DEVICE_KINDS: readonly DeviceKind[] = ['computer', 'phone', 'tablet', 'unknown'];
const COMMANDS: readonly PlaybackCommandName[] = [
  'pause',
  'resume',
  'next',
  'previous',
  'seek',
  'transfer'
];

/**
 * A session, or null when the value is not one. Strict about identity (version, song, queue,
 * index); lenient about everything a client only displays or can default.
 */
export function parseSession(value: unknown): PlaybackSession | null {
  if (!isRecord(value)) return null;
  const { version, songId, queue, queueIndex } = value;
  if (!isWholeNumber(version) || version < 0) return null;
  if (!isWholeNumber(songId)) return null;
  if (!Array.isArray(queue) || queue.length === 0 || !queue.every(isWholeNumber)) return null;
  if (!isWholeNumber(queueIndex) || queueIndex < 0 || queueIndex >= queue.length) return null;

  return {
    version,
    songId,
    title: optionalString(value.title),
    artist: optionalString(value.artist),
    album: optionalString(value.album),
    queue: [...(queue as number[])],
    queueIndex,
    positionMs: isFiniteNumber(value.positionMs) ? Math.max(0, value.positionMs) : 0,
    durationMs: isFiniteNumber(value.durationMs) && value.durationMs > 0 ? value.durationMs : null,
    isPlaying: value.isPlaying === true,
    playbackRate:
      isFiniteNumber(value.playbackRate) && value.playbackRate > 0 ? value.playbackRate : 1,
    radioSeedId: isWholeNumber(value.radioSeedId) ? value.radioSeedId : null,
    shuffle: value.shuffle === true,
    activeDeviceId: nonEmptyString(value.activeDeviceId),
    activeDeviceName: nonEmptyString(value.activeDeviceName),
    live: value.live === true,
    lastCommandId: nonEmptyString(value.lastCommandId),
    updatedAtUtc: optionalString(value.updatedAtUtc)
  };
}

/** A device, or null without a device id — a row nobody could address is worth nothing. */
export function parseDevice(value: unknown): PlaybackDevice | null {
  if (!isRecord(value)) return null;
  const deviceId = nonEmptyString(value.deviceId);
  if (!deviceId) return null;
  const kind = DEVICE_KINDS.includes(value.kind as DeviceKind) ? (value.kind as DeviceKind) : 'unknown';
  return {
    deviceId,
    installId: nonEmptyString(value.installId),
    name: nonEmptyString(value.name) ?? 'Unknown device',
    kind,
    client: value.client === 'android' ? 'android' : 'web',
    online: value.online === true,
    isActive: value.isActive === true
  };
}

/** Every valid device in the list; the invalid ones are dropped, not the whole list. */
export function parseDevices(value: unknown): PlaybackDevice[] {
  if (!Array.isArray(value)) return [];
  const devices: PlaybackDevice[] = [];
  for (const entry of value) {
    const device = parseDevice(entry);
    if (device) devices.push(device);
  }
  return devices;
}

/**
 * `{ session, devices }` — the `snapshot` event and `GET /api/playback`. A session that fails
 * validation reads as no session: the snapshot still replaces what the client knew.
 */
export function parseOverview(value: unknown): PlaybackOverview | null {
  if (!isRecord(value)) return null;
  return { session: parseSession(value.session), devices: parseDevices(value.devices) };
}

/** `{ session }` — the `session` event. Null when the session inside is not valid. */
export function parseSessionEvent(value: unknown): PlaybackSession | null {
  return isRecord(value) ? parseSession(value.session) : null;
}

/** `{ devices }` — the `devices` event. Null when the message has no device list at all. */
export function parseDevicesEvent(value: unknown): PlaybackDevice[] | null {
  if (!isRecord(value) || !Array.isArray(value.devices)) return null;
  return parseDevices(value.devices);
}

/** A `command` event, or null. A `seek` without a position is not a command anyone can run. */
export function parseCommandEvent(value: unknown): PlaybackCommandEvent | null {
  if (!isRecord(value)) return null;
  const commandId = nonEmptyString(value.commandId);
  if (!commandId) return null;
  if (!COMMANDS.includes(value.command as PlaybackCommandName)) return null;
  const command = value.command as PlaybackCommandName;
  const positionMs = isFiniteNumber(value.positionMs) ? Math.max(0, value.positionMs) : null;
  if (command === 'seek' && positionMs === null) return null;
  return {
    commandId,
    command,
    positionMs,
    fromDeviceId: nonEmptyString(value.fromDeviceId),
    fromDeviceName: nonEmptyString(value.fromDeviceName)
  };
}

/** The `POST /api/playback/state` answer. Anything but an explicit `accepted: true` is a refusal. */
export function parseStateResponse(value: unknown): PlaybackStateResponse {
  if (!isRecord(value)) return { accepted: false, session: null };
  return { accepted: value.accepted === true, session: parseSession(value.session) };
}

/** Parse an SSE `data:` payload; null for anything that is not JSON. */
export function parseEventData(data: unknown): unknown {
  if (typeof data !== 'string') return null;
  try {
    return JSON.parse(data);
  } catch {
    return null;
  }
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isFinite(value);
}

function isWholeNumber(value: unknown): value is number {
  return typeof value === 'number' && Number.isSafeInteger(value);
}

function optionalString(value: unknown): string | null {
  return typeof value === 'string' ? value : null;
}

function nonEmptyString(value: unknown): string | null {
  return typeof value === 'string' && value.trim().length > 0 ? value : null;
}
