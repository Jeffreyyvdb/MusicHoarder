import { describe, expect, it } from 'vitest';
import type { ChatMessage } from '$lib/api-client';
import {
  continuesRun,
  dayLabel,
  firstUrl,
  initials,
  linkCaption,
  linkSegments,
  mergeMessages,
  messagePreview,
  seenMessageId,
  sharedLink,
  startsDay
} from './chat-text';

function message(overrides: Partial<ChatMessage> & { id: number }): ChatMessage {
  return {
    conversationId: 'c1',
    senderId: 'me',
    mine: true,
    kind: 'text',
    text: 'hi',
    createdAtUtc: '2026-09-26T12:00:00Z',
    ...overrides
  };
}

describe('sharedLink', () => {
  it('finds the link wherever the sharing app put it', () => {
    // Spotify on Android: boilerplate and the link in `text`, nothing in `url`.
    expect(
      sharedLink({
        title: 'Harder, Better, Faster, Stronger',
        text: 'Check out this song on Spotify: https://open.spotify.com/track/5W3cjX2J3tjhG8zb6u0qHn?si=abc'
      })
    ).toBe('https://open.spotify.com/track/5W3cjX2J3tjhG8zb6u0qHn?si=abc');
    // YouTube: a bare link in `text`.
    expect(sharedLink({ text: 'https://youtu.be/dQw4w9WgXcQ?si=x' })).toBe('https://youtu.be/dQw4w9WgXcQ?si=x');
    // A browser: `url`, with the page title alongside.
    expect(sharedLink({ title: 'A page', url: 'https://example.com/a' })).toBe('https://example.com/a');
    expect(sharedLink({ title: 'no link', text: 'none either' })).toBeNull();
  });

  it('leaves trailing punctuation and unmatched brackets out of a link', () => {
    expect(firstUrl('see (https://example.com/a).')).toBe('https://example.com/a');
    expect(firstUrl('https://en.wikipedia.org/wiki/Air_(band), great')).toBe(
      'https://en.wikipedia.org/wiki/Air_(band)'
    );
  });
});

describe('linkSegments', () => {
  it('splits text around its links without losing a character', () => {
    const text = 'listen: https://youtu.be/abc, then https://example.com/x!';
    const segments = linkSegments(text);
    expect(segments).toEqual([
      { kind: 'text', value: 'listen: ' },
      { kind: 'link', value: 'https://youtu.be/abc', href: 'https://youtu.be/abc' },
      { kind: 'text', value: ', then ' },
      { kind: 'link', value: 'https://example.com/x', href: 'https://example.com/x' },
      { kind: 'text', value: '!' }
    ]);
    expect(segments.map((s) => s.value).join('')).toBe(text);
  });

  it('is plain text when there is no link', () => {
    expect(linkSegments('<b>not html</b>')).toEqual([{ kind: 'text', value: '<b>not html</b>' }]);
  });
});

describe('linkCaption', () => {
  it('names the source and what the link is', () => {
    expect(linkCaption({ url: 'https://open.spotify.com/track/x', provider: 'spotify', kind: 'track' })).toBe(
      'Spotify · Song'
    );
    expect(linkCaption({ url: 'https://music.youtube.com/watch?v=x', provider: 'youtube', kind: 'video' })).toBe(
      'YouTube Music · Video'
    );
    expect(linkCaption({ url: 'https://www.example.com/a', provider: null, kind: null })).toBe('example.com');
  });
});

