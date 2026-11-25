<script lang="ts">
  import { ChevronRight } from '@lucide/svelte';
  import { coverage } from '#lib/background-work/chart-math.ts';
  import { formatEta, formatMoment, formatValue } from '#lib/background-work/format.ts';
  import type { BackgroundJobSummary } from '#lib/background-work/types.ts';
  import { formatRelativeTime } from '#lib/formatters.ts';
  import { cn } from '#lib/utils.ts';
  import { JOB_ICONS } from './job-icons.ts';
  import Sparkline from './Sparkline.svelte';
  import StateDot from './StateDot.svelte';

  type Props = {
    job: BackgroundJobSummary;
    layout: 'row' | 'card' | 'sheet';
    onopen?: (id: BackgroundJobSummary['id']) => void;
    now?: number;
  };
  let { job, layout, onopen, now = Date.now() }: Props = $props();

  const Icon = $derived(JOB_ICONS[job.id]);
  const headline = $derived(job.headline);
  const share = $derived(coverage(headline.value, headline.of));
  const eta = $derived(headline.etaUtc ? formatEta(headline.etaUtc, { now }) : null);
  const lastWorked = $derived(
    job.lastWorkedAtUtc ? formatRelativeTime(job.lastWorkedAtUtc, now) : null
  );
  const nextRun = $derived(job.nextRunAtUtc ? formatMoment(job.nextRunAtUtc, { now }) : null);
  const interactive = $derived(layout !== 'sheet');
</script>

{#snippet body()}
  <span class="flex items-start gap-3">
    <span
      aria-hidden="true"
      class="bg-muted text-foreground mt-0.5 flex size-[29px] shrink-0 items-center justify-center rounded-[7px]"
    >
      <Icon class="size-[18px]" strokeWidth={2} />
    </span>
    <span class="flex min-w-0 flex-1 flex-col">
      <span class="flex items-center justify-between gap-2">
        <span
          class={cn('text-body min-w-0 font-semibold md:text-sm', layout === 'sheet' && 'sr-only')}
        >
          {job.title}
        </span>
        <StateDot state={job.state} />
      </span>
      <span class="text-footnote text-muted-foreground md:text-xs">{job.stateLabel}</span>
    </span>
    {#if interactive}
      <ChevronRight
        aria-hidden="true"
        class="text-muted-foreground-dim mt-1.5 -mr-1 size-4 shrink-0"
        strokeWidth={2.5}
      />
    {/if}
  </span>

  {#if layout === 'sheet'}
    <span class="text-subheadline text-muted-foreground block md:text-sm">{job.description}</span>
  {/if}

  <span class="flex flex-col gap-1.5">
    <span class="flex flex-wrap items-baseline gap-x-2">
      <span class="text-title-3 font-semibold tabular-nums">
        {formatValue(headline.format, headline.value)}
      </span>
      <span class="text-subheadline text-muted-foreground min-w-0 md:text-sm">
        {headline.caption}
      </span>
    </span>
    {#if share !== null && headline.of != null}
      <span
        role="meter"
        aria-label={headline.caption}
        aria-valuemin={0}
        aria-valuemax={headline.of}
        aria-valuenow={Math.min(headline.value, headline.of)}
        aria-valuetext={`${formatValue(headline.format, headline.value)} of ${formatValue(headline.format, headline.of)}`}
        class="bg-muted block h-1.5 overflow-hidden rounded-full"
      >
        <span
          class="block h-full rounded-full"
          style:width="{share * 100}%"
          style:background-color={job.state === 'failing' ? 'var(--viz-bad)' : 'var(--viz-neutral)'}
        ></span>
      </span>
    {/if}
  </span>

  <span class="flex items-end gap-4">
    <span class="flex w-28 shrink-0 flex-col gap-1">
      <Sparkline values={job.trend} label={job.trendLabel} />
      <span class="text-caption-2 text-muted-foreground">{job.trendLabel}</span>
    </span>
    <span class="text-footnote text-muted-foreground flex min-w-0 flex-1 flex-col md:text-xs">
      {#if eta}
        <span class="text-foreground font-medium">At this pace: {eta}</span>
        {#if layout === 'sheet' && headline.etaLabel}<span>{headline.etaLabel}</span>{/if}
      {/if}
      {#if layout === 'sheet'}
        {#if lastWorked}<span>Last worked {lastWorked}</span>{/if}
        {#if nextRun}<span>Next look {nextRun}</span>{/if}
      {:else if !eta}
        {#if nextRun}<span>Next look {nextRun}</span>{:else if lastWorked}<span
            >Last worked {lastWorked}</span
          >{/if}
      {/if}
    </span>
  </span>
{/snippet}

{#if interactive}
  <button
    type="button"
    data-slot="background-job"
    data-job={job.id}
    onclick={() => onopen?.(job.id)}
    class={cn(
      'text-foreground focus-visible:ring-ring hover:bg-accent active:bg-accent flex w-full cursor-pointer flex-col gap-3 text-left transition-colors duration-100 focus-visible:ring-2 focus-visible:outline-hidden focus-visible:ring-inset',
      layout === 'row'
        ? 'after:bg-separator relative px-4 py-3 after:absolute after:inset-x-4 after:bottom-0 after:h-(--hairline) last:after:hidden'
        : 'bg-card rounded-xl p-4'
    )}
  >
    {@render body()}
  </button>
{:else}
  <div
    data-slot="background-job"
    data-job={job.id}
    class="flex w-full flex-col gap-3 px-4 pt-1 pb-3"
  >
    {@render body()}
  </div>
{/if}
