<script lang="ts">
  const pipelineLog: [string, string][] = [
    ['scan', 'discovered 47 new audio files'],
    ['fp', 'AcoustID match 0.94 → 3f9e8c72…'],
    ['meta', 'MusicBrainz: Radiohead — "Nude"'],
    ['art', 'CAA: fetched front-1500.jpg'],
    ['lyr', 'LRCLIB: synced lyrics (142 lines)'],
    ['write', '→ /dest/Radiohead/In Rainbows/03 Nude.flac']
  ];

  const features: [string, string][] = [
    ['acoustic fingerprinting', 'Identifies tracks even when the filename is "track_047.mp3".'],
    ['nine enrichment sources', 'MusicBrainz, Discogs, Spotify, LRCLIB and more.'],
    ['deduplication', 'Keeps the highest-quality copy. Logs every decision.'],
    ['manual review queue', 'Low-confidence matches surface here. You decide.'],
    ['original files untouched', 'Source folder is yours to keep or delete.'],
    ['plain files, plain folders', 'No proprietary database. No lock-in.']
  ];
</script>

<div class="mob-surface min-h-screen">
  <div class="mob-header" style="border-bottom: 0; background: transparent;">
    <div class="flex flex-1 items-center gap-2">
      <div class="mob-logo size-[18px]" style="background: var(--primary);">
        <svg width="18" height="18" viewBox="0 0 18 18" fill="none" aria-hidden="true">
          <rect x="1" y="1" width="16" height="16" rx="3.5" fill="var(--primary)" />
          <path d="M5.5 12.5V6l6-1.2V11" stroke="#fff" stroke-width="1.4" stroke-linecap="round" />
          <circle cx="5" cy="12.5" r="1.3" fill="#fff" />
          <circle cx="11" cy="11" r="1.3" fill="#fff" />
        </svg>
      </div>
      <div class="text-sm font-semibold">MusicHoarder</div>
      <span
        class="text-muted-foreground rounded px-1.5 py-0.5 font-mono text-[10px]"
        style="background: var(--surface-sunken);">v0.4.2</span>
    </div>
    <a class="mob-btn ghost w-auto px-2.5 py-1.5" href="/app">Sign in</a>
  </div>

  <div class="mob-landing-hero">
    <div class="mob-landing-eyebrow">Catalogue · Enrich · Archive</div>
    <h1 class="mob-landing-h1">A library for the music you actually <em>collect.</em></h1>
    <p class="mob-landing-lede">
      MusicHoarder ingests your messy folders, identifies every track by acoustic fingerprint,
      and writes a tidy, deduplicated library to disk.
    </p>
    <div class="mob-landing-cta">
      <a class="mob-btn primary" href="/app">Sign in</a>
      <a
        class="mob-btn"
        href="https://github.com/Jeffreyyvdb/MusicHoarder"
        target="_blank"
        rel="noopener noreferrer">Self-host on GitHub</a
      >
    </div>
  </div>

  <div class="mob-landing-card">
    <div class="mob-landing-card-h">
      <span class="mh-pulse"></span>
      <span>live pipeline</span>
      <span class="text-muted-foreground ml-auto text-[10px] font-normal tracking-normal normal-case">
        ~/music_dump_2024
      </span>
    </div>
    <div class="mob-landing-card-b">
      {#each pipelineLog as [stage, msg] (stage)}
        <div class="flex gap-1.5">
          <span class="text-primary shrink-0">[{stage}]</span>
          <span class="text-muted-foreground truncate">{msg}</span>
        </div>
      {/each}
    </div>
  </div>

  <div class="mob-landing-section">
    <div class="mob-landing-eyebrow">What it does</div>
    <h2 class="mob-landing-h2">Six things, well.</h2>
    {#each features as [h, p], i (h)}
      <div class="mob-feature">
        <div class="mob-feature-num">{String(i + 1).padStart(2, '0')}</div>
        <div class="mob-feature-h">{h}</div>
        <div class="mob-feature-p">{p}</div>
      </div>
    {/each}
  </div>

  <div class="text-muted-foreground border-t px-5 pt-5 pb-10 text-center font-mono text-[11px]" style="border-color: var(--border);">
    © 2026 · MIT · no telemetry · runs on your own hardware
  </div>
</div>
