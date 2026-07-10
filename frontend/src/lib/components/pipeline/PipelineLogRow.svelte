<script lang="ts">
  import type { ApiOverviewActivity } from '$lib/api-client';
  import { activityTone } from '$lib/activity-tone';
  import { cn } from '$lib/utils';

  type Props = { activity: ApiOverviewActivity; faded?: number };
  const { activity, faded = 0 }: Props = $props();

  // Map backend activity type → design's "stage tag" abbreviation.
  // Colour comes from the shared activityTone() so it matches PipelineHomeV2's
  // "Live activity" panel — this is purely visual sugar, the underlying values
  // come from /overview.
  const TAG: Record<ApiOverviewActivity['type'], string> = {
    discovered: 'scan',
    enriched: 'meta',
    copied: 'write',
    review: 'meta',
    failed: 'err'
  };

  const t = $derived({ tag: TAG[activity.type] ?? TAG.discovered, tone: activityTone(activity.type) });
  const msgTone = $derived(
    activity.type === 'failed' || activity.type === 'review' ? t.tone : 'text-muted-foreground'
  );
  const subject = $derived(
    activity.artist && activity.artist !== 'unknown'
      ? `${activity.artist} — ${activity.track}`
      : activity.track
  );
  const opacity = $derived(Math.max(0.45, 1 - faded * 0.04));
</script>

<div
  class="grid grid-cols-[80px_60px_1fr] items-baseline gap-2 rounded px-1.5 py-0.5 hover:bg-muted/40"
  style:opacity={opacity}
>
  <span class="text-muted-foreground/70 font-mono text-[10px] tabular-nums">{activity.time}</span>
  <span class={cn('font-mono text-[10px] font-semibold', t.tone)}>[{t.tag}]</span>
  <span class={cn('truncate font-mono text-[11px]', msgTone)}>{subject}</span>
</div>
