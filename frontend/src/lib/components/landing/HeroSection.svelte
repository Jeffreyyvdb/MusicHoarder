<script lang="ts">
  import { Button } from '$lib/components/ui/button';

  type LogRow = readonly [stage: string, msg: string, level: 'ok' | 'warn'];

  const log: LogRow[] = [
    ['scan', 'discovered 47 new audio files', 'ok'],
    ['fp', 'AcoustID match (0.94) → 3f9e8c72-1a4b…', 'ok'],
    ['meta', 'MusicBrainz: Radiohead — In Rainbows — "Nude"', 'ok'],
    ['art', 'CAA: fetched front-1500.jpg (214 KB)', 'ok'],
    ['lyr', 'LRCLIB: synced lyrics (142 lines)', 'ok'],
    ['dupe', 'duplicate → keeping FLAC over 320 MP3', 'warn'],
    ['write', '→ /dest/Radiohead/In Rainbows (2007)/03 Nude.flac', 'ok'],
    ['fp', 'low confidence (0.62) — flagged for review', 'warn']
  ];
</script>

<section
  class="mx-auto grid max-w-[1280px] items-center gap-10 px-6 pt-3 pb-14 md:grid-cols-[1.1fr_1fr] md:gap-16 md:px-14 md:pt-3 md:pb-14"
>
  <div class="pt-3">
    <div
      class="text-muted-foreground font-mono text-[11px] font-semibold tracking-[0.12em] uppercase"
    >
      CATALOGUE · ENRICH · ARCHIVE
    </div>

    <h1
      class="mt-3.5 mb-5 text-[clamp(44px,5.5vw,76px)] leading-[0.98] font-bold tracking-[-0.035em] text-balance"
    >
      A library for the<br />music you actually<br /><em class="text-primary font-normal italic"
        >collect.</em
      >
    </h1>

    <p
      class="text-muted-foreground mb-7 max-w-[540px] text-[16px] leading-[1.55] text-pretty"
    >
      MusicHoarder ingests your messy folders, identifies every track by acoustic fingerprint, pulls
      metadata from nine open sources, and writes a tidy, deduplicated library to disk —
      <span class="text-primary font-mono text-[13px]"
        >&nbsp;artist / album (year) /&nbsp;</span
      >
      — the way it should be.
    </p>

    <div class="mb-6 flex flex-wrap gap-2.5">
      <Button size="lg" href="/library">Sign in</Button>
      <Button
        size="lg"
        variant="outline"
        href="https://github.com/Jeffreyyvdb/MusicHoarder"
        target="_blank"
        rel="noopener noreferrer">Self-host on GitHub</Button
      >
    </div>

    <div class="text-muted-foreground font-mono text-[12px]">
      Open source · MIT · runs on your own hardware
    </div>
  </div>

  <div class="md:justify-self-end">
    <div
      class="bg-card border-border overflow-hidden rounded-[10px] border"
      style="box-shadow: 0 8px 24px rgba(0,0,0,0.08), 0 0 0 0.5px rgba(0,0,0,0.06);"
    >
      <div
        class="bg-surface-sunken border-border text-muted-foreground flex items-center gap-2 border-b px-4 py-3 text-[11px] font-semibold tracking-[0.04em] uppercase"
      >
        <span class="bg-primary relative h-[7px] w-[7px] rounded-full">
          <span class="bg-primary absolute inset-0 animate-ping rounded-full opacity-60"></span>
        </span>
        <span>live pipeline</span>
        <span
          class="ml-auto max-w-[240px] truncate font-mono text-[11px] font-normal tracking-normal normal-case"
        >
          ~/Downloads/music_dump_2024
        </span>
      </div>

      <div class="max-h-[280px] overflow-hidden px-4 py-3 font-mono text-[11px] leading-[1.6]">
        {#each log as [stage, msg, level] (stage + msg)}
          <div class="flex gap-2 py-0.5">
            <span
              class="flex-shrink-0 font-mono"
              style:color={level === 'warn' ? '#a0721a' : 'var(--primary)'}
            >
              [{stage}]
            </span>
            <span class="text-muted-foreground flex-1 truncate font-mono">{msg}</span>
          </div>
        {/each}
      </div>

      <div
        class="bg-surface-sunken border-border text-muted-foreground flex justify-between border-t px-4 py-2.5 text-[11px]"
      >
        <span>processed <strong class="text-foreground font-semibold">8,955</strong></span>
        <span>remaining <strong class="text-foreground font-semibold">3,892</strong></span>
        <span>eta <strong class="text-foreground font-semibold">00:14:32</strong></span>
      </div>
    </div>
  </div>
</section>
