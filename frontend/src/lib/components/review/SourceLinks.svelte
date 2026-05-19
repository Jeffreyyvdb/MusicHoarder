<script lang="ts">
  import type { ApiSong } from '$lib/api-client';
  import { lrclibWebSearchUrl } from '$lib/lrclib-url';
  import { matchedViaAcoustId } from '$lib/source-connection';
  import { ExternalLink } from '@lucide/svelte';

  type Props = { track?: ApiSong | null };
  const { track }: Props = $props();

  const links = $derived.by(() => {
    if (!track) return [] as { name: string; url: string }[];
    const list: { name: string; url: string }[] = [];

    if (track.acoustIdTrackId) {
      list.push({
        name: 'AcoustID Track',
        url: `https://acoustid.org/track/${track.acoustIdTrackId}`
      });
    } else if (matchedViaAcoustId(track.matchedBy ?? undefined)) {
      list.push({ name: 'AcoustID', url: 'https://acoustid.org' });
    }

    if (track.musicBrainzId) {
      list.push({
        name: 'MusicBrainz Recording',
        url: `https://musicbrainz.org/recording/${track.musicBrainzId}`
      });
    }
    if (track.musicBrainzReleaseId) {
      list.push({
        name: 'MusicBrainz Release',
        url: `https://musicbrainz.org/release/${track.musicBrainzReleaseId}`
      });
    }
    if (track.spotifyId) {
      list.push({
        name: 'Spotify Track',
        url: `https://open.spotify.com/track/${track.spotifyId}`
      });
    }

    const lrclibSearch = lrclibWebSearchUrl(track.artist, track.title);
    if (lrclibSearch || track.lrclibId) {
      list.push({ name: 'LRCLIB', url: lrclibSearch ?? 'https://lrclib.net' });
    }
    return list;
  });
</script>

{#if links.length > 0}
  <div class="bg-secondary/50 rounded-lg p-3">
    <p class="text-muted-foreground mb-2 text-xs font-medium">Source Links</p>
    <div class="space-y-1.5">
      {#each links as link (link.url)}
        <a
          href={link.url}
          target="_blank"
          rel="noopener noreferrer"
          class="text-primary hover:text-primary/80 flex items-center gap-2 text-sm transition-colors"
        >
          <ExternalLink class="size-3.5 shrink-0" />
          <span class="truncate">{link.name}</span>
        </a>
      {/each}
    </div>
  </div>
{/if}
