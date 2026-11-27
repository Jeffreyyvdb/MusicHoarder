import { describe, expect, it } from 'vitest';
import type { CleanEditItem, CleanEditProgress, CleanEditVerdict } from '#lib/api-client.ts';
import { clock, evidenceLine, notCheckedCount, progressLine, verdictLabel } from './clean-edits.ts';

const item = (over: Partial<CleanEditItem>): CleanEditItem => ({
  songId: 1,
  artist: 'Kanye West',
  title: 'Stronger',
  verdict: 'cleanMaster',
  margin: 0.68,
  editedSeconds: 0.26,
  references: [
    { edition: 'clean', id: 'itunes:1442846328', correlation: 0.91 },
    { edition: 'explicit', id: 'deezer:1178682', correlation: 0.94 },
    { edition: 'explicit', id: 'deezer:630827242', correlation: 0.89 }
  ],
  mutedAtSeconds: [],
  checkedAtUtc: '2026-11-27T10:00:00Z',
  ...over
});

const progress = (over: Partial<CleanEditProgress>): CleanEditProgress => ({
  running: false,
  stopping: false,
  total: 0,
  processed: 0,
  checked: 0,
  skipped: 0,
  failed: 0,
  ...over
});

describe('clean edits', () => {
  it('says which test flagged a clean master, how decisively, and from which catalogs', () => {
    expect(evidenceLine(item({}))).toBe(
      'Follows the clean edition · 68% closer to it · 0.3 s differ · Deezer and iTunes previews'
    );
  });

  it('lists where words are muted once per second and counts the ones it leaves out', () => {
    const muted = item({
      verdict: 'mutedWords',
      references: [{ edition: 'explicit', id: 'deezer:4232303', correlation: 0.96 }],
      mutedAtSeconds: [53, 53.5, 64.3, 70, 80, 91]
    });

    expect(evidenceLine(muted)).toBe(
      'Muted at 0:53, 1:04, 1:10, 1:20 and 1 more · Deezer previews'
    );
  });

  it('counts the songs that have no check yet', () => {
    const counts = { cleanMaster: 2, explicit: 5, failed: 1 } as Record<CleanEditVerdict, number>;

    expect(notCheckedCount(counts, 20)).toBe(12);
    expect(notCheckedCount(counts, 3)).toBe(0);
  });

  it('writes a clock time', () => {
    expect(clock(0)).toBe('0:00');
    expect(clock(64.3)).toBe('1:04');
    expect(clock(3725)).toBe('62:05');
  });

  it('shows a run while it goes, and why it ended early', () => {
    expect(
      progressLine(progress({ running: true, total: 1200, processed: 34, current: 'A - B' }))
    ).toBe('Checking 34 of 1,200 · A - B');
    expect(progressLine(progress({ running: true, stopping: true, total: 10, processed: 3 }))).toBe(
      'Stopping · 3 of 10'
    );
    expect(progressLine(progress({ stoppedReason: 'Stopped.' }))).toBe('Stopped.');
    expect(progressLine(progress({}))).toBeNull();
  });

  it('names every verdict the server can send', () => {
    const wire: CleanEditVerdict[] = [
      'explicit',
      'cleanMaster',
      'mutedWords',
      'inconclusive',
      'noCleanEdition',
      'noReference',
      'failed'
    ];

    for (const verdict of wire) expect(verdictLabel(verdict)).not.toBe(verdict);
  });
});
