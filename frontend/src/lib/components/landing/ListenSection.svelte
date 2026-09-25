<script lang="ts">
  import LandingSection from '$lib/components/landing/LandingSection.svelte';
  import FeatureGrid from '$lib/components/landing/FeatureGrid.svelte';
  import Screenshot from '$lib/components/landing/Screenshot.svelte';
  import { listenFeatures } from '$lib/components/landing/landing-content';
</script>

<LandingSection
  id="listen"
  surface="grouped"
  eyebrow="Listen"
  title="A player worthy of the music you own."
  lead="Once the library is clean, it is a music app in the style of Apple Music — browse by album, artist or track, sing along to synced lyrics, and let it keep playing."
>
  <div class="mx-auto mt-12 max-w-[1100px] md:mt-16">
    <Screenshot
      name="phone-listen"
      width={2400}
      height={1643}
      sizes="(min-width: 1100px) 1100px, 100vw"
      alt="The installed app on three iPhones in dark mode: the Tracks list filtered to Spotify liked songs, Frank Ocean's Blond album page, and Now Playing over the song's music video, with the mini player above the floating tab bar"
    />
  </div>

  <FeatureGrid items={listenFeatures} surface="grouped" class="mt-12 md:mt-16" />

  <figure class="mt-16 md:mt-24">
    <Screenshot
      name="now-playing"
      width={2200}
      height={1376}
      alt="Now Playing on the desktop: Sade's Kiss of Life with its time-synced lyrics over the song's muted music video, the current line highlighted, and the cover and transport controls on the left"
    />
    <figcaption class="text-muted-foreground mt-4 text-center text-[15px] leading-[20px]">
      On a bigger screen, Now Playing puts the lyrics beside the cover — here over the song’s own
      music video, playing in sync.
    </figcaption>
  </figure>
</LandingSection>
