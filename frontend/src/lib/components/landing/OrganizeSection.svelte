<script lang="ts">
  import LandingSection from '$lib/components/landing/LandingSection.svelte';
  import FeatureGrid from '$lib/components/landing/FeatureGrid.svelte';
  import Screenshot from '$lib/components/landing/Screenshot.svelte';
  import { organizeFeatures } from '$lib/components/landing/landing-content';
</script>

<LandingSection
  id="organize"
  eyebrow="Organize"
  title="Identified by sound. Agreed on by consensus."
  lead="Every file is fingerprinted, matched by several providers at once and written as a clean, consistently tagged copy — Artist / Year - Album / NN - Track — while your originals stay exactly as they were."
>
  <figure class="mt-12 md:mt-16">
    <Screenshot
      name="pipeline"
      width={2200}
      height={1376}
      alt="The Pipeline dashboard: 20,192 source files, 18,530 in the library, 3,784 awaiting a decision and an average AI quality of 82.4, above the stage-by-stage conveyor and the queue of what needs you"
    />
    <figcaption class="text-muted-foreground mt-4 text-center text-[15px] leading-[20px]">
      The Pipeline shows the whole flow live: Scan, Fingerprint, Match, Decide, AI grade, Dedupe,
      Library.
    </figcaption>
  </figure>

  <FeatureGrid items={organizeFeatures} surface="plain" class="mt-12 md:mt-16" />

  <div class="mt-16 grid grid-cols-1 items-center gap-8 md:mt-24 lg:grid-cols-[1.35fr_1fr] lg:gap-12">
    <Screenshot
      name="album"
      width={2200}
      height={1376}
      sizes="(min-width: 1024px) 680px, 100vw"
      alt="An album page on the desktop: Mac Miller's Circles, linked to Spotify and Deezer with 10 of 12 tracks present, the missing opener listed with Get this track and Find this track"
    />
    <div class="text-center lg:text-left">
      <h3 class="text-[clamp(26px,3vw,34px)] leading-[1.12] font-bold tracking-[-0.025em] text-balance">
        Every album, complete.
      </h3>
      <p class="text-muted-foreground mt-4 text-[17px] leading-[1.5] text-pretty">
        Each album is cross-checked against MusicBrainz, Spotify, Apple Music and Deezer, so its
        page lists every real track — and marks the ones you are missing, with one tap to go and
        get them.
      </p>
    </div>
  </div>
</LandingSection>
