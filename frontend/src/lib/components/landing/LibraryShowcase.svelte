<script lang="ts">
  import LibraryAlbumsGridV2 from '$lib/components/v2/LibraryAlbumsGridV2.svelte';
  import { demoAlbums, demoLibraryStats } from '$lib/components/landing/landing-demo-data';
</script>

<section id="library" class="mx-auto max-w-[1280px] scroll-mt-8 px-6 py-14 md:px-14">
  <div class="text-muted-foreground font-mono text-[11px] font-semibold tracking-[0.12em] uppercase">
    THE PAYOFF · LIBRARY
  </div>
  <h2 class="mt-2 mb-3 text-[clamp(26px,3vw,34px)] font-bold tracking-[-0.025em] text-balance">
    What comes out the other side.
  </h2>
  <p class="text-muted-foreground max-w-[640px] text-[14.5px] leading-[1.6] text-pretty">
    Correct titles, real artwork, consistent folders —
    <strong class="text-foreground">Artist / Year - Album / NN - Track</strong> on your own disk.
    Browse it in a clean interface or just open the folder. No database to escape from.
  </p>

  <div class="mt-2">
    <LibraryAlbumsGridV2 albums={demoAlbums} hrefFor={() => '/login'} isLoading={false} />
  </div>

  <div
    class="text-muted-foreground mt-6 flex flex-wrap items-center gap-x-6 gap-y-2 font-mono text-[12px]"
  >
    <span>
      <strong class="text-foreground font-semibold">{demoLibraryStats.tracks.toLocaleString()}</strong>
      tracks catalogued
    </span>
    <span>
      <strong class="text-foreground font-semibold">{demoLibraryStats.albums.toLocaleString()}</strong>
      albums ·
      <strong class="text-foreground font-semibold">{demoLibraryStats.artists.toLocaleString()}</strong>
      artists
    </span>
    <span>
      <strong class="text-foreground font-semibold">{demoLibraryStats.enrichedPct}%</strong>
      fully enriched
    </span>
    <a href="/login" class="text-foreground hover:text-primary ml-auto transition-colors">
      Open the library in the demo →
    </a>
  </div>
</section>
