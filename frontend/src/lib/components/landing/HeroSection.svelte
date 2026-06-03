<script lang="ts">
  import { goto } from '$app/navigation';
  import { signInAsDemo } from '$lib/api-client';
  import { Button } from '$lib/components/ui/button';
  import CommandBlock from '$lib/components/landing/CommandBlock.svelte';
  import { demoHeroLog, githubUrl, installCommand } from '$lib/components/landing/landing-demo-data';

  let launching = $state(false);

  async function tryDemo() {
    if (launching) return;
    launching = true;
    try {
      await signInAsDemo();
      await goto('/pipeline');
    } catch {
      await goto('/login');
    }
  }
</script>

<section
  class="mx-auto grid max-w-[1280px] items-center gap-10 px-6 pt-3 pb-14 md:grid-cols-[1.1fr_1fr] md:gap-16 md:px-14"
>
  <!-- LEFT column -->
  <div class="pt-3">
    <div
      class="text-muted-foreground flex items-center gap-2 font-mono text-[11px] font-semibold tracking-[0.12em] uppercase"
    >
      <span class="bg-primary relative inline-block size-[7px] flex-shrink-0 rounded-full">
        <span class="bg-primary absolute inset-0 animate-ping rounded-full opacity-60"></span>
      </span>
      <span>SELF-HOSTED · MIT · YOUR HARDWARE</span>
    </div>

    <h1
      class="mt-3.5 mb-5 text-[clamp(40px,5.5vw,68px)] leading-[1.0] font-bold tracking-[-0.035em] text-balance"
    >
      Point it at the mess.<br />Get back a <em class="text-primary font-normal italic"
        >library.</em
      >
    </h1>

    <p class="text-muted-foreground mb-6 max-w-[540px] text-[16px] leading-[1.6] text-pretty">
      MusicHoarder is a self-hosted pipeline that <strong class="text-foreground"
        >fingerprints every track</strong
      >, reaches <strong class="text-foreground">consensus across seven providers</strong>, grades
      the result with a <strong class="text-foreground">quality LLM</strong>, dedupes, and writes a
      tidy library to your own disk.
    </p>

    <CommandBlock text={installCommand} label="your-server : ~" class="mb-6 max-w-[560px]" />

    <div class="mb-6 flex flex-wrap items-center gap-3">
      <Button size="lg" onclick={tryDemo} disabled={launching}>Try the live demo</Button>
      <a
        href={githubUrl}
        target="_blank"
        rel="noopener noreferrer"
        class="text-muted-foreground hover:text-foreground text-[14px] font-medium transition-colors"
      >
        View on GitHub
      </a>
    </div>

    <div class="text-muted-foreground flex flex-wrap gap-x-5 gap-y-1.5 font-mono text-[11.5px]">
      <span>runs on your hardware</span>
      <span>source mounted read-only</span>
      <span>open files on disk</span>
    </div>
  </div>

  <!-- RIGHT column: live-pipeline log card -->
  <div class="md:justify-self-end">
    <div
      class="bg-card border-border overflow-hidden rounded-[10px] border"
      style="box-shadow: 0 8px 24px rgba(0,0,0,0.08), 0 0 0 0.5px rgba(0,0,0,0.06);"
    >
      <div
        class="bg-surface-sunken border-border text-muted-foreground flex items-center gap-2 border-b px-4 py-3 text-[11px] font-semibold tracking-[0.04em] uppercase"
      >
        <span class="bg-primary relative h-[7px] w-[7px] rounded-full">
          <span class="bg-primary absolute inset-0 animate-ping rounded-full opacity-60"></span>
        </span>
        <span>live pipeline</span>
        <span
          class="ml-auto max-w-[240px] truncate font-mono text-[11px] font-normal tracking-normal normal-case"
        >
          ~/Downloads/music_dump_2024
        </span>
      </div>

      <div class="max-h-[280px] overflow-hidden px-4 py-3 font-mono text-[11px] leading-[1.6]">
        {#each demoHeroLog as [stage, msg, level] (stage + msg)}
          <div class="flex gap-2 py-0.5">
            <span
              class="flex-shrink-0 font-mono"
              style:color={level === 'warn' ? '#a0721a' : 'var(--primary)'}
            >
              [{stage}]
            </span>
            <span class="text-muted-foreground flex-1 truncate font-mono">{msg}</span>
          </div>
        {/each}
      </div>

      <div
        class="bg-surface-sunken border-border text-muted-foreground flex justify-between border-t px-4 py-2.5 text-[11px]"
      >
        <span>processed <strong class="text-foreground font-semibold">8,955</strong></span>
        <span>remaining <strong class="text-foreground font-semibold">3,892</strong></span>
        <span>eta <strong class="text-foreground font-semibold">00:14:32</strong></span>
      </div>
    </div>
  </div>
</section>
