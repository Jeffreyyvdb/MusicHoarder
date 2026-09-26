<script lang="ts">
  import LandingSection from '$lib/components/landing/LandingSection.svelte';
  import FeatureGrid from '$lib/components/landing/FeatureGrid.svelte';
  import Screenshot from '$lib/components/landing/Screenshot.svelte';
  import { growFeatures } from '$lib/components/landing/landing-content';
</script>

<LandingSection
  id="grow"
  eyebrow="Grow"
  title="New music finds its way in."
  lead="Discover playlists, sync what you like on Spotify, or paste a link — and, if you switch it on, wishlisted songs are fetched and filed for you."
>
  <figure class="mt-12 md:mt-16">
    <Screenshot
      name="discover"
      width={2200}
      height={1376}
      alt="The Discover page: editorial and chart playlists with genre filters, a search box, a paste-a-link button and one-click subscribe"
    />
  </figure>

  <FeatureGrid items={growFeatures} surface="plain" class="mt-12 md:mt-16" />
</LandingSection>
