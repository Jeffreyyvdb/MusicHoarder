import type { ApiOverviewActivity } from '$lib/api-client';

/**
 * Shared colour mapping for `ApiOverviewActivity.type`, used by both
 * PipelineLogRow (the drawer's live log) and PipelineHomeV2 (the pipeline
 * page's Recent activity) so the same event type always reads the same
 * colour regardless of which panel is showing it, in both themes.
 *
 * Colour only for status: a failure is the destructive text token, a review
 * the warning token, and progress (a match, a landed copy) plain text — tint
 * TEXT is kept for things you can tap, so success shows as a tint dot instead
 * (activityDot). The old sky-blue "enriched" meant nothing anywhere else.
 */
export function activityTone(type: ApiOverviewActivity['type']): string {
  switch (type) {
    case 'failed':
      return 'text-destructive-text';
    case 'review':
      return 'text-warning-text';
    case 'enriched':
    case 'copied':
      return 'text-foreground';
    default:
      return 'text-muted-foreground';
  }
}

/** The matching status dot (fill) per activity type. */
export function activityDot(type: ApiOverviewActivity['type']): string {
  switch (type) {
    case 'failed':
      return 'bg-destructive';
    case 'review':
      return 'bg-warning';
    case 'enriched':
    case 'copied':
      return 'bg-primary';
    default:
      return 'bg-muted-foreground-dim';
  }
}
