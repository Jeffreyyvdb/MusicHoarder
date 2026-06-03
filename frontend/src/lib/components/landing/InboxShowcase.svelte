<script lang="ts">
  import { Tag, Copy, Sparkles, ChevronRight } from '@lucide/svelte';
  import { demoInboxCards } from '$lib/components/landing/landing-demo-data';
  import type { DemoInboxCard } from '$lib/components/landing/landing-demo-data';

  const iconByKind = {
    tag: Tag,
    duplicate: Copy,
    ai: Sparkles
  } as const;

  function iconFor(kind: DemoInboxCard['kind']) {
    return iconByKind[kind];
  }
</script>

<section id="inbox" class="mx-auto max-w-[1280px] scroll-mt-8 px-6 py-14 md:px-14">
  <div
    class="text-muted-foreground font-mono text-[11px] font-semibold tracking-[0.12em] uppercase"
  >
    HUMAN REVIEW · INBOX
  </div>
  <h2 class="mt-2 mb-3 text-[clamp(26px,3vw,34px)] font-bold tracking-[-0.025em] text-balance">
    When the pipeline can't decide, you do — and only then.
  </h2>
  <p class="text-muted-foreground max-w-[640px] text-[14.5px] leading-[1.6] text-pretty">
    The automatic stages handle the boring 90%. Everything ambiguous — disagreeing providers,
    look-alike duplicates, anything the quality LLM distrusts — collects in one
    <strong class="text-foreground">Inbox</strong>, so you review dozens of items, not thousands.
  </p>

  <div class="mt-8 grid grid-cols-1 gap-3.5 md:grid-cols-3">
    {#each demoInboxCards as card (card.kind)}
      {@const Icon = iconFor(card.kind)}
      <a
        href="/login"
        class="bg-card border-border hover:border-primary/40 group flex flex-col rounded-lg border p-5 transition"
      >
        <div class="flex items-center gap-3">
          <div class="bg-primary/10 text-primary grid size-9 place-items-center rounded-md">
            <Icon class="size-[18px]" />
          </div>
          <div class="flex flex-col">
            <span class="font-mono text-2xl font-semibold">{card.count}</span>
            <span class="text-[13px] font-medium">{card.label}</span>
          </div>
        </div>

        {#if card.kind === 'ai'}
          <div class="mt-3 flex flex-wrap items-center gap-1.5">
            <span
              class="rounded bg-red-500/15 px-1.5 py-0.5 text-[10px] font-semibold tracking-wide text-red-600 uppercase dark:text-red-400"
            >
              Wrong
            </span>
            <span
              class="rounded bg-amber-500/15 px-1.5 py-0.5 text-[10px] font-semibold tracking-wide text-amber-600 uppercase dark:text-amber-400"
            >
              Questionable
            </span>
          </div>
        {/if}

        <p class="text-muted-foreground mt-3 text-[13px] leading-[1.55]">{card.desc}</p>

        <span class="text-primary mt-3 inline-flex items-center gap-0.5 text-[12.5px] font-medium">
          {card.cta}
          <ChevronRight class="size-3.5" />
        </span>
      </a>
    {/each}
  </div>
</section>
