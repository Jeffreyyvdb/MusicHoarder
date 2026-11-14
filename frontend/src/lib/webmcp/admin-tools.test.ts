import { describe, expect, it, vi } from 'vitest';
import type { ApiSong, EnrichmentDetail } from '#lib/api-client.ts';
import { createInboxTools, type InboxDeps } from './admin-tools.ts';

const waiting = {
  id: 11,
  fileName: 'track01.mp3',
  title: 'Track 01',
  artist: 'Unknown',
  album: null,
  hasFingerprint: true,
  matchConfidence: 0.55,
  matchWarnings: ['title_mismatch']
} as unknown as ApiSong;

const detail = {
  id: 11,
  fileName: 'track01.mp3',
  enrichmentStatus: 'NeedsReview',
  matchConfidence: 0.55,
  matchWarnings: ['title_mismatch'],
  source: { title: 'Track 01', artist: 'Unknown' },
  current: { title: 'Track 01', artist: 'Unknown' },
  providerAttempts: [
    {
      provider: 'SpotifyAPI',
      status: 'Matched',
      attemptedAtUtc: '2026-11-01T00:00:00Z',
      candidate: {
        title: 'Money Trees',
        artist: 'Kendrick Lamar',
        album: 'good kid',
        year: 2012,
        matchConfidence: 0.6
      }
    },
    {
      provider: 'AcoustID',
      status: 'Matched',
      attemptedAtUtc: '2026-11-01T00:00:00Z',
      candidate: {
        title: 'Money Trees',
        artist: 'Kendrick Lamar',
        album: 'good kid, m.A.A.d city',
        year: 2012,
        matchConfidence: 0.9
      }
    }
  ]
} as unknown as EnrichmentDetail;

function setup(isAdmin = true) {
  const deps: InboxDeps = {
    counts: vi.fn(async () => ({
      review: 1,
      dupes: 2,
      ai: 3,
      artists: 4,
      albums: 5,
      failed: 6,
      total: 6
    })),
    reviewQueue: vi.fn(async () => [waiting]),
    enrichmentDetail: vi.fn(async () => detail),
    submitReview: vi.fn(async (id, request) => ({
      id,
      fileName: 'track01.mp3',
      decision: request.decision,
      enrichmentStatus: 1,
      libraryBuildStatus: 0,
      title: request.title ?? 'Track 01',
      artist: request.artist ?? 'Unknown'
    })),
    afterDecision: vi.fn(),
    isAdmin
  };
  const tools = createInboxTools(deps);
  const call = (name: string, input: Record<string, unknown> = {}) => {
    const tool = tools.find((t) => t.name === name);
    if (!tool) throw new Error(`no tool ${name}`);
    return tool.execute(input, { signal: new AbortController().signal }) as Promise<
      Record<string, unknown>
    >;
  };
  return { deps, tools, call };
}

describe('Inbox tools', () => {
  it('gives demo the read tools and no decision tool', () => {
    const demo = setup(false).tools.map((t) => t.name);
    expect(demo).toEqual(['get_inbox_summary', 'list_tag_review', 'get_tag_review_item']);
    expect(setup(true).tools.map((t) => t.name)).toContain('decide_tag_review');
  });

  it('marks the decision consequential, so an agent confirms with the user first', () => {
    const decide = setup().tools.find((t) => t.name === 'decide_tag_review')!;
    expect(decide.annotations).toEqual({ consequentialHint: true });
  });

  it('summarises the queues, the name queues for an admin only', async () => {
    const admin = setup(true);
    expect(await admin.call('get_inbox_summary')).toEqual({
      tagReview: 1,
      duplicateTracks: 2,
      aiFlagged: 3,
      artistNames: 4,
      albumNames: 5,
      failedMatches: 6,
      badgeTotal: 6
    });
    expect(admin.deps.counts).toHaveBeenCalledWith(true);
    const demo = setup(false);
    expect(await demo.call('get_inbox_summary')).not.toHaveProperty('artistNames');
    expect(demo.deps.counts).toHaveBeenCalledWith(false);
  });

  it('lists the Tag review queue with the reason each track waits', async () => {
    const { call } = setup();
    expect(await call('list_tag_review')).toEqual({
      total: 1,
      items: [
        {
          songId: 11,
          fileName: 'track01.mp3',
          title: 'Track 01',
          artist: 'Unknown',
          album: null,
          confidence: 0.55,
          reason: 'Low confidence'
        }
      ]
    });
  });

  it('shows one item with its candidates best first', async () => {
    const { call } = setup();
    const item = await call('get_tag_review_item', { songId: 11 });
    expect(item).toMatchObject({ songId: 11, status: 'NeedsReview', warnings: ['title_mismatch'] });
    expect(
      (item.candidates as { provider: string; score: number }[]).map((c) => c.provider)
    ).toEqual(['AcoustID', 'Spotify']);
  });

  it('approves with corrected tags and tells the page', async () => {
    const { call, deps } = setup();
    const result = await call('decide_tag_review', {
      songId: 11,
      decision: 'approve',
      title: 'Money Trees',
      artist: 'Kendrick Lamar',
      year: '2012'
    });
    expect(deps.submitReview).toHaveBeenCalledWith(11, {
      decision: 'approve',
      title: 'Money Trees',
      artist: 'Kendrick Lamar',
      year: 2012
    });
    expect(deps.afterDecision).toHaveBeenCalledWith(11);
    expect(result).toMatchObject({ songId: 11, decision: 'approve', title: 'Money Trees' });
  });

  it('rejects without sending tag fields, and refuses a track that is not waiting', async () => {
    const { call, deps } = setup();
    await call('decide_tag_review', { songId: 11, decision: 'reject', title: 'ignored' });
    expect(deps.submitReview).toHaveBeenCalledWith(11, { decision: 'reject' });
    expect(await call('decide_tag_review', { songId: 12, decision: 'approve' })).toEqual({
      error: 'Song 12 is not in the Tag review queue.'
    });
    expect(await call('decide_tag_review', { songId: 11, decision: 'maybe' })).toHaveProperty(
      'error'
    );
    expect(deps.submitReview).toHaveBeenCalledTimes(1);
  });
});
