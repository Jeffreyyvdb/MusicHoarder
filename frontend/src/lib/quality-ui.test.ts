import { describe, expect, it } from 'vitest';
import {
  QUALITY_BUCKETS,
  VERDICT_DOT,
  VERDICT_TABS,
  classifyBucket,
  issueLabel,
  qualityCategoryLabel,
  scoreColor,
  toneText,
  verdictBadge
} from './quality-ui';

describe('classifyBucket', () => {
  it('mirrors the backend buckets', () => {
    expect(classifyBucket('NeedsReview', 'Excellent')).toBe('flagged');
    expect(classifyBucket('Matched', 'Wrong')).toBe('silent');
    expect(classifyBucket('Matched', 'Questionable')).toBe('silent');
    expect(classifyBucket('Matched', 'Excellent')).toBe('verified');
    expect(classifyBucket('Matched', 'Good')).toBe('other');
    expect(classifyBucket(null, undefined)).toBe('other');
  });
});

describe('status colours', () => {
  it('reads fine grades as plain text and only doubt/failure in a status tone', () => {
    expect(scoreColor(95)).toBe('text-foreground');
    expect(scoreColor(70)).toBe('text-foreground');
    expect(scoreColor(55)).toBe('text-warning-text');
    expect(scoreColor(12)).toBe('text-destructive-text');
    expect(toneText('green')).toBe('text-foreground');
    expect(toneText('red')).toBe('text-destructive-text');
  });

  it('never uses a raw palette hue (they failed contrast in light mode)', () => {
    const classes = [
      ...Object.values(VERDICT_DOT),
      ...(['Excellent', 'Good', 'Questionable', 'Wrong', 'Ungradeable'] as const).map(verdictBadge),
      ...VERDICT_TABS.map((t) => t.dot)
    ].join(' ');
    expect(classes).not.toMatch(/(emerald|teal|amber|red|sky)-\d/);
  });
});

describe('qualityCategoryLabel', () => {
  it('names buckets and verdict tabs alike', () => {
    expect(qualityCategoryLabel('silent')).toBe(QUALITY_BUCKETS[0].label);
    expect(qualityCategoryLabel('all')).toBe('All graded');
    expect(qualityCategoryLabel('wrong')).toBe('Wrong');
  });
});

describe('issueLabel', () => {
  it('turns a grader code into words', () => {
    expect(issueLabel('wrong_recording')).toBe('Wrong recording');
    expect(issueLabel('artist_changed')).toBe('Artist changed');
    expect(issueLabel('year__missing_')).toBe('Year missing');
    expect(issueLabel('')).toBe('');
  });
});
