import type { ApiOverviewActivity } from '$lib/api-client';

/**
 * Shared colour mapping for `ApiOverviewActivity.type`, used by both
 * PipelineLogRow (the drawer's live log) and PipelineHomeV2 (the pipeline
 * page's "Live activity" panel) so the same event type always reads the
 * same colour regardless of which panel is showing it, in both themes.
 */
export function activityTone(type: ApiOverviewActivity['type']): string {
  switch (type) {
    case 'failed':
      return 'text-red-600 dark:text-red-400';
    case 'review':
      return 'text-amber-600 dark:text-amber-500';
    case 'enriched':
      return 'text-sky-600 dark:text-sky-400';
    case 'copied':
      return 'text-primary';
    default:
      return 'text-muted-foreground';
  }
}
