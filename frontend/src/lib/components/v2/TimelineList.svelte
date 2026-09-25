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

  // The rail dot carries the tint; the stage label carries it again as text, so colour is never
  // the only signal (the warn/err dots also get a glyph). Status text uses the contrast-checked
  // tokens; `ok` and `info` stage labels stay foreground — tint text is for things you can tap.
  const DOT: Record<TimelineTint, string> = {
    ok: 'bg-primary text-primary-foreground',
    info: 'bg-[#6a89cc] text-white',
    warn: 'bg-warning text-black',
    err: 'bg-destructive text-destructive-foreground',
    neutral: 'bg-muted-foreground text-background'
  };
  const STAGE: Record<TimelineTint, string> = {
    ok: 'bg-primary/12 text-foreground',
    info: 'bg-[#6a89cc]/15 text-foreground',
    warn: 'bg-warning/15 text-warning-text',
    err: 'bg-destructive/10 text-destructive-text dark:bg-destructive/15',
    // Label colour on the fill: muted text on a translucent fill measured 4.14:1 in dark.
    neutral: 'bg-muted text-foreground'
  };

  // The pipeline names its stages in capitals (SCAN, FP LOOKUP); the app writes sentence case.
  const STAGE_WORDS: Record<string, string> = {
    'FP LOOKUP': 'Fingerprint lookup',
    'AI GRADE': 'AI grade',
    WRITE: 'Written',
    FLAG: 'Flagged'
  };
  function stageLabel(stage: string): string {
    const known = STAGE_WORDS[stage.toUpperCase()];
    if (known) return known;
    const lower = stage.toLowerCase().replace(/[_-]+/g, ' ');
    return lower.charAt(0).toUpperCase() + lower.slice(1);
  }

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

<ol class="bg-card relative rounded-xl p-4 pl-5">
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
          <span class="text-muted-foreground text-caption-1 font-normal tabular-nums"
            >{clock(ev.time)}</span
          >
          <span class={cn('text-caption-1 rounded px-1.5 py-0.5 font-semibold', STAGE[ev.tint])}
            >{stageLabel(ev.stage)}</span
          >
          {#if ev.provider}
            <span class="bg-secondary inline-flex items-center gap-1.5 rounded-md px-1.5 py-0.5">
              <span class="size-1.5 rounded-full" style="background: {ev.provider.color}"></span>
              <span class="text-caption-1">{ev.provider.label}</span>
              {#if ev.provider.pct != null}
                <!-- Label colour: this sits on the chip's fill, where muted text measured 4.44:1. -->
                <span
                  class="text-foreground border-border text-caption-1 ml-0.5 border-l pl-1.5 tabular-nums"
                  >{ev.provider.pct}</span
                >
              {/if}
            </span>
          {/if}
        </div>
        <div class="text-foreground text-subheadline mt-1 break-words md:text-[13px]">
          {ev.description}
        </div>
        {#if ev.searchQuery}
          <div class="text-muted-foreground text-footnote mt-1 flex flex-wrap items-center gap-1.5">
            <Search class="size-3 shrink-0" strokeWidth={2} />
            <span>searched</span>
            {#if ev.searchUrl}
              <!-- 44pt to a finger: the footnote line is 18px, so a pseudo-element grows the hit
                   area 13px up and down (as HistoryV2's inline links do) without moving the text. -->
              <a
                href={ev.searchUrl}
                target="_blank"
                rel="noopener noreferrer"
                class="text-primary relative inline-flex items-center gap-1 font-mono underline decoration-dotted underline-offset-2 pointer-coarse:after:absolute pointer-coarse:after:-inset-x-1 pointer-coarse:after:-inset-y-[13px]"
                >“{ev.searchQuery}”<ExternalLink class="size-3" strokeWidth={2} /></a
              >
            {:else}
              <span class="text-foreground font-mono">“{ev.searchQuery}”</span>
            {/if}
          </div>
        {/if}
      </div>
    </li>
  {/each}
</ol>
