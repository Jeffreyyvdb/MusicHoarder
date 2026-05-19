<script lang="ts">
  import { Search, AudioWaveform, Sparkles, FolderTree } from '@lucide/svelte';

  const steps = [
    {
      icon: Search,
      step: '1',
      title: 'Scan',
      description:
        'Point at your NAS or local drive. Discovers every .mp3, .flac, and audio file automatically.'
    },
    {
      icon: AudioWaveform,
      step: '2',
      title: 'Fingerprint',
      description: 'Identifies tracks by audio content using AcoustID, not just unreliable file tags.'
    },
    {
      icon: Sparkles,
      step: '3',
      title: 'Enrich',
      description:
        'Matches against MusicBrainz, Spotify, and community databases for complete metadata.'
    },
    {
      icon: FolderTree,
      step: '4',
      title: 'Build',
      description: 'Creates a clean library with correct tags, album art, and organized folder structure.'
    }
  ];
</script>

<section class="bg-card/30 border-border border-t px-6 py-24">
  <div class="mx-auto max-w-6xl">
    <div class="mb-16 text-center">
      <h2 class="mb-4 text-3xl font-bold md:text-4xl">How it works</h2>
      <p class="text-muted-foreground mx-auto max-w-xl text-lg">
        Four simple steps from chaos to a perfectly organized music library.
      </p>
    </div>

    <div class="grid gap-6 sm:grid-cols-2 lg:grid-cols-4 lg:gap-8">
      {#each steps as step (step.step)}
        <div
          class="group bg-card border-border hover:border-primary/50 relative rounded-xl border p-6 transition-colors"
        >
          <div
            class="bg-primary text-primary-foreground absolute -top-3 -right-3 flex h-8 w-8 items-center justify-center rounded-full text-sm font-bold"
          >
            {step.step}
          </div>
          <div
            class="bg-primary/10 group-hover:bg-primary/20 mb-4 flex h-12 w-12 items-center justify-center rounded-lg transition-colors"
          >
            <step.icon class="text-primary h-6 w-6" />
          </div>
          <h3 class="mb-2 text-lg font-semibold">{step.title}</h3>
          <p class="text-muted-foreground text-sm leading-relaxed">{step.description}</p>
        </div>
      {/each}
    </div>
  </div>
</section>
