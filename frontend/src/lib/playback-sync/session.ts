import { previousAction } from '$lib/player-seek';
import type { PlaybackCommandName, PlaybackDevice, PlaybackSession } from './wire';

/**
 * The rules every client applies to the account's playback session — what this device shows, when
 * it steps aside, how far a remote song has got. Pure functions over plain values, so they are
 * tested case for case here and the Android client ports them without translating any browser API.
 */

/**
 * Where the music this device shows lives.
 *
 *  • `local` — there is no session, this device holds it, or the feature is off (demo, share page).
 *  • `remote` — another reachable device holds it: the transport sends it commands.
 *  • `remembered` — the device holding it is gone: the session is shown paused, and Play picks it
 *    up here.
 */
export type PlaybackMode = 'local' | 'remote' | 'remembered';

/** At most this many ids travel with a session. */
export const QUEUE_CAP = 1000;
/** How much of the already-played queue survives a trim. */
export const QUEUE_KEEP_BEHIND = 50;

export function deriveMode(
  session: PlaybackSession | null,
  myDeviceId: string | null,
  enabled: boolean
): PlaybackMode {
  if (!enabled || !session || !myDeviceId) return 'local';
  if (session.activeDeviceId === myDeviceId) return 'local';
  return session.live ? 'remote' : 'remembered';
}

/**
 * Whether the UI shows the session rather than the local player: always away from `local`, and in
 * `local` too when nothing is loaded here but the session exists (an app restarted as the active
 * device has an empty player; its Play should pick the session up, not do nothing).
 */
export function showsSession(
  mode: PlaybackMode,
  session: PlaybackSession | null,
  localLoaded: boolean
): boolean {
  if (!session) return false;
  return mode !== 'local' || !localLoaded;
}

/**
 * While this device's claim is on its way, it already holds the session as far as its own screen
 * is concerned: showing the other device for the round trip would flash "Playing on iPhone" under
 * a song that is starting here.
 */
export function withLocalClaim(
  session: PlaybackSession,
  myDeviceId: string,
  myDeviceName: string
): PlaybackSession {
  return { ...session, activeDeviceId: myDeviceId, activeDeviceName: myDeviceName, live: true };
}

/**
 * The session's position now, in ms. The server's `positionMs` is as of its message; `receivedAt`
 * and `now` come from one monotonic clock on this device (never the wall clock, which can jump).
 * A paused session stands still; a playing one moves at its rate, never past the song's end.
 */
export function extrapolatePositionMs(
  session: Pick<PlaybackSession, 'positionMs' | 'durationMs' | 'isPlaying' | 'playbackRate'>,
  receivedAt: number,
  now: number
): number {
  let position = session.positionMs;
  if (session.isPlaying) {
    const rate = session.playbackRate > 0 ? session.playbackRate : 1;
    position += Math.max(0, now - receivedAt) * rate;
  }
  position = Math.max(0, position);
  if (session.durationMs !== null && session.durationMs > 0) {
    position = Math.min(position, session.durationMs);
  }
  return position;
}

/**
 * Cap a queue before it is sent: a queue within the cap goes as it is; a longer one keeps the
 * current song, starts at most {@link QUEUE_KEEP_BEHIND} items before it, takes up to
 * {@link QUEUE_CAP} from there, and re-bases the index. The server enforces the same rule.
 */
export function trimQueue(
  ids: readonly number[],
  index: number
): { queue: number[]; queueIndex: number } {
  if (ids.length <= QUEUE_CAP) return { queue: [...ids], queueIndex: index };
  const start = Math.max(0, Math.min(index, ids.length - 1) - QUEUE_KEEP_BEHIND);
  return { queue: ids.slice(start, start + QUEUE_CAP), queueIndex: index - start };
}

/**
 * A `snapshot` always replaces what the client knew (a reconnect, an API restart); a `session`
 * event — and the session a report's answer carries — only when it is newer than the last one
 * applied, since two paths (the stream and a POST's answer) can deliver the same change twice and
 * in either order.
 */
export function shouldApplySession(
  incomingVersion: number,
  lastAppliedVersion: number | null,
  kind: 'snapshot' | 'session'
): boolean {
  if (kind === 'snapshot' || lastAppliedVersion === null) return true;
  return incomingVersion > lastAppliedVersion;
}

