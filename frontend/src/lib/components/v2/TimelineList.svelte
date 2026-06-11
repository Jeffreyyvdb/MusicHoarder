<script lang="ts">
  import { AlertTriangle, ExternalLink, Search } from '@lucide/svelte';
  import { cn } from '$lib/utils';
  import type { TimelineEvent, TimelineTint } from '$lib/review-helpers';

  type Props = {
    events: TimelineEvent[];
    /**
     * Track timelines span minutes, so a ms-precision clock reads best; album timelines span
     * months, so prefix the date and drop the milliseconds.
     */
    showDate?: boolean;
  };
  const { events, showDate = false }: Props = $props();

  const DOT: Record<TimelineTint, string> = {
    ok: 'bg-primary text-primary-foreground',
    info: 'bg-[#6a89cc] text-white',
    warn: 'bg-amber-500 text-white',
    err: 'bg-red-500 text-white',
    neutral: 'bg-muted-foreground text-white'
  };
  const STAGE: Record<TimelineTint, string> = {
    ok: 'bg-primary/15 text-primary',
    info: 'bg-[#6a89cc]/15 text-[#4a6abc] dark:text-[#9ab0e0]',
    warn: 'bg-amber-500/15 text-amber-600 dark:text-amber-400',
    err: 'bg-red-500/15 text-red-600 dark:text-red-400',
    neutral: 'bg-muted text-muted-foreground'
  };

  function clock(iso: string): string {
    const d = new Date(iso);
    if (Number.isNaN(d.getTime())) return '';
    if (showDate) {
      return (
        d.toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' }) +
        ' ' +
        d.toLocaleTimeString([], { hour12: false, hour: '2-digit', minute: '2-digit' })
      );
    }
    return (
      d.toLocaleTimeString([], { hour12: false }) +
      '.' +
      String(d.getMilliseconds()).padStart(3, '0')
    );
  }
</script>

<ol class="border-border bg-card relative rounded-lg border p-4 pl-5">
  {#each events as ev, i (ev.key)}
    <li class="relative flex gap-3 pb-5 last:pb-0">
      <!-- rail -->
      <div class="flex flex-col items-center">
        <span class={cn('grid size-5 shrink-0 place-items-center rounded-full', DOT[ev.tint])}>
          {#if ev.tint === 'warn' || ev.tint === 'err'}
            <AlertTriangle class="size-3" strokeWidth={2.5} />
          {/if}
        </span>
        {#if i < events.length - 1}
          <span class="bg-border mt-1 w-px flex-1"></span>
        {/if}
      </div>
      <!-- body -->
      <div class="-mt-0.5 min-w-0 flex-1">
        <div class="flex flex-wrap items-center gap-2">
          <span class="text-muted-foreground font-mono text-[11px]">{clock(ev.time)}</span>
          <span
            class={cn(
              'rounded px-1.5 py-0.5 text-[9.5px] font-bold tracking-[0.06em] uppercase',
              STAGE[ev.tint]
            )}>{ev.stage}</span
          >
          {#if ev.provider}
            <span class="border-border inline-flex items-center gap-1.5 rounded-md border px-1.5 py-0.5">
              <span class="size-1.5 rounded-full" style="background: {ev.provider.color}"></span>
              <span class="text-[10.5px]">{ev.provider.label}</span>
              {#if ev.provider.pct != null}
                <span class="text-muted-foreground border-border ml-0.5 border-l pl-1.5 font-mono text-[10.5px]"
                  >{ev.provider.pct}</span
                >
              {/if}
            </span>
          {/if}
        </div>
        <div class="text-foreground/90 mt-1 text-[13px] break-words">{ev.description}</div>
        {#if ev.searchQuery}
          <div class="text-muted-foreground mt-1 flex flex-wrap items-center gap-1.5 text-[11.5px]">
            <Search class="size-3 shrink-0" strokeWidth={2} />
            <span>searched</span>
            {#if ev.searchUrl}
              <a
                href={ev.searchUrl}
                target="_blank"
                rel="noopener noreferrer"
                class="text-foreground/90 hover:text-primary inline-flex items-center gap-1 font-mono underline decoration-dotted underline-offset-2"
                >“{ev.searchQuery}”<ExternalLink class="size-3" strokeWidth={2} /></a
              >
            {:else}
              <span class="text-foreground/90 font-mono">“{ev.searchQuery}”</span>
            {/if}
          </div>
        {/if}
      </div>
    </li>
  {/each}
</ol>
