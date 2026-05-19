<script lang="ts">
  import {
    Headphones,
    Music2,
    FileText,
    Image,
    Disc3,
    PieChart,
    Server,
    Shield
  } from '@lucide/svelte';

  const features = [
    {
      icon: Image,
      title: 'Album Artwork',
      description:
        'Fetches high-quality album covers automatically. Your library finally looks as good as it sounds.'
    },
    {
      icon: FileText,
      title: 'Synced Lyrics',
      description: 'LRC lyrics embedded automatically via LRCLIB. Karaoke-ready, no extra setup.'
    },
    {
      icon: Disc3,
      title: 'Spotify Sync',
      description:
        'Connect your Spotify to pull metadata, discover missing tracks, and keep libraries in sync.'
    },
    {
      icon: PieChart,
      title: 'Discography Tracker',
      description:
        "See what percentage of an artist's discography you own. Chase that 100% completion."
    },
    {
      icon: Music2,
      title: 'Unreleased Music',
      description:
        'Works with leaks, bootlegs, and unofficial releases using community tracker matching.'
    },
    {
      icon: Headphones,
      title: 'Ear Verification',
      description: 'Built-in player to confirm matches before committing. Never silently wrong.'
    },
    {
      icon: Server,
      title: 'Self-Hosted',
      description: 'Your files never leave your machine. Runs on TrueNAS, Proxmox, or any homelab.'
    },
    {
      icon: Shield,
      title: 'Open Source',
      description: 'MIT licensed. Full transparency, community-driven development.'
    }
  ];
</script>

<section class="border-border border-t px-6 py-24">
  <div class="mx-auto max-w-6xl">
    <div class="mb-16 text-center">
      <h2 class="mb-4 text-3xl font-bold md:text-4xl">Built for music hoarders</h2>
      <p class="text-muted-foreground mx-auto max-w-xl text-lg">
        Everything you need to wrangle even the messiest music collection.
      </p>
    </div>

    <div class="grid gap-6 sm:grid-cols-2 lg:grid-cols-4">
      {#each features as feature (feature.title)}
        <div
          class="group border-border bg-card/50 hover:bg-card hover:border-border rounded-xl border p-6 transition-all"
        >
          <div
            class="bg-primary/10 group-hover:bg-primary/20 mb-4 flex h-10 w-10 items-center justify-center rounded-lg transition-colors"
          >
            <feature.icon class="text-primary h-5 w-5" />
          </div>
          <h3 class="mb-2 text-lg font-semibold">{feature.title}</h3>
          <p class="text-muted-foreground text-sm leading-relaxed">{feature.description}</p>
        </div>
      {/each}
    </div>
  </div>
</section>
