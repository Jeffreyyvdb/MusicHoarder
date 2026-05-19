<script lang="ts">
  const features: ReadonlyArray<readonly [title: string, body: string]> = [
    [
      'acoustic fingerprinting',
      'Chromaprint + AcoustID identifies tracks even when filename is "track_047.mp3" and the embedded tags are blank.'
    ],
    [
      'nine enrichment sources',
      'MusicBrainz, Discogs, Spotify, Last.fm, AcoustID, Apple Music, LRCLIB, Genius, Cover Art Archive — fallback chain, never just one.'
    ],
    [
      'deduplication',
      'Bit-aware dedupe keeps the highest-quality copy; logs every decision so you can audit later.'
    ],
    [
      'manual review queue',
      'Anything under your confidence threshold lands in a queue. You decide.'
    ],
    [
      'original files, untouched',
      'MusicHoarder reads but never destroys. The source folder is yours to delete or keep.'
    ],
    [
      'fully open formats',
      'Library writes are plain files in plain folders. No proprietary database, no lock-in.'
    ]
  ];
</script>

<section
  id="features"
  class="border-border mx-auto grid max-w-[1280px] grid-cols-1 border-y px-6 sm:grid-cols-2 md:grid-cols-3 md:px-14"
>
  {#each features as [title, body], i (title)}
    <div
      class="border-border relative py-7 pr-6"
      class:border-r={i % 3 !== 2}
      class:border-t={i >= 3}
    >
      <div class="text-primary font-mono text-[11px] font-semibold">
        {String(i + 1).padStart(2, '0')}
      </div>
      <div class="mt-2.5 mb-1.5 text-[17px] font-semibold tracking-[-0.01em]">{title}</div>
      <div class="text-muted-foreground max-w-[320px] text-[13px] leading-[1.5] text-pretty">
        {body}
      </div>
    </div>
  {/each}
</section>
