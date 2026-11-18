import type { LaunchAction } from './widgets.ts';

export interface LaunchDeps {
  /** The song the player has loaded or remembers, or null. */
  loadedSongId: () => number | null;
  isPlaying: () => boolean;
  openNowPlaying: (songId: number) => void;
  /** The mini player's Play: a play intent, which claims the session. */
  play: () => void;
  open: (path: string) => void;
}

/** A widget's Play on a fresh page waits this long for the reload snapshot or Connect session. */
const READY_WAIT_MS = 10_000;
const READY_POLL_MS = 250;

/** Do what a tap on a widget asked. Returns a cancel for a Play still waiting for something to resume. */
export function runLaunchAction(action: LaunchAction, deps: LaunchDeps): () => void {
  if (action.kind === 'open') {
    deps.open(action.path);
    return () => {};
  }
  if (action.kind === 'now-playing') {
    const id = deps.loadedSongId();
    if (id != null) deps.openNowPlaying(id);
    return () => {};
  }
  const resume = () => {
    if (!deps.isPlaying()) deps.play();
  };
  if (deps.loadedSongId() != null) {
    resume();
    return () => {};
  }
  // Nothing to resume yet: the page just started. With nothing after the wait, the app is simply
  // open and the person picks a song.
  const startedAt = Date.now();
  // A local wait on the player's own state, not a poll of the server.
  // eslint-disable-next-line local/no-bare-poll
  const timer = setInterval(() => {
    if (deps.loadedSongId() != null) {
      clearInterval(timer);
      resume();
    } else if (Date.now() - startedAt >= READY_WAIT_MS) {
      clearInterval(timer);
    }
  }, READY_POLL_MS);
  return () => clearInterval(timer);
}
