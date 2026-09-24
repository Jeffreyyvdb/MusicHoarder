/** Shared colour + label helpers for the AI-quality workbench (cards, list, detail). */

import type { QualityVerdict, QualityCategory, QualityBucketName } from '$lib/api-client';

// Colour here follows the app's status rule: red means wrong, the warning tone means "look at
// this", and everything that is fine reads as plain text — the brand tint is kept for things you
// can tap, so an Excellent grade is a green DOT, never green text. Text tones are the contrast-
// checked tokens (--destructive-text, --warning-text); the old per-hue teal/emerald/amber-600
// utilities fell to 2.5–3.2:1 on the light page. Colour is never the only carrier: every verdict
// also shows its word, glyph or score.

/** Client-side mirror of the backend QualityBuckets.Classify (keeps the detail view fresh after re-grade). */
export function classifyBucket(
  enrichmentStatusAtGrade: string | null | undefined,
  verdict: QualityVerdict | undefined
): QualityBucketName {
  if (enrichmentStatusAtGrade?.toLowerCase() === 'needsreview') return 'flagged';
  if (enrichmentStatusAtGrade?.toLowerCase() === 'matched') {
    if (verdict === 'Wrong' || verdict === 'Questionable') return 'silent';
    if (verdict === 'Excellent') return 'verified';
  }
  return 'other';
}

/** Solid dot/segment background per verdict (Tailwind class). */
export const VERDICT_DOT: Record<QualityVerdict, string> = {
  Wrong: 'bg-destructive',
  Questionable: 'bg-warning',
  // Good sits between the tint and neutral: the chart teal, which only ever means "good" here.
  Good: 'bg-chart-2',
  Excellent: 'bg-primary',
  Ungradeable: 'bg-muted-foreground-dim'
};

/**
 * Pill tint (bg + text + border) per verdict — the Badge's own semantic tints, so a verdict pill
 * reads the same as every other status capsule. Callers add `border` for the shape.
 */
export function verdictBadge(v: QualityVerdict | undefined): string {
  switch (v) {
    case 'Excellent':
    case 'Good':
      return 'bg-muted text-foreground border-transparent';
    case 'Questionable':
      return 'bg-warning/6 text-warning-text dark:bg-warning/15 border-transparent';
    case 'Wrong':
      return 'bg-destructive/10 text-destructive-text dark:bg-destructive/12 border-transparent';
    default:
      return 'bg-muted text-muted-foreground border-transparent';
  }
}

/** Text tone for a 0–100 grade: plain when fine, the warning tone when doubtful, red when wrong. */
export function scoreColor(score: number): string {
  if (score >= 70) return 'text-foreground';
  if (score >= 40) return 'text-warning-text';
  return 'text-destructive-text';
}

export type ConflictTone = 'green' | 'teal' | 'amber' | 'red' | 'gray';

export function verdictTone(v: QualityVerdict | undefined): ConflictTone {
  switch (v) {
    case 'Excellent':
      return 'green';
    case 'Good':
      return 'teal';
    case 'Questionable':
      return 'amber';
    case 'Wrong':
      return 'red';
    default:
      return 'gray';
  }
}

/** Text colour for a conflict-card verdict line. The good tones are plain text (see above). */
export function toneText(t: ConflictTone): string {
  switch (t) {
    case 'green':
    case 'teal':
      return 'text-foreground';
    case 'amber':
      return 'text-warning-text';
    case 'red':
      return 'text-destructive-text';
    default:
      return 'text-muted-foreground';
  }
}

/** Glyph colour beside a verdict line: the good tones may carry the tint on a glyph. */
export function toneGlyph(t: ConflictTone): string {
  switch (t) {
    case 'green':
      return 'text-primary';
    case 'teal':
      return 'text-chart-2';
    default:
      return toneText(t);
  }
}

/** Left-border colour for the LLM reasoning block, keyed to the verdict tone. */
export function toneBorder(t: ConflictTone): string {
  switch (t) {
    case 'green':
      return 'border-l-primary';
    case 'teal':
      return 'border-l-chart-2';
    case 'amber':
      return 'border-l-warning';
    case 'red':
      return 'border-l-destructive';
    default:
      return 'border-l-border';
  }
}

/** Short glyph shown before an AI verdict ("✓"/"✗"/"?"). */
export function verdictGlyph(v: QualityVerdict | undefined): string {
  switch (v) {
    case 'Excellent':
    case 'Good':
      return '✓';
    case 'Wrong':
      return '✗';
    case 'Questionable':
      return '?';
    default:
      return '—';
  }
}

/** The category tabs below the three big buckets. */
export const VERDICT_TABS: { id: QualityCategory; label: string; dot: string }[] = [
  { id: 'all', label: 'All graded', dot: '' },
  { id: 'wrong', label: 'Wrong', dot: VERDICT_DOT.Wrong },
  { id: 'questionable', label: 'Questionable', dot: VERDICT_DOT.Questionable },
  { id: 'good', label: 'Good', dot: VERDICT_DOT.Good },
  { id: 'excellent', label: 'Excellent', dot: VERDICT_DOT.Excellent },
  { id: 'ungradeable', label: 'Ungradeable', dot: VERDICT_DOT.Ungradeable }
];

/**
 * The three workbench buckets, with the explanation that used to live only in a hover tooltip —
 * a finger never hovers, so it is now the row's second line on a phone and the card's on a desk.
 */
export const QUALITY_BUCKETS: {
  id: 'silent' | 'flagged' | 'verified';
  label: string;
  explain: string;
}[] = [
  {
    id: 'silent',
    label: 'Silent failures',
    explain: 'Auto-accepted, but the AI grader rates them wrong or doubtful.'
  },
  {
    id: 'flagged',
    label: 'Algorithm flagged',
    explain: 'Not confident enough to auto-accept — yours to decide.'
  },
  {
    id: 'verified',
    label: 'Verified clean',
    explain: 'Every provider agreed and the AI graded it excellent.'
  }
];

/** Display name of any list category (the buckets and the verdict tabs share one selector). */
export function qualityCategoryLabel(c: QualityCategory): string {
  return (
    QUALITY_BUCKETS.find((b) => b.id === c)?.label ??
    VERDICT_TABS.find((t) => t.id === c)?.label ??
    c
  );
}

/**
 * A grader issue code as people read it: "wrong_recording" → "Wrong recording". Every list chip,
 * detail row and filter pill uses it; the raw code stays one step away (a hover title, the copied
 * dossier), where a developer or an AI assistant is the reader.
 */
export function issueLabel(code: string): string {
  const s = code.replace(/_/g, ' ').replace(/\s+/g, ' ').trim();
  return s.charAt(0).toUpperCase() + s.slice(1);
}
