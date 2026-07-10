<script lang="ts">
  import { goto } from '$app/navigation';
  import { signInAsDemo } from '$lib/api-client';
  import { Button } from '$lib/components/ui/button';
  import CommandBlock from '$lib/components/landing/CommandBlock.svelte';
  import { githubUrl, installCommand } from '$lib/components/landing/landing-demo-data';
  import { ExternalLink } from '@lucide/svelte';

  let launching = $state(false);

  async function startDemo() {
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

<section id="get-started" class="relative mx-auto max-w-[1280px] scroll-mt-8 px-6 py-14 md:px-14">
  <div
    aria-hidden="true"
    class="pointer-events-none absolute inset-0 -z-10"
    style="background: radial-gradient(60% 70% at 50% 0%, color-mix(in oklch, var(--color-primary) 12%, transparent), transparent 70%);"
  ></div>

  <div class="mx-auto max-w-[640px] text-center">
    <div
      class="text-muted-foreground font-mono text-[11px] font-semibold tracking-[0.12em] uppercase"
    >
      START HOARDING
    </div>
    <h2 class="mt-2 mb-3 text-[clamp(26px,3vw,34px)] font-bold tracking-[-0.025em] text-balance">
      Your library is one command away.
    </h2>
    <p class="text-muted-foreground mx-auto max-w-[640px] text-[14.5px] leading-[1.6] text-pretty">
      Pull the image, point it at your folders, and let the <strong class="text-foreground"
        >conveyor</strong
      > do the cataloguing.
    </p>

    <CommandBlock
      text={installCommand}
      label="your-server : ~"
      class="mx-auto mt-8 max-w-[560px] text-left"
    />

    <div class="mt-7 flex flex-wrap justify-center gap-3">
      <Button size="lg" onclick={startDemo} disabled={launching}>Try the live demo</Button>
      <Button
        size="lg"
        variant="outline"
        href={githubUrl}
        target="_blank"
        rel="noopener noreferrer"
      >
        <ExternalLink class="size-4" />
        View on GitHub
      </Button>
    </div>
  </div>
</section>
