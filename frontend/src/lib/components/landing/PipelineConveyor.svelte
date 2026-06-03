<script lang="ts">
  import PipelineStageCard from '$lib/components/pipeline/PipelineStageCard.svelte';
  import { demoStages, demoMatchProviders, demoInFlight } from '$lib/components/landing/landing-demo-data';
  import { providerLabel, providerColor } from '$lib/review-helpers';

  let selected = $state('match');

  const selectedLabel = $derived(demoStages.find((s) => s.id === selected)?.label ?? selected);
  const inFlight = $derived(demoInFlight[selected] ?? []);
</script>

<section id="pipeline" class="mx-auto max-w-[1280px] scroll-mt-8 px-6 py-14 md:px-14">
  <div class="text-muted-foreground font-mono text-[11px] font-semibold tracking-[0.12em] uppercase">
    THE PIPELINE
  </div>
  <h2 class="mt-2 mb-3 text-[clamp(26px,3vw,34px)] font-bold tracking-[-0.025em] text-balance">
    Seven stages on one conveyor — watch it move.
  </h2>
  <p class="text-muted-foreground max-w-[640px] text-[14.5px] leading-[1.6] text-pretty">
    Files flow left to right — scanned, fingerprinted, matched against providers, graded by an LLM,
    deduped, then written to your library. <strong class="text-foreground">Click any stage</strong> to
    see what's flowing through it right now.
  </p>

  <!-- The conveyor strip -->
  <div class="mt-8 grid grid-cols-2 gap-2 sm:grid-cols-4 lg:grid-cols-7">
    {#each demoStages as s (s.id)}
      <button
        type="button"
        onclick={() => (selected = s.id)}
        aria-pressed={selected === s.id}
        class="rounded-md text-left outline-none focus-visible:ring-2 focus-visible:ring-primary"
        class:ring-2={selected === s.id}
        class:ring-primary={selected === s.id}
      >
        <PipelineStageCard
          icon={s.icon}
          label={s.label}
          status={s.status}
          isPaused={false}
          count={s.count}
          total={s.total}
          perSec={s.perSec}
        />
      </button>
    {/each}
  </div>

  <!-- Detail panel -->
  <div class="bg-card border-border mt-4 rounded-lg border p-4">
    {#if selected === 'match'}
      <div class="text-muted-foreground mb-3 font-mono text-[11.5px]">
        Providers querying in parallel · avg 240ms
      </div>
      <div class="grid grid-cols-1 gap-2 sm:grid-cols-2 lg:grid-cols-4">
        {#each demoMatchProviders as p (p.provider)}
          <div class="bg-surface-sunken border-border flex items-center gap-2.5 rounded-md border p-2.5">
            <span
              class="size-2.5 shrink-0 rounded-full"
              style="background:{providerColor(p.provider)}"
            ></span>
            <div class="min-w-0 flex-1">
              <div class="truncate text-[12.5px] font-medium">{providerLabel(p.provider)}</div>
              <div class="text-muted-foreground truncate text-[11px]">{p.kind}</div>
            </div>
            {#if p.status === 'Matched' && p.confidence != null}
              <div class="text-primary font-mono text-[12px] font-semibold tabular-nums">
                {Math.round(p.confidence * 100)}%
              </div>
            {:else}
              <div class="text-muted-foreground font-mono text-[12px]">no match</div>
            {/if}
          </div>
        {/each}
      </div>
    {:else}
      <div class="text-muted-foreground mb-3 font-mono text-[11.5px]">
        In flight · {selectedLabel}
      </div>
      {#if inFlight.length > 0}
        <div class="flex flex-wrap gap-2">
          {#each inFlight as item, i (i)}
            <span
              class="inline-flex items-center gap-2 rounded-full border px-2.5 py-1 text-[12px] {item.warn
                ? 'border-amber-500/40 bg-amber-500/10 text-amber-500'
                : 'border-border'}"
            >
              <span>{item.name}</span>
              <span class="font-mono text-[11px] {item.warn ? 'text-amber-500/80' : 'text-muted-foreground'}">{item.meta}</span>
            </span>
          {/each}
        </div>
      {:else}
        <div class="text-muted-foreground py-6 text-center text-[13px]">
          Landed clean — nothing waiting in this stage.
        </div>
      {/if}
    {/if}
  </div>

  <!-- Footer stat row -->
  <div class="text-muted-foreground mt-4 flex flex-wrap items-center gap-x-5 gap-y-2 font-mono text-[11.5px]">
    <span>+47 tracks landed per hour</span>
    <span>0 errors in 24h</span>
    <span class="flex items-center gap-1.5">
      <span class="bg-primary size-2.5 rounded-full"></span>
      live throughput
    </span>
    <span class="flex items-center gap-1.5">
      <span class="size-2.5 rounded-full bg-amber-500"></span>
      needs a human
    </span>
  </div>
</section>
