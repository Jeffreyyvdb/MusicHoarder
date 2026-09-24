<script lang="ts">
  import type { ShareDailyPoint } from '$lib/api-client';
  import { countLabel, dayLabel, niceMax, peakDay, sumDaily } from '$lib/share-stats';
  import { cn } from '$lib/utils';

  // Opens per day, as columns: one series, so no legend — the section header names it. The marks
  // follow the chart rules the Stats bars do: the accent for the data and nothing else, bars capped
  // at 24px with a 4px rounded top and a square foot on one hairline baseline, 2px of surface
  // between neighbours, a recessive axis with one round tick.
  //
  // The hover layer is a readout line over the plot rather than a floating tooltip (iOS Health's
  // pattern): at rest it names the busiest day, and while a pointer hovers, a finger scrubs or ←/→
  // steps through the days, it shows that day — so nothing ever covers the controls above the chart
  // or runs off a phone's edge. Every figure is also in the visually hidden table after the plot.

  type Props = { daily: ShareDailyPoint[]; class?: string };
  const { daily, class: className }: Props = $props();

  const top = $derived(niceMax(Math.max(0, ...daily.map((d) => d.views))));
  const total = $derived(sumDaily(daily));
  const peak = $derived(peakDay(daily));

  let active = $state<number | null>(null);
  let plot = $state<HTMLElement | null>(null);

  const summary = $derived(
    `Opens per day over ${countLabel(daily.length, 'day', 'days')}: ${countLabel(total.views, 'open', 'opens')}` +
      (peak ? `, most on ${dayLabel(peak.date)} with ${peak.views.toLocaleString()}.` : '.')
  );

  function indexAt(clientX: number): number | null {
    if (!plot || daily.length === 0) return null;
    const box = plot.getBoundingClientRect();
    if (box.width <= 0) return null;
    const i = Math.floor(((clientX - box.left) / box.width) * daily.length);
    return Math.min(daily.length - 1, Math.max(0, i));
  }

  function onpointermove(event: PointerEvent) {
    active = indexAt(event.clientX);
  }

  function onpointerleave(event: PointerEvent) {
    // A finger lifting ends the gesture but should leave the day it picked showing.
    if (event.pointerType === 'mouse') active = null;
  }

  function onkeydown(event: KeyboardEvent) {
    if (daily.length === 0) return;
    const last = daily.length - 1;
    if (event.key === 'ArrowLeft') active = active == null ? last : Math.max(0, active - 1);
    else if (event.key === 'ArrowRight')
      active = active == null ? last : Math.min(last, active + 1);
    else if (event.key === 'Home') active = 0;
    else if (event.key === 'End') active = last;
    else if (event.key === 'Escape') active = null;
    else return;
    event.preventDefault();
  }

  const hovered = $derived(active != null ? daily[active] : null);
  const shown = $derived(hovered ?? peak);

  // Three dates under the plot at most: the first, the middle and the last day.
  type Tick = { i: number; align: 'left' | 'center' | 'right' };
  const ticks = $derived.by<Tick[]>(() => {
    if (daily.length === 0) return [];
    if (daily.length === 1) return [{ i: 0, align: 'center' }];
    const out: Tick[] = [
      { i: 0, align: 'left' },
      { i: daily.length - 1, align: 'right' }
    ];
    if (daily.length >= 7)
      out.splice(1, 0, { i: Math.floor((daily.length - 1) / 2), align: 'center' });
    return out;
  });
</script>

<div class={cn('flex flex-col gap-1.5', className)}>
  <!-- The readout: the hovered day, else the busiest one. -->
  <div class="ml-9 min-h-10" aria-live="polite" aria-atomic="true">
    {#if shown}
      <p class="text-footnote text-muted-foreground md:text-xs">
        {hovered ? dayLabel(shown.date) : `Busiest day · ${dayLabel(shown.date)}`}
      </p>
      <p class="text-subheadline font-semibold tabular-nums md:text-sm">
        {countLabel(shown.views, 'open', 'opens')}
        <span class="text-muted-foreground font-normal">
          · {countLabel(shown.visitors, 'visitor', 'visitors')} · {countLabel(
            shown.plays,
            'play',
            'plays'
          )}
        </span>
      </p>
    {:else}
      <p class="text-footnote text-muted-foreground md:text-xs">No opens in these days</p>
    {/if}
  </div>

  <div class="flex gap-2">
    <!-- The value axis: one round tick at the top, 0 at the baseline. -->
    <div
      class="text-caption-2 text-muted-foreground flex h-32 w-7 shrink-0 flex-col justify-between text-right tabular-nums md:text-[11px]"
      aria-hidden="true"
    >
      <span class="-translate-y-1/2">{top.toLocaleString()}</span>
      <span class="translate-y-1/2">0</span>
    </div>

    <div class="relative min-w-0 flex-1">
      <!-- An image to assistive tech (the table below carries every figure); focusable with ←/→
           only so a sighted keyboard user can step the readout the way a pointer does. -->
      <!-- svelte-ignore a11y_no_noninteractive_tabindex, a11y_no_noninteractive_element_interactions -->
      <div
        bind:this={plot}
        role="img"
        aria-label={summary}
        tabindex="0"
        data-scroll-x
        class="focus-visible:ring-ring relative flex h-32 touch-pan-y items-end rounded-sm outline-none focus-visible:ring-2"
        {onpointermove}
        onpointerdown={onpointermove}
        {onpointerleave}
        {onkeydown}
        onblur={() => (active = null)}
      >
        <!-- Gridline at the tick, and the baseline. -->
        <div
          class="bg-separator pointer-events-none absolute inset-x-0 top-0 h-(--hairline)"
          aria-hidden="true"
        ></div>
        <div
          class="bg-separator pointer-events-none absolute inset-x-0 bottom-0 h-(--hairline)"
          aria-hidden="true"
        ></div>

        {#each daily as d, i (d.date)}
          <div class="flex h-full min-w-0 flex-1 items-end justify-center px-px" aria-hidden="true">
            {#if d.views > 0}
              <div
                class={cn(
                  'bg-primary w-full max-w-6 rounded-t-[4px] transition-opacity duration-100',
                  active != null && active !== i && 'opacity-40'
                )}
                style="height: {Math.max(2, (d.views / top) * 100)}%"
              ></div>
            {/if}
          </div>
        {/each}
      </div>
    </div>
  </div>

  <div
    class="text-caption-2 text-muted-foreground relative ml-9 h-4 md:text-[11px]"
    aria-hidden="true"
  >
    {#each ticks as t (t.i)}
      <span
        class={cn(
          'absolute top-0 whitespace-nowrap',
          t.align === 'center' && '-translate-x-1/2',
          t.align === 'right' && '-translate-x-full'
        )}
        style="left: {t.align === 'left'
          ? 0
          : t.align === 'right'
            ? 100
            : ((t.i + 0.5) / daily.length) * 100}%"
      >
        {dayLabel(daily[t.i].date)}
      </span>
    {/each}
  </div>

  <table class="sr-only">
    <caption>Opens, visitors and plays per day</caption>
    <thead>
      <tr
        ><th scope="col">Day</th><th scope="col">Opens</th><th scope="col">Visitors</th><th
          scope="col">Plays</th
        ></tr
      >
    </thead>
    <tbody>
      {#each daily as d (d.date)}
        <tr
          ><th scope="row">{dayLabel(d.date)}</th><td>{d.views}</td><td>{d.visitors}</td><td
            >{d.plays}</td
          ></tr
        >
      {/each}
    </tbody>
  </table>
</div>
