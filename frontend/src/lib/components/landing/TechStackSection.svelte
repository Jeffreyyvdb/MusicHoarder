<script lang="ts">
  import { Folder, Play, Tag, AudioWaveform } from '@lucide/svelte';

  const sources = [
    {
      icon: Folder,
      title: 'Local folders',
      body: '~/Music · ~/Downloads · external drives · NAS shares (SMB/AFP)'
    },
    {
      icon: Play,
      title: 'Spotify',
      body: 'Liked songs + playlists, queued for purchase or fingerprint-matched against your library'
    },
    {
      icon: Tag,
      title: 'MusicBrainz / Discogs',
      body: 'Open metadata catalogues — the gold standard.'
    },
    {
      icon: AudioWaveform,
      title: 'AcoustID / Chromaprint',
      body: 'Acoustic fingerprints. Identifies the recording, not the filename.'
    }
  ];
</script>

<section id="sources" class="mx-auto max-w-[1280px] px-6 pt-6 pb-2 md:px-14">
  <div
    class="text-muted-foreground font-mono text-[11px] font-semibold tracking-[0.12em] uppercase"
  >
    SOURCES
  </div>
  <h2 class="mt-2 mb-7 text-[32px] font-bold tracking-[-0.025em]">
    Imports what you actually have.
  </h2>

  <div class="grid grid-cols-1 gap-3.5 md:grid-cols-2">
    {#each sources as src (src.title)}
      <div class="bg-card border-border rounded-lg border p-4.5">
        <src.icon class="text-primary h-4.5 w-4.5" />
        <div class="mt-2.5 mb-1 text-[14px] font-semibold tracking-[-0.01em]">{src.title}</div>
        <div class="text-muted-foreground text-[12.5px] leading-[1.5] text-pretty">
          {src.body}
        </div>
      </div>
    {/each}
  </div>
</section>
