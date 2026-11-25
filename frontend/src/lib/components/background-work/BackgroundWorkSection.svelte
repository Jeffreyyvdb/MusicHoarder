<script lang="ts">
  import type { Snippet } from 'svelte';
  import * as GroupedList from '#lib/components/ui/grouped-list/index.ts';
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import { formatMoment } from '#lib/background-work/format.ts';
  import type { BackgroundJobId, BackgroundWorkSummary } from '#lib/background-work/types.ts';
  import { IsMobile } from '#lib/hooks/is-mobile.svelte.ts';
  import BackgroundJobItem from './BackgroundJobItem.svelte';

  type Props = {
    summary: BackgroundWorkSummary | null;
    error?: string | null;
    onopen: (id: BackgroundJobId) => void;
    header: Snippet;
    now?: number;
  };
  let { summary, error = null, onopen, header, now = Date.now() }: Props = $props();

  const isMobile = new IsMobile();
</script>

<GroupedList.Section
  headingLevel={2}
  {header}
  footer={summary
    ? `Updated ${formatMoment(summary.generatedAtUtc, { now })}. Work per day is counted in UTC days.`
    : undefined}
  contentClass={isMobile.current ? undefined : 'overflow-visible rounded-none bg-transparent'}
  data-slot="background-work"
>
  {#if summary}
    {#if isMobile.current}
      {#each summary.jobs as job (job.id)}
        <BackgroundJobItem {job} layout="row" {onopen} {now} />
      {/each}
    {:else}
      <div class="grid grid-cols-[repeat(auto-fill,minmax(20rem,1fr))] gap-4">
        {#each summary.jobs as job (job.id)}
          <BackgroundJobItem {job} layout="card" {onopen} {now} />
        {/each}
      </div>
    {/if}
  {:else if error}
    <div
      class="text-subheadline text-muted-foreground bg-card rounded-xl px-4 py-6 text-center md:text-sm"
    >
      {error}
    </div>
  {:else}
    <div class="bg-card flex flex-col gap-3 rounded-xl p-4" aria-busy="true">
      <Skeleton class="h-5 w-40" />
      <Skeleton class="h-8 w-24" />
      <Skeleton class="h-8 w-full" />
    </div>
  {/if}
</GroupedList.Section>