/**
 * Whether this device has been superseded: the session names another device while this one is
 * still playing it. Only an account-library track counts (`playingSessionLocally`): a share link's
 * track is nobody's session, so no claim elsewhere can stop it. A claim of ours still in flight
 * wins (`claiming`) — the session it answers may be the very one that shows the other device, a
 * moment before ours lands — and so does a pick-up that will claim the moment its `play()` answers
 * (a transfer sent here): the sender's own heartbeat still names the sender until then.
 */
export function isSuperseded(
  session: PlaybackSession | null,
  myDeviceId: string,
  playingSessionLocally: boolean,
  claiming: boolean
): boolean {
  if (!session || claiming || !playingSessionLocally) return false;
  return session.activeDeviceId !== null && session.activeDeviceId !== myDeviceId;
}

/**
 * After a reload, whether the restored queue may start playing on its own: only when this device
 * still holds the session, or nothing is live anywhere else. Another device playing means this
 * tab stays quiet and shows that device instead.
 */
export function mayAutoResume(session: PlaybackSession | null, myDeviceId: string): boolean {
  if (!session) return true;
  return session.activeDeviceId === myDeviceId || !session.live;
}

/** What this device says when a snapshot names it as the one holding the session. */
export type SnapshotReport = 'none' | 'heartbeat' | 'stopped';

/**
 * What this device says when a snapshot names it as the one holding the session:
 *
 *  • holding something of it (`sessionLoadedHere`): a report at once (`heartbeat`). The server
 *    treats a session it reloaded (after a restart) as paused until its device reports, and only a
 *    report keeps the holder reachable, so this is what makes it live again;
 *  • holding nothing of it while the session still says it plays here: the device came back (a
 *    reload that restored nothing, a crash) within the server's reconnect grace, which the server
 *    cannot tell from a routine reconnect — the session would play on for up to a minute and be
 *    remembered that far past where the music stopped. Saying it stopped (`stopped`) records the
 *    pause now.
 */
export function snapshotReportFor(
  session: PlaybackSession | null,
  myDeviceId: string | null,
  sessionLoadedHere: boolean
): SnapshotReport {
  if (!session || !myDeviceId || session.activeDeviceId !== myDeviceId) return 'none';
  if (sessionLoadedHere) return 'heartbeat';
  return session.isPlaying ? 'stopped' : 'none';
}

export interface PendingCommand {
  commandId: string;
  command: PlaybackCommandName;
  targetDeviceId: string | null;
  /**
   * The session already named the target when the command was sent: a transfer to the remembered
   * holder, back online with nothing playing. Its holding the session then says nothing about
   * whether the command got there.
   */
  targetHeldSession: boolean;
}

/**
 * A command has landed when the target acknowledged it (`lastCommandId`), or — for a transfer,
 * which the target answers with a claim rather than an ack — when the target holds the session.
 * That shortcut is only for a transfer that moves the session: one sent to the device that already
 * held it would count as landed the moment it went, so a failed pick-up there would never say
 * "Couldn't reach". Such a transfer lands only on its acknowledgement; the target claims with the
 * command's id (`inResponseTo`), which the server records as `lastCommandId`.
 */
export function commandLanded(session: PlaybackSession | null, pending: PendingCommand): boolean {
  if (!session) return false;
  if (session.lastCommandId === pending.commandId) return true;
  return (
    pending.command === 'transfer' &&
    !pending.targetHeldSession &&
    pending.targetDeviceId !== null &&
    session.activeDeviceId === pending.targetDeviceId
  );
}

/**
 * The ids of the commands still waiting (`pending`, in the order they were sent) that have landed:
 * each one {@link commandLanded} says has, and every command sent to the same device before it. A
 * device carries its commands out in the order its stream delivers them, but a quick burst (three
 * taps on Next) is acknowledged by one report naming only the newest — and the server passes on
 * only the newest session anyway — so the older ones never show up as `lastCommandId` themselves.
 */
export function landedCommands(
  session: PlaybackSession | null,
  pending: readonly PendingCommand[]
): string[] {
  const landed: string[] = [];
  const reached = new Set<string | null>();
  for (let i = pending.length - 1; i >= 0; i--) {
    const command = pending[i];
    if (!reached.has(command.targetDeviceId) && !commandLanded(session, command)) continue;
    reached.add(command.targetDeviceId);
    landed.push(command.commandId);
  }
  return landed.reverse();
}

