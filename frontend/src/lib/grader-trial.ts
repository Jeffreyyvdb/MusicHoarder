// Display rules for the grader trial page (Manage → Grader trial). Pure, so they are tested on
// their own; the numbers themselves come from GET /api/quality/trial/report.

import type { GraderTrialCheckStatus, GraderTrialComparison } from '#lib/api-client.ts';

/** A 0–1 share as a percentage, one decimal under 10%; an em dash when unknown. */
export function pct(v: number | null | undefined): string {
  if (v == null || Number.isNaN(v)) return '—';
  const p = v * 100;
  return `${p < 10 && p > 0 ? p.toFixed(1) : Math.round(p)}%`;
}

/** Milliseconds as "180 ms" or "12.4 s". */
export function ms(v: number | null | undefined): string {
  if (v == null) return '—';
  return v < 1000 ? `${Math.round(v)} ms` : `${(v / 1000).toFixed(1)} s`;
}

/** USD with enough digits to tell fractions of a cent apart. */
export function usd(v: number | null | undefined): string {
  if (v == null) return '—';
  if (v === 0) return '$0';
  if (v >= 1) return `$${v.toFixed(2)}`;
  if (v >= 0.01) return `$${v.toFixed(3)}`;
  return `$${v.toPrecision(2)}`;
}

/** A plain number with up to `digits` decimals; an em dash when unknown. */
export function num(v: number | null | undefined, digits = 2): string {
  if (v == null || Number.isNaN(v)) return '—';
  return Number(v.toFixed(digits)).toString();
}

/** How many times cheaper or faster `b` is than `a` ("6.7×"), or null when either is missing. */
export function ratio(a: number | null | undefined, b: number | null | undefined): string | null {
  if (a == null || b == null || b <= 0) return null;
  const r = a / b;
  return `${r >= 10 ? Math.round(r) : r.toFixed(1)}×`;
}

const RECOMMENDATIONS: Record<GraderTrialComparison['recommendation'], { title: string; detail: string }> = {
  switch: {
    title: 'Switch to this model',
    detail: 'Every check passes. Plan the two-tier grader: this model decides, the chat model writes summaries for flagged songs.'
  },
  'keep-incumbent': {
    title: 'Keep the chat grader',
    detail: 'This model misses wrong songs the chat grader catches. Use it at most as a cheap pre-filter.'
  },
  'need-more-data': {
    title: 'Not enough data yet',
    detail: 'Keep both graders running and label more songs below.'
  },
  'rerun-with-fewer-questions': {
    title: 'Quality holds, cost or speed does not',
    detail: 'Try again with fewer questions per request.'
  }
};

export function recommendationText(r: GraderTrialComparison['recommendation']): { title: string; detail: string } {
  return RECOMMENDATIONS[r] ?? RECOMMENDATIONS['need-more-data'];
}

const CHECK_LABELS: Record<string, string> = {
  quality: 'Catches wrong songs',
  unsupported_identity: 'Catches unsupported identities',
  latency: 'Faster at p95',
  cost: 'Cheaper per 1,000',
  coverage: 'Dossiers fit'
};

export function checkLabel(name: string): string {
  return CHECK_LABELS[name] ?? name;
}

/** Status pill tint: success is a word plus a neutral tint, never green text (see the HIG notes). */
export function checkTone(status: GraderTrialCheckStatus): string {
  switch (status) {
    case 'pass':
      return 'bg-muted text-foreground';
    case 'fail':
      return 'bg-destructive/10 text-destructive-text dark:bg-destructive/12';
    default:
      return 'bg-warning/6 text-warning-text dark:bg-warning/15';
  }
}

export function checkWord(status: GraderTrialCheckStatus): string {
  return status === 'pass' ? 'Pass' : status === 'fail' ? 'Fail' : 'Too few';
}

/** A model id without its maker prefix and date suffix: "typesafe/jev-1.14-20261014" → "jev-1.14". */
export function shortModel(model: string): string {
  const name = model.includes('/') ? model.slice(model.indexOf('/') + 1) : model;
  return name.replace(/-\d{8}$/, '');
}
