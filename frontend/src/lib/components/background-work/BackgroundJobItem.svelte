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
    trendInSheet?: boolean;
  };
  let { job, layout, onopen, now = Date.now(), trendInSheet = false }: Props = $props();

  const Icon = $derived(JOB_ICONS[job.id]);
  const headline = $derived(job.headline);
  const share = $derived(coverage(headline.value, headline.of));
  const eta = $derived(headline.etaUtc ? formatEta(headline.etaUtc, { now }) : null);
  const lastWorked = $derived(
    job.lastWorkedAtUtc ? formatRelativeTime(job.lastWorkedAtUtc, now) : null
  );
  const nextRun = $derived(job.nextRunAtUtc ? formatMoment(job.nextRunAtUtc, { now }) : null);
  const interactive = $derived(layout !== 'sheet');

  type Fact = { label: string; value: string; note?: string | null };
  const facts = $derived.by((): Fact[] => {
    const all: Fact[] = [];
    if (eta) all.push({ label: 'At this pace', value: eta, note: headline.etaLabel });
    if (layout === 'sheet') {
      if (lastWorked) all.push({ label: 'Last worked', value: lastWorked });
      if (nextRun) all.push({ label: 'Next look', value: nextRun });
      return all;
    }
    if (all.length > 0) return all.map((fact) => ({ ...fact, note: null }));
    if (nextRun) return [{ label: 'Next look', value: nextRun }];
    if (lastWorked) return [{ label: 'Last worked', value: lastWorked }];
    return job.trendLabel ? [{ label: 'Last worked', value: 'not yet' }] : [];
  });
</script>

{#snippet trendChart()}
  {#if job.trendLabel}
    <span class="flex flex-col gap-1.5">
      <Sparkline values={job.trend} label={job.trendLabel} class="h-10 w-full" />
      <span class="text-footnote text-muted-foreground block first-letter:uppercase md:text-xs">
        {job.trendLabel}, last {job.trend.length} days
      </span>
    </span>
  {/if}
{/snippet}

{#snippet metric()}
  {#if headline.caption}
    <span class="flex flex-col gap-0.5">
      <span class="text-title-1 leading-none font-bold tracking-tight tabular-nums">
        {formatValue(headline.format, headline.value)}
      </span>
      <span class="text-subheadline text-muted-foreground mt-1 md:text-sm">{headline.caption}</span>
    </span>
  {/if}
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
{/snippet}

{#snippet factList(list: Fact[])}
  {#if list.length > 0}
    <span
      class={cn(
        'flex flex-col',
        layout === 'sheet' && 'divide-separator divide-y-(length:--hairline)',
        layout === 'card' && 'border-separator gap-1 border-t-(length:--hairline) pt-3',
        layout === 'row' && 'gap-1'
      )}
    >
      {#each list as fact (fact.label)}
        <span
          class={cn(
            'text-footnote flex flex-wrap items-baseline justify-between gap-x-3 md:text-xs',
            layout === 'sheet' && 'text-subheadline py-2.5 first:pt-0 last:pb-0 md:text-sm'
          )}
        >
          <span class="text-muted-foreground">{fact.label}<span class="sr-only">:</span></span>
          <span class="ml-auto flex min-w-0 flex-col items-end text-right">
            <span class="text-foreground first-letter:uppercase">{fact.value}</span>
            {#if fact.note}
              <span class="text-footnote text-muted-foreground md:text-xs">{fact.note}</span>
            {/if}
          </span>
        </span>
      {/each}
    </span>
  {/if}
{/snippet}

{#snippet body()}
  <span class="grid grid-cols-[29px_minmax(0,1fr)_auto] items-start gap-x-3">
    <span
      aria-hidden="true"
      class="bg-muted text-foreground flex size-[29px] items-center justify-center rounded-[7px]"
    >
      <Icon class="size-[18px]" strokeWidth={2} />
    </span>
    <span class="text-body flex min-h-[29px] items-center font-semibold md:text-sm"
      >{job.title}</span
    >
    <span class="flex min-h-[29px] items-center gap-1.5">
      <StateDot state={job.state} />
      <ChevronRight
        aria-hidden="true"
        class="text-muted-foreground-dim -mr-1 size-4 shrink-0"
        strokeWidth={2.5}
      />
    </span>
    <span class="text-footnote text-muted-foreground col-span-2 col-start-2 md:text-xs"
      >{job.stateLabel}</span
    >
  </span>

  <span class="flex flex-col gap-3">{@render metric()}</span>

  <span class="mt-auto flex flex-col gap-3 pt-1">
    {@render trendChart()}
    {@render factList(facts)}
  </span>
{/snippet}

{#snippet sheetBody()}
  <span class="text-subheadline text-muted-foreground block px-4 md:text-sm">{job.description}</span
  >
  <span class="bg-card mx-4 flex flex-col gap-4 rounded-xl p-4">
    <span class="flex flex-col gap-0.5">
      <StateDot state={job.state} />
      <span class="text-footnote text-muted-foreground md:text-xs">{job.stateLabel}</span>
    </span>
    <span class="flex flex-col gap-3">{@render metric()}</span>
    {#if trendInSheet}{@render trendChart()}{/if}
    {@render factList(facts)}
  </span>
{/snippet}

{#if interactive}
  <button
    type="button"
    data-slot="background-job"
    data-job={job.id}
    onclick={() => onopen?.(job.id)}
    class={cn(
      'text-foreground focus-visible:ring-ring flex w-full cursor-pointer flex-col gap-4 text-left transition-[background-color,scale] duration-100 focus-visible:ring-2 focus-visible:outline-hidden focus-visible:ring-inset',
      layout === 'row'
        ? 'after:bg-separator hover:bg-accent active:bg-accent relative px-4 py-4 after:absolute after:inset-x-4 after:bottom-0 after:h-(--hairline) last:after:hidden'
        : 'bg-card hover:ring-separator h-full rounded-xl p-4 hover:ring-1 motion-safe:active:scale-[0.99]'
    )}
  >
    {@render body()}
  </button>
{:else}
  <div data-slot="background-job" data-job={job.id} class="flex w-full flex-col gap-4">
    {@render sheetBody()}
  </div>
{/if}
