<script lang="ts">
  import LandingSection from '$lib/components/landing/LandingSection.svelte';
  import FeatureGrid from '$lib/components/landing/FeatureGrid.svelte';
  import Screenshot from '$lib/components/landing/Screenshot.svelte';
  import { shareFeatures } from '$lib/components/landing/landing-content';
</script>

<LandingSection
  id="share"
  surface="grouped"
  eyebrow="Share and keep track"
  title="Bring people in. See what it’s been doing."
  lead="Share a single album or your whole library, and check the numbers and the history behind it whenever you like."
>
  <FeatureGrid items={shareFeatures} surface="grouped" class="mt-12 md:mt-16" />

  <div class="mt-16 grid grid-cols-1 gap-10 md:mt-24 lg:grid-cols-2 lg:gap-8">
    <figure>
      <Screenshot
        name="stats"
        width={2200}
        height={1376}
        sizes="(min-width: 1024px) 600px, 100vw"
        alt="The Stats page: library, cover, lyrics and liked-to-library counts above the pipeline funnel, the Spotify wishlist journey, top artists and a format breakdown"
      />
      <figcaption class="mt-4 text-center">
        <span class="block text-[19px] leading-[24px] font-semibold">Stats</span>
        <span class="text-muted-foreground mt-1 block text-[15px] leading-[20px]">
          Your hoard at a glance, from the pipeline funnel to the format breakdown.
        </span>
      </figcaption>
    </figure>
    <figure>
      <Screenshot
        name="history"
        width={2200}
        height={1376}
        sizes="(min-width: 1024px) 600px, 100vw"
        alt="The History feed: scans, cover art added, artists renamed and album splits healed, each with when it happened, filterable by kind and period"
      />
      <figcaption class="mt-4 text-center">
        <span class="block text-[19px] leading-[24px] font-semibold">History</span>
        <span class="text-muted-foreground mt-1 block text-[15px] leading-[20px]">
          Everything it acquired, fixed and wrote, and when.
        </span>
      </figcaption>
    </figure>
  </div>
</LandingSection>
