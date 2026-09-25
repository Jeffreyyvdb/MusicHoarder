<script lang="ts">
  import { ExternalLink } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import CommandBlock from '$lib/components/landing/CommandBlock.svelte';
  import LandingSection from '$lib/components/landing/LandingSection.svelte';
  import { createPrimaryCta } from '$lib/components/landing/cta.svelte';
  import { githubUrl, installCommand } from '$lib/components/landing/landing-content';

  const cta = createPrimaryCta({ signedOutLabel: 'Try the live demo' });
</script>

<LandingSection
  id="get-started"
  surface="grouped"
  eyebrow="Start hoarding"
  title="Your library is one command away."
  lead="Pull the images, point them at your folders, and let the pipeline do the cataloguing. Or look around a real library first — the demo is read-only."
>
  <CommandBlock
    text={installCommand}
    label="your-server : ~"
    class="mx-auto mt-12 max-w-[640px] text-left md:mt-14"
  />

  <div class="mt-9 flex flex-wrap justify-center gap-3">
    <Button size="pill" onclick={cta.activate} disabled={cta.busy}>{cta.label}</Button>
    <Button
      size="pill"
      variant="tinted"
      href={githubUrl}
      target="_blank"
      rel="noopener noreferrer"
    >
      <ExternalLink aria-hidden="true" />
      View on GitHub
    </Button>
  </div>
</LandingSection>
