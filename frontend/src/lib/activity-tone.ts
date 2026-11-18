import type { ApiOverviewActivity } from '#lib/api-client.ts';

/**
 * Shared colour mapping for `ApiOverviewActivity.kind`, used by both
 * PipelineLogRow (the drawer's live log) and PipelineHomeV2 (the pipeline
 * page's Recent activity) so the same event kind always reads the same
 * colour regardless of which panel is showing it, in both themes.
 *
 * Colour only for status: a failure is the destructive text token, a review
 * the warning token, and progress (a match, a landed copy) plain text — tint
 * TEXT is kept for things you can tap, so success shows as a tint dot instead
 * (activityDot).
 */
export function activityTone(kind: ApiOverviewActivity['kind']): string {
  switch (kind) {
    case 'failed':
      return 'text-destructive-text';
    case 'review':
      return 'text-warning-text';
    case 'matched':
    case 'added':
      return 'text-foreground';
  }
}

/** The matching status dot (fill) per activity kind. */
export function activityDot(kind: ApiOverviewActivity['kind']): string {
  switch (kind) {
    case 'failed':
      return 'bg-destructive';
    case 'review':
      return 'bg-warning';
    case 'matched':
    case 'added':
      return 'bg-primary';
  }
}
