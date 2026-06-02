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
    ],
    [
      'cover art, sorted',
      'Fetches the right sleeve from the Cover Art Archive, caches it, and writes it into the library so Navidrome and friends show the real artwork.'
    ],
    [
      'full album tracklists',
      'Reconciles the complete tracklist across MusicBrainz, Spotify, Apple Music and Deezer — missing tracks greyed out so gaps are obvious at a glance.'
    ],
    [
      'plays in your browser',
      'A built-in player with waveform, a ⌘K command palette, and a polished mobile layout — browse and listen without leaving the app.'
    ]
  ];
</script>

<section id="features" class="mx-auto max-w-[1280px] scroll-mt-8 px-6 md:px-14">
  <div class="border-border grid grid-cols-1 border-y md:grid-cols-3">
    {#each features as [title, body], i (title)}
      <div
        class="border-border relative px-7 py-8"
        class:max-md:border-t={i > 0}
        class:md:border-r={i % 3 !== 2}
        class:md:border-t={i >= 3}
      >
        <div class="text-primary font-mono text-[11px] font-semibold">
          {String(i + 1).padStart(2, '0')}
        </div>
        <div class="mt-2.5 mb-1.5 text-[17px] font-semibold tracking-[-0.01em]">{title}</div>
        <div class="text-muted-foreground text-[13px] leading-[1.55] text-pretty">
          {body}
        </div>
      </div>
    {/each}
  </div>
</section>