/** "This device", "Another tab" (the same browser profile), or the device's own name. */
export function deviceLabel(
  device: Pick<PlaybackDevice, 'deviceId' | 'installId' | 'name'>,
  myDeviceId: string | null,
  myInstallId: string | null
): string {
  if (device.deviceId === myDeviceId) return 'This device';
  if (isSameBrowser(device, myDeviceId, myInstallId)) return 'Another tab';
  return device.name;
}

/** Another tab of this browser profile (same install id, different device id). */
export function isSameBrowser(
  device: Pick<PlaybackDevice, 'deviceId' | 'installId'> | null | undefined,
  myDeviceId: string | null,
  myInstallId: string | null
): boolean {
  if (!device || !myInstallId || device.deviceId === myDeviceId) return false;
  return device.installId === myInstallId;
}

/**
 * The line that says where the shown session is: "Playing on iPhone", "Paused on MacBook",
 * "Last played on iPhone", or "… in another tab" for a tab of this same browser. Null in `local`
 * mode, where the player on screen is this device's own.
 */
export function sessionLine(
  mode: PlaybackMode,
  session: PlaybackSession | null,
  sameBrowser: boolean
): string | null {
  if (mode === 'local' || !session) return null;
  const verb = mode === 'remembered' ? 'Last played' : session.isPlaying ? 'Playing' : 'Paused';
  if (sameBrowser) return `${verb} in another tab`;
  const name = session.activeDeviceName?.trim();
  if (name) return `${verb} on ${name}`;
  return mode === 'remembered' ? verb : `${verb} on another device`;
}

/** The superseded toast's title: "Now playing on iPhone" / "Now playing in another tab". */
export function nowPlayingElsewhere(name: string | null, sameBrowser: boolean): string {
  if (sameBrowser) return 'Now playing in another tab';
  const trimmed = name?.trim();
  return trimmed ? `Now playing on ${trimmed}` : 'Now playing on another device';
}

/**
 * Picking a session up here: the queue resolved against this device's library — every row it
 * holds, built or not (the web's whole `/songs` list, Android's whole `/songs` dump), so the two
 * clients agree about what can be picked up — positioned at the session's song. Ids the library
 * does not hold are skipped; when the current song itself is one of them the whole thing is
 * refused (null) — "This song isn't available here". `standIn` answers for an id `resolve` could
 * not: while the library's rows have not arrived, every id is kept under a stand-in (the song's
 * own display hints for the current one) rather than dropped, so the claim that follows never cuts
 * the account's queue short; the rows replace the stand-ins once they are here.
 */
export function resolveAdoptQueue<T>(
  session: Pick<PlaybackSession, 'songId' | 'queue' | 'queueIndex'>,
  resolve: (id: number) => T | null,
  standIn: (id: number) => T | null
): { queue: T[]; index: number } | null {
  const queue: T[] = [];
  let index = -1;
  session.queue.forEach((id, i) => {
    if (i === session.queueIndex) {
      // The session's own song id wins over whatever the queue says at its index.
      const current = resolve(session.songId) ?? standIn(session.songId);
      if (current === null) return;
      index = queue.length;
      queue.push(current);
      return;
    }
    const song = resolve(id) ?? standIn(id);
    if (song !== null) queue.push(song);
  });
  return index < 0 ? null : { queue, index };
}

/**
 * Where a picked-up session starts when it is picked up by Next or Previous (a media key, or the
 * transport of a remembered session) rather than by Play: Next moves one on at 0:00 (staying put
 * at the end of the queue, where the station takes over); Previous follows the player's own rule —
 * restart past 3 s, else one back.
 */
export function adoptStart(
  index: number,
  queueLength: number,
  positionMs: number,
  step: 'none' | 'next' | 'previous'
): { index: number; positionMs: number } {
  if (step === 'next') {
    return index < queueLength - 1 ? { index: index + 1, positionMs: 0 } : { index, positionMs };
  }
  if (step === 'previous') {
    const action = previousAction(positionMs / 1000, index);
    if (action === 'previous') return { index: index - 1, positionMs: 0 };
    return { index, positionMs: 0 };
  }
  return { index, positionMs };
}