describe('messagePreview', () => {
  it('says who wrote it and what was sent', () => {
    expect(messagePreview(message({ id: 1, text: 'Hello\nthere' }))).toBe('You: Hello there');
    expect(messagePreview(message({ id: 1, mine: false, text: 'Yo' }))).toBe('Yo');
    expect(
      messagePreview(
        message({
          id: 1,
          mine: false,
          kind: 'share',
          text: null,
          share: {
            token: 't',
            scope: 'Album',
            songId: 1,
            title: 'Discovery',
            hasCover: true,
            revoked: false,
            ownedByViewer: false
          }
        })
      )
    ).toBe('Sent you an album: Discovery');
    expect(
      messagePreview(
        message({
          id: 1,
          kind: 'link',
          text: null,
          link: { url: 'https://youtu.be/x', provider: 'youtube', kind: 'video', title: 'Around the World' }
        })
      )
    ).toBe('You: Sent a link: Around the World');
    expect(messagePreview(null)).toBe('No messages yet');
  });
});

describe('runs and days', () => {
  it('joins one person’s messages within five minutes into a run', () => {
    const a = message({ id: 1, createdAtUtc: '2026-09-26T12:00:00Z' });
    expect(continuesRun(a, message({ id: 2, createdAtUtc: '2026-09-26T12:04:00Z' }))).toBe(true);
    expect(continuesRun(a, message({ id: 2, createdAtUtc: '2026-09-26T12:06:00Z' }))).toBe(false);
    expect(continuesRun(a, message({ id: 2, senderId: 'them', mine: false }))).toBe(false);
    expect(continuesRun(undefined, a)).toBe(false);
  });

  it('starts a day at the first message and at every new day', () => {
    const a = message({ id: 1, createdAtUtc: '2026-09-20T12:00:00Z' });
    expect(startsDay(undefined, a)).toBe(true);
    expect(startsDay(a, message({ id: 2, createdAtUtc: '2026-09-20T12:30:00Z' }))).toBe(false);
    expect(startsDay(a, message({ id: 2, createdAtUtc: '2026-09-23T12:30:00Z' }))).toBe(true);
  });
});

describe('seenMessageId', () => {
  const mine1 = message({ id: 1, createdAtUtc: '2026-09-26T12:00:00Z' });
  const mine2 = message({ id: 2, createdAtUtc: '2026-09-26T12:01:00Z' });
  const theirs = message({ id: 3, mine: false, senderId: 'them', createdAtUtc: '2026-09-26T12:02:00Z' });

  it('goes under the newest message of yours they have read', () => {
    expect(seenMessageId([mine1, mine2], '2026-09-26T12:05:00Z')).toBe(2);
    expect(seenMessageId([mine1, mine2], '2026-09-26T12:00:30Z')).toBe(1);
    expect(seenMessageId([mine1, mine2], '2026-09-26T11:00:00Z')).toBeNull();
    expect(seenMessageId([mine1, mine2], null)).toBeNull();
  });

  it('is not shown once they have replied', () => {
    expect(seenMessageId([mine1, theirs], '2026-09-26T12:05:00Z')).toBeNull();
  });
});

describe('mergeMessages', () => {
  it('keeps one copy of each message, oldest first', () => {
    const merged = mergeMessages(
      [message({ id: 2 }), message({ id: 1 })],
      [message({ id: 3 }), message({ id: 2, text: 'edited' })]
    );
    expect(merged.map((m) => m.id)).toEqual([1, 2, 3]);
    expect(merged[1].text).toBe('edited');
  });
});

describe('initials', () => {
  it('takes the first and last word, or two letters of one', () => {
    expect(initials({ name: 'Jeffrey van den Berg' })).toBe('JB');
    expect(initials({ name: 'alice' })).toBe('AL');
    expect(initials({ name: '' })).toBe('?');
  });
});

describe('dayLabel', () => {
  it('names recent days and dates older ones', () => {
    const now = new Date(2026, 8, 26, 15, 0);
    expect(dayLabel(new Date(2026, 8, 26, 9, 0).toISOString(), now)).toBe('Today');
    expect(dayLabel(new Date(2026, 8, 25, 23, 0).toISOString(), now)).toBe('Yesterday');
    expect(dayLabel(new Date(2026, 8, 1, 12, 0).toISOString(), now)).not.toMatch(/Today|Yesterday/);
  });
});
