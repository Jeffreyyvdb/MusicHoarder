import { describe, expect, it } from 'vitest';
import { reasonFor, confidencePercent } from './review-helpers';

describe('reasonFor', () => {
  it('returns no_fingerprint when the fingerprint is missing', () => {
    expect(reasonFor({ fingerprint: null, matchWarnings: [], matchConfidence: 0.5 }).key).toBe(
      'no_fingerprint'
    );
    expect(reasonFor({ fingerprint: '', matchWarnings: [], matchConfidence: 0.5 }).key).toBe(
      'no_fingerprint'
    );
  });

  it('returns multiple_matches when more than one warning is present', () => {
    const r = reasonFor({ fingerprint: 'abc', matchWarnings: ['a', 'b'], matchConfidence: 0.5 });
    expect(r.key).toBe('multiple_matches');
  });

  it('returns low_confidence when confidence is below 0.7', () => {
    const r = reasonFor({ fingerprint: 'abc', matchWarnings: [], matchConfidence: 0.42 });
    expect(r.key).toBe('low_confidence');
  });

  it('returns below_threshold (not low_confidence) when confidence is >= 0.7', () => {
    // The regression this plan fixes: the old unconditional fallback labelled every
    // mid-confidence row "Low confidence". A >= 0.7 row is now a distinct reason.
    const r = reasonFor({ fingerprint: 'abc', matchWarnings: [], matchConfidence: 0.82 });
    expect(r.key).toBe('below_threshold');
    expect(r.label).toBe('Needs review');
  });

  it('returns below_threshold when confidence is unknown but a fingerprint exists', () => {
    const r = reasonFor({ fingerprint: 'abc', matchWarnings: [], matchConfidence: null });
    expect(r.key).toBe('below_threshold');
  });
});

describe('confidencePercent', () => {
  it('rounds to a whole-percent string', () => {
    expect(confidencePercent({ matchConfidence: 0.825 })).toBe('83%');
    expect(confidencePercent({ matchConfidence: 0.9 })).toBe('90%');
  });

  it('returns null when confidence is unknown', () => {
    expect(confidencePercent({ matchConfidence: null })).toBeNull();
    expect(confidencePercent({ matchConfidence: undefined })).toBeNull();
  });
});
