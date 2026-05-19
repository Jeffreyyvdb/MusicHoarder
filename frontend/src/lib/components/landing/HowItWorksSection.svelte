<script lang="ts">
  import {
    ScanLine,
    AudioWaveform,
    Tag,
    Image as ImageIcon,
    FileText,
    Copy,
    Save,
    ChevronRight
  } from '@lucide/svelte';

  const stages = [
    { icon: ScanLine, label: 'Scan', desc: 'walk source tree, sniff format' },
    { icon: AudioWaveform, label: 'Fingerprint', desc: 'Chromaprint hash → AcoustID' },
    { icon: Tag, label: 'Lookup', desc: 'MusicBrainz + Discogs + Spotify' },
    { icon: ImageIcon, label: 'Artwork', desc: 'CAA → Last.fm → Spotify upscale' },
    { icon: FileText, label: 'Lyrics', desc: 'LRCLIB synced → Genius fallback' },
    { icon: Copy, label: 'Dedupe', desc: 'compare bitrate + length + hash' },
    { icon: Save, label: 'Write', desc: 'rename, tag, sort, move' }
  ];
</script>

<section id="pipeline" class="mx-auto max-w-[1280px] px-6 py-14 text-center md:px-14">
  <div
    class="text-muted-foreground font-mono text-[11px] font-semibold tracking-[0.12em] uppercase"
  >
    THE PIPELINE
  </div>
  <h2 class="mt-2 mb-7 text-[32px] font-bold tracking-[-0.025em]">
    Seven stages. Streamed end-to-end.
  </h2>

  <div class="flex flex-wrap items-stretch justify-center gap-1">
    {#each stages as stage, i (stage.label)}
      <div
        class="bg-card border-border min-w-[120px] flex-1 rounded-lg border px-3.5 py-3.5 text-center md:max-w-[160px]"
      >
        <div
          class="bg-primary/10 text-primary mx-auto mb-2 grid h-8 w-8 place-items-center rounded-md"
        >
          <stage.icon class="h-4 w-4" />
        </div>
        <div class="mb-1 text-[12px] font-semibold">{stage.label}</div>
        <div class="text-muted-foreground text-[10.5px] leading-[1.4]">{stage.desc}</div>
      </div>
      {#if i < stages.length - 1}
        <div class="text-muted-foreground/60 grid place-items-center self-center px-0.5">
          <ChevronRight class="h-3 w-3" />
        </div>
      {/if}
    {/each}
  </div>
</section>
