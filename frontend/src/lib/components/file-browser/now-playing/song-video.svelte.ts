import { untrack } from 'svelte';
import {
  deleteSongVideo,
  fetchSongVideo,
  getSongVideoInfo,
  getSongVideoInfoUntilSettled,
  resetSongVideoOffset,
  setSongVideoOffset,
  type SongVideoInfo
} from '$lib/api-client';

/** How often a server-side fetch is polled until it settles (they take ~30–60s). */
const POLL_MS = 3000;

/**
 * The music video attached to the song Now Playing shows: its status and sync offset, loaded
 * once and shared by everything that needs it — the backdrop behind the player, the Video mode,
 * the ⋯ menu's Music video section and the Manage video sheet. It used to be loaded twice (by the
 * panel and by the backdrop), and the admin actions lived in the backdrop's floating popover; one
 * owner means a nudge moves the backdrop and the watch view together, live.
 *
 * The owner (SongDetailHost) drives it from two effects:
 *   $effect(() => (id == null ? undefined : video.load(id)));
 *   $effect(() => (video.info?.status === 'Fetching' ? video.poll() : undefined));
 */
export class SongVideo {
  songId = $state<number | null>(null);
  info = $state<SongVideoInfo | null>(null);
  /** The first load has a definitive answer (a record, or none attached). */
  settled = $state(false);
  /** The info load keeps failing and is retrying in the background. Unknown is NOT "none". */
  infoUnavailable = $state(false);
  /** Local mirror of info.syncOffsetMs, moved optimistically by a nudge. */
  offsetMs = $state(0);
  /**
   * Bumped when a refetch settles: the backdrop and the watch view give the new file a clean
   * slate even when the old one had failed or ended.
   */
  generation = $state(0);
  busy = $state(false);

  /** Ready and its file is actually on disk — fileMissing means the stream endpoint would 404. */
  get playable(): boolean {
    return this.info?.status === 'Ready' && !this.info.fileMissing;
  }

  /**
   * Load (and reload on song change) the video info. The load only settles on a definitive answer
   * (info, or a 404 meaning none attached) and retries everything else until aborted — a
   * transient failure after a page refresh must read as "unavailable", never as "no video, fetch
   * it again". Returns the abort, for the owner's effect cleanup.
   */
  load(id: number): () => void {
    untrack(() => {
      this.songId = id;
      this.info = null;
      this.settled = false;
      this.infoUnavailable = false;
      this.offsetMs = 0;
    });
    const abort = new AbortController();
    getSongVideoInfoUntilSettled(id, {
      signal: abort.signal,
      onRetry: () => {
        if (!abort.signal.aborted) this.infoUnavailable = true;
      }
    }).then(
      (result) => {
        if (abort.signal.aborted) return;
        this.infoUnavailable = false;
        this.info = result;
        this.offsetMs = result?.syncOffsetMs ?? 0;
        this.settled = true;
      },
      () => {} // rejects only on abort
    );
    return () => abort.abort();
  }

  /** While a fetch runs server-side, poll until it settles. Returns the stop, for the effect. */
  poll(): () => void {
    const id = untrack(() => this.songId);
    const timer = setInterval(() => {
      if (id === null) return;
      getSongVideoInfo(id).then(
        (result) => {
          if (this.songId !== id) return;
          if (result && result.status !== 'Fetching') {
            this.info = result;
            this.offsetMs = result.syncOffsetMs;
            this.generation += 1;
          }
        },
        () => {}
      );
    }, POLL_MS);
    return () => clearInterval(timer);
  }

  /** Start a download: the auto search, a pasted URL, or a candidate the owner picked. */
  async fetch(url?: string): Promise<boolean> {
    const id = this.songId;
    if (id === null) return false;
    this.busy = true;
    try {
      const info = await fetchSongVideo(id, url);
      if (this.songId === id) this.info = info;
      return true;
    } catch {
      return false; // surfaced via info.lastError on the next poll
    } finally {
      this.busy = false;
    }
  }

  async nudge(deltaMs: number): Promise<void> {
    const id = this.songId;
    if (id === null) return;
    const next = this.offsetMs + deltaMs;
    this.offsetMs = next; // optimistic — nudging should feel live
    try {
      const info = await setSongVideoOffset(id, next);
      if (this.songId !== id) return;
      this.info = info;
      this.offsetMs = info.syncOffsetMs;
    } catch {
      if (this.songId === id) this.offsetMs = this.info?.syncOffsetMs ?? 0;
    }
  }

  async resetAuto(): Promise<void> {
    const id = this.songId;
    if (id === null) return;
    try {
      const info = await resetSongVideoOffset(id);
      if (this.songId !== id) return;
      this.info = info;
      this.offsetMs = info.syncOffsetMs;
    } catch {
      /* keep current */
    }
  }

  async remove(): Promise<void> {
    const id = this.songId;
    if (id === null) return;
    this.busy = true;
    try {
      await deleteSongVideo(id);
      if (this.songId !== id) return;
      this.info = null;
      this.offsetMs = 0;
    } finally {
      this.busy = false;
    }
  }
}

function formatOffset(ms: number): string {
  const sign = ms < 0 ? '−' : '+';
  return `${sign}${(Math.abs(ms) / 1000).toFixed(1)}s`;
}

export { formatOffset as formatVideoOffset };

/** "Auto-aligned +0.3s · 92%", "Manual −1.0s", … — how the clip is lined up with the audio. */
export function videoSyncLabel(info: SongVideoInfo | null): string {
  if (!info) return '';
  switch (info.syncSource) {
    case 'SameSource':
      return 'Synced (same source)';
    case 'AutoAligned':
      return `Auto-aligned ${formatOffset(info.syncOffsetMs)}${info.syncConfidence != null ? ` · ${Math.round(info.syncConfidence * 100)}%` : ''}`;
    case 'Manual':
      return `Manual ${formatOffset(info.syncOffsetMs)}`;
    default:
      return 'Not aligned';
  }
}

/**
 * The one-line status every role sees when a video record exists but cannot play (fetching,
 * file missing, failed), or while its status cannot be read. Null when there is nothing to say.
 */
export function videoProblem(
  info: SongVideoInfo | null,
  infoUnavailable: boolean
): { text: string; tone: 'muted' | 'destructive'; busy?: boolean } | null {
  if (info?.status === 'Fetching')
    return { text: 'Fetching the video…', tone: 'muted', busy: true };
  if (info?.status === 'Ready' && info.fileMissing)
    return { text: 'The video file is missing', tone: 'destructive' };
  if (info?.status === 'Failed') return { text: 'The video download failed', tone: 'destructive' };
  if (!info && infoUnavailable)
    return { text: 'Video status unavailable — retrying', tone: 'muted', busy: true };
  return null;
}
