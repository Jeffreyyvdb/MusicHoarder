<script lang="ts">
  import { ChevronRight, Search, Sparkles, AlertTriangle } from '@lucide/svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { cn } from '$lib/utils';
  import {
    buildTimeline,
    contributedProviders,
    providerColor,
    formatElapsed,
    elapsedMs,
    type TimelineEvent,
    type TimelineTint
  } from '$lib/review-helpers';
  import {
    demoProvenanceSong,
    demoProvenanceDetail,
    demoProvenanceGrade
  } from '$lib/components/landing/landing-demo-data';

  // ── static derived display values (no $state — this section never mutates) ──
  const contributed = contributedProviders(demoProvenanceDetail);
  const providerAttemptCount = demoProvenanceDetail.providerAttempts.length;
  const wallClock = formatElapsed(elapsedMs(demoProvenanceSong, demoProvenanceDetail));

  // ── timeline (real buildTimeline + the AI grade slotted into the chronology) ─
  const events: TimelineEvent[] = (() => {
    const base = [...buildTimeline(demoProvenanceSong, demoProvenanceDetail)];
    base.push({
      key: 'ai-grade',
      time: demoProvenanceGrade.gradedAtUtc ?? demoProvenanceSong.indexedAtUtc ?? '',
      stage: 'AI GRADE',
      tint: 'ok',
      provider: {
        label: 'Quality LLM',
        color: providerColor('Spotify'),
        pct: demoProvenanceGrade.score ?? null
      },
      description: `Quality grade · ${demoProvenanceGrade.verdict} (${demoProvenanceGrade.score}/100) — ${demoProvenanceGrade.summary}`,
      deltaMs: null
    });
    return base.sort((a, b) => new Date(a.time).getTime() - new Date(b.time).getTime());
  })();

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
    return (
      d.toLocaleTimeString([], { hour12: false }) +
      '.' +
      String(d.getMilliseconds()).padStart(3, '0')
    );
  }
</script>

<section id="provenance" class="mx-auto max-w-[1280px] scroll-mt-8 px-6 py-14 md:px-14">
  <div class="text-muted-foreground font-mono text-[11px] font-semibold tracking-[0.12em] uppercase">
    THE RECEIPTS · PROVENANCE
  </div>
  <h2 class="mt-2 mb-3 text-[clamp(26px,3vw,34px)] font-bold tracking-[-0.025em] text-balance">
    Every track remembers how it got here.
  </h2>
  <p class="text-muted-foreground max-w-[640px] text-[14.5px] leading-[1.6] text-pretty">
    Click any track and see the whole story — where the raw file came from, every provider that
    touched it, what the AI graded it, and exactly where it lives now.
    <strong class="text-foreground">Nothing is a black box.</strong>
  </p>

  <div class="bg-card border-border mt-8 rounded-lg border p-5 sm:p-6">
    <!-- Hero -->
    <div class="flex flex-col items-start gap-4 sm:flex-row sm:items-center sm:gap-5">
      <Cover artist="Radiohead" title="In Rainbows" coverUrl={null} size={96} corner={10} caption={false} />
      <div class="min-w-0 flex-1">
        <div class="text-muted-foreground font-mono text-[10px] tracking-[0.1em] uppercase">Track</div>
        <h3 class="mt-0.5 truncate text-2xl font-semibold tracking-tight">Nude</h3>
        <div class="text-muted-foreground mt-0.5 truncate text-[13px]">
          Radiohead · In Rainbows · 2007
        </div>
        <div class="mt-3 flex flex-wrap items-center gap-x-4 gap-y-1.5 text-[11.5px] sm:gap-x-5">
          <span class="text-muted-foreground">
            Duration <b class="text-foreground font-mono">4:17</b>
          </span>
          <span class="text-muted-foreground">
            Format <b class="text-foreground font-mono">FLAC 1024kbps</b>
          </span>
          <span class="text-muted-foreground">
            Size <b class="text-foreground font-mono">31.8 MB</b>
          </span>
          <span class="text-muted-foreground inline-flex items-center gap-1">
            <Sparkles class="size-3" />
            AI grade
            <b class="font-mono" style="color: oklch(0.74 0.15 150)">{demoProvenanceGrade.score}/100</b>
          </span>
        </div>
      </div>
    </div>

    <!-- Source → Destination paths -->
    <div class="mt-6 grid grid-cols-1 items-stretch gap-3 sm:grid-cols-[1fr_auto_1fr]">
      <div class="border-border bg-surface-sunken rounded-lg border p-3.5">
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">
          Source · raw
        </div>
        <div class="text-muted-foreground mt-1.5 font-mono text-[11.5px] break-all">
          {demoProvenanceSong.sourcePath}
        </div>
      </div>
      <div class="text-muted-foreground hidden items-center justify-center sm:flex">
        <ChevronRight class="size-5" />
      </div>
      <div
        class="rounded-lg border p-3.5"
        style="background: oklch(0.62 0.13 145 / 0.08); border-color: oklch(0.62 0.13 145 / 0.3)"
      >
        <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">
          Destination · clean
        </div>
        <div class="text-primary mt-1.5 font-mono text-[11.5px] break-all">
          {demoProvenanceSong.destinationPath}
        </div>
      </div>
    </div>

    <!-- Contributing providers -->
    <div class="mt-6">
      <div class="mb-2.5 flex flex-wrap items-baseline gap-x-2 gap-y-1">
        <span class="text-[13px] font-semibold">Providers that contributed</span>
        <span class="text-muted-foreground text-[11.5px]">
          {contributed.length} of {providerAttemptCount} provider{providerAttemptCount === 1
            ? ''
            : 's'} returned data this track used.
        </span>
      </div>
      <div class="flex flex-wrap gap-2">
        {#each contributed as c (c.label)}
          <span
            class="border-border bg-card inline-flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[12px]"
          >
            <span class="size-2 rounded-full" style="background: {c.color}"></span>
            {c.label}
          </span>
        {/each}
      </div>
    </div>

    <!-- Full timeline -->
    <div class="mt-6">
      <div class="mb-3 flex flex-wrap items-baseline gap-x-2 gap-y-1">
        <span class="text-[13px] font-semibold">Full timeline</span>
        <span class="text-muted-foreground text-[11.5px]">
          {events.length} event{events.length === 1 ? '' : 's'} · {wallClock} end-to-end
        </span>
      </div>

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
                  <span class="text-foreground/90 font-mono">“{ev.searchQuery}”</span>
                </div>
              {/if}
            </div>
          </li>
        {/each}
      </ol>
    </div>
  </div>
</section>
