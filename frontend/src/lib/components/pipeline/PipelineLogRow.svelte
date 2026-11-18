<script lang="ts">
  import type { ApiOverviewActivity } from '#lib/api-client.ts';
  import { activityTone } from '#lib/activity-tone.ts';
  import { activityLabel, activitySubline } from '#lib/pipeline/recent-activity.ts';
  import { cn } from '#lib/utils.ts';

  type Props = {
    activity: ApiOverviewActivity;
    /** Past the first screenful of the tail: the subject steps down a text tier. */
    older?: boolean;
  };
  const { activity, older = false }: Props = $props();

  // Map the activity kind to the design's "stage tag" abbreviation.
  // Colour comes from the shared activityTone() so it matches PipelineHomeV2's
  // Recent activity — this is purely visual sugar, the underlying values
  // come from /overview. Mono stays here: a log tail is read column by column.
  const TAG: Record<ApiOverviewActivity['kind'], string> = {
    added: 'write',
    matched: 'meta',
    review: 'meta',
    failed: 'err'
  };

  const t = $derived({ tag: TAG[activity.kind], tone: activityTone(activity.kind) });
  // Older rows recede by stepping down a text token, never by opacity: the log sits on the
  // grouped well at 11px, where even a 0.75 fade took the time column under 3:1 in light mode.
  // Failures and reviews keep their status tone at any age.
  const msgTone = $derived(
    activity.kind === 'failed' || activity.kind === 'review'
      ? t.tone
      : older
        ? 'text-muted-foreground-dim'
        : 'text-muted-foreground'
  );
  const subject = $derived(
    activity.subject.type === 'album'
      ? `${activityLabel(activity)} — ${activitySubline(activity)}`
      : activitySubline(activity)
  );
</script>

<div class="hover:bg-accent grid grid-cols-[80px_60px_1fr] items-baseline gap-2 rounded px-1.5 py-0.5">
  <span class="text-muted-foreground-dim font-mono text-[11px] tabular-nums">{activity.time}</span>
  <span class={cn('font-mono text-[11px] font-semibold', t.tone)}>[{t.tag}]</span>
  <span class={cn('truncate font-mono text-[11px]', msgTone)}>{subject}</span>
</div>
