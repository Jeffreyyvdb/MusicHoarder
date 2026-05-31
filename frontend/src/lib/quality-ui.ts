/** Shared colour + label helpers for the AI-quality workbench (cards, list, detail). */

import type { QualityVerdict, QualityCategory, QualityBucketName } from '$lib/api-client';

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
  Wrong: 'bg-red-500',
  Questionable: 'bg-amber-500',
  Good: 'bg-teal-500',
  Excellent: 'bg-emerald-500',
  Ungradeable: 'bg-muted-foreground/40'
};

/** Pill tint (bg + text + border) per verdict — matches the /review grade badge. */
export function verdictBadge(v: QualityVerdict | undefined): string {
  switch (v) {
    case 'Excellent':
      return 'bg-emerald-500/15 text-emerald-600 dark:text-emerald-400 border-emerald-500/30';
    case 'Good':
      return 'bg-teal-500/15 text-teal-600 dark:text-teal-400 border-teal-500/30';
    case 'Questionable':
      return 'bg-amber-500/15 text-amber-600 dark:text-amber-400 border-amber-500/30';
    case 'Wrong':
      return 'bg-red-500/15 text-red-600 dark:text-red-400 border-red-500/30';
    default:
      return 'bg-muted text-muted-foreground border-border';
  }
}

export function scoreColor(score: number): string {
  if (score >= 90) return 'text-emerald-600 dark:text-emerald-400';
  if (score >= 70) return 'text-teal-600 dark:text-teal-400';
  if (score >= 40) return 'text-amber-600 dark:text-amber-400';
  return 'text-red-600 dark:text-red-400';
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

/** Text colour for a conflict-card verdict line. */
export function toneText(t: ConflictTone): string {
  switch (t) {
    case 'green':
      return 'text-emerald-600 dark:text-emerald-400';
    case 'teal':
      return 'text-teal-600 dark:text-teal-400';
    case 'amber':
      return 'text-amber-600 dark:text-amber-400';
    case 'red':
      return 'text-red-600 dark:text-red-400';
    default:
      return 'text-muted-foreground';
  }
}

/** Left-border colour for the LLM reasoning block, keyed to the verdict tone. */
export function toneBorder(t: ConflictTone): string {
  switch (t) {
    case 'green':
      return 'border-l-emerald-500';
    case 'teal':
      return 'border-l-teal-500';
    case 'amber':
      return 'border-l-amber-500';
    case 'red':
      return 'border-l-red-500';
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
  { id: 'wrong', label: 'Wrong', dot: 'bg-red-500' },
  { id: 'questionable', label: 'Questionable', dot: 'bg-amber-500' },
  { id: 'good', label: 'Good', dot: 'bg-teal-500' },
  { id: 'excellent', label: 'Excellent', dot: 'bg-emerald-500' },
  { id: 'ungradeable', label: 'Ungradeable', dot: 'bg-muted-foreground/40' }
];
