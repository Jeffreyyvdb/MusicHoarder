import { describe, expect, it } from 'vitest';
import { albumCompletionSummary, type AlbumCompletionRunResult } from './api-client';

function result(over: Partial<AlbumCompletionRunResult>): AlbumCompletionRunResult {
  return {
    albumsExamined: 0,
    tracksQueued: 0,
    albumsFilled: 0,
    albumsSkipped: 0,
    albumsAlreadyComplete: 0,
    idleReason: null,
    ...over
  };
}

/**
 * A completion pass queueing nothing is a normal outcome, not a failure — every album complete, all
 * of them compilations, or the catalog lookup hasn't caught up. The whole reason this summary exists
 * is that a silent zero is indistinguishable from the feature being broken, which is exactly how it
 * first came across.
 */
describe('albumCompletionSummary', () => {
  it('reports what it queued', () => {
    const s = albumCompletionSummary(result({ tracksQueued: 12, albumsFilled: 3, albumsExamined: 5 }));
    expect(s).toContain('12 missing tracks');
    expect(s).toContain('3 albums');
  });

  it('singularises a single track and album', () => {
    const s = albumCompletionSummary(result({ tracksQueued: 1, albumsFilled: 1, albumsExamined: 1 }));
    expect(s).toContain('1 missing track ');
    expect(s).toContain('1 album.');
  });

  it('explains a zero by naming which kind it is', () => {
    const s = albumCompletionSummary(
      result({ albumsExamined: 4, albumsAlreadyComplete: 3, albumsSkipped: 1 })
    );
    expect(s).toContain('Checked 4 albums');
    expect(s).toContain('3 already complete');
    expect(s).toContain('1 skipped');
  });

  it('still says something useful when a zero has no breakdown', () => {
    expect(albumCompletionSummary(result({ albumsExamined: 2 }))).toBe(
      'Checked 2 albums, nothing to queue.'
    );
  });

  it.each([
    ['downloads-disabled', 'wishlist downloads'],
    ['disabled', 'is off'],
    ['at-pending-ceiling', 'catches up'],
    ['no-canonical-albums', 'try again'],
    ['library-empty', 'no music'],
    ['no-candidates', 'recently']
  ])('turns the %s idle reason into an explanation', (reason, expected) => {
    expect(albumCompletionSummary(result({ idleReason: reason })).toLowerCase()).toContain(expected);
  });

  it('prefers the idle reason over a count breakdown', () => {
    // An idle pass examined nothing, so a "checked 0 albums" message would be actively misleading.
    const s = albumCompletionSummary(result({ idleReason: 'no-candidates', albumsExamined: 0 }));
    expect(s).not.toContain('Checked 0');
  });
});
