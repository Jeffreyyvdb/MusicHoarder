<script lang="ts">
  import { ChevronRight } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import Screenshot from '$lib/components/landing/Screenshot.svelte';
  import { createPrimaryCta } from '$lib/components/landing/cta.svelte';
  import { githubUrl } from '$lib/components/landing/landing-content';

  const cta = createPrimaryCta({ signedOutLabel: 'Try the live demo' });
</script>

<section class="bg-background px-4 pt-14 pb-16 md:px-10 md:pt-24 md:pb-24">
  <div class="mx-auto max-w-[1200px] text-center">
    <p class="text-muted-foreground text-[17px] leading-[22px] font-semibold md:text-[19px]">
      Self-hosted · Open source · Free
    </p>
    <h1
      class="mx-auto mt-3 max-w-[960px] text-[clamp(44px,7.4vw,92px)] leading-[1.02] font-bold tracking-[-0.035em] text-balance"
    >
      Point it at the mess. Get back a library.
    </h1>
    <p
      class="text-muted-foreground mx-auto mt-6 max-w-[720px] text-[19px] leading-[1.45] text-pretty md:text-[24px]"
    >
      MusicHoarder identifies every track by its sound, fixes the tags and builds a clean library on
      your own disk — then plays it in an app that feels at home on your iPhone, your Android phone
      and your desktop.
    </p>

    <div class="mt-9 flex flex-wrap items-center justify-center gap-x-6 gap-y-3">
      <Button size="pill" onclick={cta.activate} disabled={cta.busy}>{cta.label}</Button>
      <a
        href={githubUrl}
        target="_blank"
        rel="noopener noreferrer"
        class="text-primary inline-flex min-h-11 items-center text-[17px] font-medium hover:underline"
      >
        View on GitHub
        <ChevronRight class="size-4" aria-hidden="true" />
      </a>
    </div>

    <div class="mx-auto mt-12 max-w-[1200px] md:mt-16">
      <Screenshot
        name="hero"
        dark="hero-dark"
        width={2400}
        height={1543}
        sizes="(min-width: 1200px) 1200px, 100vw"
        alt="MusicHoarder on a desktop and an iPhone: the Albums grid of a real library, next to Now Playing with time-synced lyrics over the song's music video"
      />
    </div>

    <p class="text-muted-foreground mt-6 text-[13px] leading-[18px]">
      Runs on your hardware · Your originals stay read-only · Plain files on disk
    </p>
  </div>
</section>
