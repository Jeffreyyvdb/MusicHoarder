<script lang="ts">
  const features: ReadonlyArray<readonly [title: string, body: string]> = [
    [
      'Identified by sound',
      'Chromaprint + AcoustID fingerprint every file — even when it is named track_047.mp3 with empty tags.'
    ],
    [
      'Consensus across seven providers',
      'AcoustID, Spotify, MusicBrainz, Deezer, Apple Music and two community trackers vote; MusicHoarder takes the agreement, not the first hit.'
    ],
    [
      'Graded by an LLM',
      'A quality LLM scores every match 0–100 and only escalates what it distrusts to your Inbox — so you review dozens of tracks, not thousands.'
    ],
    [
      'Bit-aware dedupe',
      'Groups by acoustic fingerprint, keeps the highest-quality copy, and logs every decision so you can audit any choice later.'
    ],
    [
      'Your files, untouched',
      'The source folder is mounted read-only. MusicHoarder writes a clean copy to a separate library; the originals are yours to keep or delete.'
    ],
    [
      'Plain files, no lock-in',
      'Output is Artist / Year - Album / NN - Track on disk, with real artwork and synced lyrics embedded. No proprietary database, no cloud.'
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
