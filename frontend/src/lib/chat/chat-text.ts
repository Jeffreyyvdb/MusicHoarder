import type { ChatConversation, ChatLink, ChatMessage, ChatPerson } from '$lib/api-client';

/**
 * The pure parts of chat: what a shared link is, how a message reads in the conversation list, how
 * messages group into runs and where "Seen" goes. No stores and no DOM, so the tests pin them.
 * The server makes the same decisions about links (ChatLinkParser.cs) — these only shape display.
 */

const URL_PATTERN = /https?:\/\/[^\s<>"']+/i;
const URL_PATTERN_GLOBAL = /https?:\/\/[^\s<>"']+/gi;
const TRAILING_PUNCTUATION = /[.,;:!?'"’”]+$/;

/** A URL as it sits in running text: trailing punctuation off, and a `)` only when one opened. */
function trimUrl(raw: string): string {
  let url = raw.replace(TRAILING_PUNCTUATION, '');
  const count = (s: string, c: string) => s.split(c).length - 1;
  while (url.endsWith(')') && count(url, ')') > count(url, '(')) url = url.slice(0, -1);
  return url.replace(TRAILING_PUNCTUATION, '');
}

/** The first web address in `text`, or null. */
export function firstUrl(text: string | null | undefined): string | null {
  if (!text) return null;
  const match = URL_PATTERN.exec(text);
  return match ? trimUrl(match[0]) : null;
}

/**
 * What an app handed the share sheet, reduced to the link it is about. Apps disagree on where it
 * goes: Spotify puts "…on Spotify: https://open.spotify.com/…" in `text`, YouTube a bare link in
 * `text`, a browser uses `url` and the page title in `title`.
 */
export function sharedLink(fields: {
  title?: string | null;
  text?: string | null;
  url?: string | null;
}): string | null {
  return firstUrl(fields.url) ?? firstUrl(fields.text) ?? firstUrl(fields.title);
}

export type TextSegment = { kind: 'text'; value: string } | { kind: 'link'; value: string; href: string };

/** A message's text split into plain runs and links, so links render as anchors (never as HTML). */
export function linkSegments(text: string): TextSegment[] {
  const segments: TextSegment[] = [];
  let last = 0;
  for (const match of text.matchAll(URL_PATTERN_GLOBAL)) {
    const start = match.index ?? 0;
    const url = trimUrl(match[0]);
    if (start > last) segments.push({ kind: 'text', value: text.slice(last, start) });
    segments.push({ kind: 'link', value: url, href: url });
    last = start + url.length;
  }
  if (last < text.length) segments.push({ kind: 'text', value: text.slice(last) });
  return segments;
}

/** "Spotify", "YouTube", or the link's host. */
export function linkSource(link: Pick<ChatLink, 'url' | 'provider'>): string {
  if (link.provider === 'spotify') return 'Spotify';
  if (link.provider === 'youtube') {
    try {
      return new URL(link.url).hostname === 'music.youtube.com' ? 'YouTube Music' : 'YouTube';
    } catch {
      return 'YouTube';
    }
  }
  try {
    return new URL(link.url).hostname.replace(/^www\./, '');
  } catch {
    return link.url;
  }
}

const KIND_NOUNS: Record<string, string> = {
  track: 'song',
  album: 'album',
  playlist: 'playlist',
  artist: 'artist',
  episode: 'episode',
  show: 'podcast',
  video: 'video'
};

/** "Spotify · Song", "YouTube · Video", or just the source. */
export function linkCaption(link: Pick<ChatLink, 'url' | 'provider' | 'kind'>): string {
  const noun = link.kind ? KIND_NOUNS[link.kind] : undefined;
  const source = linkSource(link);
  return noun ? `${source} · ${noun[0].toUpperCase()}${noun.slice(1)}` : source;
}

/** What a message says in one line, as the conversation list shows it. */
export function messagePreview(message: ChatMessage | null | undefined): string {
  if (!message) return 'No messages yet';
  const who = message.mine ? 'You: ' : '';
  if (message.kind === 'shareOpened' && message.share) {
    return `You opened ${message.share.scope === 'Album' ? 'an album' : 'a song'}: ${message.share.title}`;
  }
  if (message.text?.trim()) return who + message.text.trim().replace(/\s+/g, ' ');
  if (message.share) {
    const noun = message.share.scope === 'Album' ? 'an album' : 'a song';
    return `${who}${message.mine ? 'Sent' : 'Sent you'} ${noun}: ${message.share.title}`;
  }
  if (message.link) {
    const what = message.link.title ?? linkSource(message.link);
    return `${who}${message.mine ? 'Sent' : 'Sent you'} a link: ${what}`;
  }
  return who.trim();
}

/** A conversation's name: the other people in it, or who it was with once they are gone. */
export function conversationTitle(conversation: Pick<ChatConversation, 'members'>): string {
  const names = conversation.members.map((m) => m.name);
  return names.length === 0 ? 'Deleted account' : names.join(', ');
}

/** One or two letters for an avatar. */
export function initials(person: Pick<ChatPerson, 'name'> | null | undefined): string {
  const words = (person?.name ?? '').trim().split(/[\s._@-]+/).filter(Boolean);
  if (words.length === 0) return '?';
  const letters = words.length === 1 ? words[0].slice(0, 2) : words[0][0] + words[words.length - 1][0];
  return letters.toUpperCase();
}

/** Messages this close together from the same person read as one run (one avatar, tight spacing). */
export const RUN_GAP_MS = 5 * 60_000;

/** Whether `message` continues the run `previous` started. */
export function continuesRun(previous: ChatMessage | undefined, message: ChatMessage): boolean {
  if (!previous || previous.senderId !== message.senderId) return false;
  if (previous.kind === 'shareOpened' || message.kind === 'shareOpened') return false;
  return Date.parse(message.createdAtUtc) - Date.parse(previous.createdAtUtc) < RUN_GAP_MS;
}

/** Whether a day separator goes before `message` (a new local day since `previous`). */
export function startsDay(previous: ChatMessage | undefined, message: ChatMessage): boolean {
  if (!previous) return true;
  return new Date(previous.createdAtUtc).toDateString() !== new Date(message.createdAtUtc).toDateString();
}

/**
 * The id of your message the "Seen" goes under: the newest of yours the other person has read up
 * to, and only when nothing of theirs came after it (their reply already says they saw it).
 */
export function seenMessageId(
  messages: readonly ChatMessage[],
  peerLastReadAtUtc: string | null | undefined
): number | null {
  if (!peerLastReadAtUtc) return null;
  const readUpTo = Date.parse(peerLastReadAtUtc);
  const lastIndex = messages.length - 1;
  if (lastIndex < 0 || !messages[lastIndex].mine) return null;
  for (let i = lastIndex; i >= 0; i--) {
    const m = messages[i];
    if (!m.mine) return null;
    if (Date.parse(m.createdAtUtc) <= readUpTo) return m.id;
  }
  return null;
}

/** Merge a fetched page into what is shown: by id, oldest first, a newer copy winning. */
export function mergeMessages(current: readonly ChatMessage[], incoming: readonly ChatMessage[]): ChatMessage[] {
  const byId = new Map<number, ChatMessage>();
  for (const m of current) byId.set(m.id, m);
  for (const m of incoming) byId.set(m.id, m);
  return [...byId.values()].sort((a, b) => a.id - b.id);
}

const DAY_MS = 86_400_000;

function daysBetween(iso: string, now: Date): number {
  const d = new Date(iso);
  const start = (x: Date) => new Date(x.getFullYear(), x.getMonth(), x.getDate()).getTime();
  return Math.round((start(now) - start(d)) / DAY_MS);
}

/** A day separator in a conversation: Today, Yesterday, a weekday this week, else the date. */
export function dayLabel(iso: string, now: Date = new Date()): string {
  const days = daysBetween(iso, now);
  const d = new Date(iso);
  if (days === 0) return 'Today';
  if (days === 1) return 'Yesterday';
  if (days > 1 && days < 7) return d.toLocaleDateString(undefined, { weekday: 'long' });
  return d.toLocaleDateString(undefined, {
    day: 'numeric',
    month: 'long',
    year: d.getFullYear() === now.getFullYear() ? undefined : 'numeric'
  });
}

/** When, in the conversation list: a time today, Yesterday, a weekday this week, else a short date. */
export function listTimeLabel(iso: string, now: Date = new Date()): string {
  const days = daysBetween(iso, now);
  const d = new Date(iso);
  if (days <= 0) return d.toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
  if (days === 1) return 'Yesterday';
  if (days < 7) return d.toLocaleDateString(undefined, { weekday: 'short' });
  return d.toLocaleDateString(undefined, { day: 'numeric', month: 'short' });
}

export function timeLabel(iso: string): string {
  return new Date(iso).toLocaleTimeString(undefined, { hour: 'numeric', minute: '2-digit' });
}
