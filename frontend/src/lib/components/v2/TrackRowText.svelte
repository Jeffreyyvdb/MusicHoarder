<script lang="ts">
  import { Heart } from '@lucide/svelte';
  import { Badge } from '$lib/components/ui/badge';
  import SharedByBadge from '$lib/components/v2/SharedByBadge.svelte';
  import { mapEnrichmentStatus, type ApiSong } from '$lib/api-client';
  import { artistOf, titleOf } from '$lib/track-list-view.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { cn } from '$lib/utils';

  /**
   * The text column of a track row — title over the secondary line — shared by every list that
   * shows tracks with a second line (Tracks, an artist's songs, the Overview's favourites), so the
   * marks sit in one place everywhere:
   *
   *   Title                                  (text-primary while it is the loaded song)
   *   [Review] ♥ Artist · Album  [shared]
   *
   * The heart LEADS the secondary line (a list with no secondary line — an album's tracklist —
   * puts it before the title instead). It is a mark, not a control: the row's ⋯ menu is where a
   * favourite is changed. The per-row shared glyph only appears when the list mixes libraries;
   * with one grantor the page subtitle's "Shared by X" already says it for every row.
   */
  type Props = {
    song: ApiSong;
    /** The song loaded in the player: the title takes the tint and is announced as playing. */
    loaded?: boolean;
    /** The secondary line after the marks; defaults to "Artist · Album". */
    secondary?: string;
    /** Show the favourite heart. Off where every row is a favourite (the Favourites shelf). */
    heart?: boolean;
    /**
     * A tap on this row opens Now Playing instead of playing it (the phone's tap rule on the
     * loaded row): say so to a screen reader, as Android's "Show player" click label does.
     */
    opensPlayer?: boolean;
    class?: string;
  };
  const {
    song,
    loaded = false,
    secondary,
    heart = true,
    opensPlayer = false,
    class: className
  }: Props = $props();

  const needsReview = $derived(mapEnrichmentStatus(song.enrichmentStatus) === 'needsreview');
  const liked = $derived(heart && Boolean(song.likedAtUtc));
  const line = $derived(secondary ?? `${artistOf(song)}${song.album ? ` · ${song.album}` : ''}`);
</script>

<span class={cn('flex min-w-0 flex-1 flex-col', className)}>
  <span class={cn('text-body truncate md:text-sm', loaded && 'text-primary')}>
    {#if loaded}<span class="sr-only">Now playing, </span>{/if}{titleOf(song)}
  </span>
  <span class="text-subheadline text-muted-foreground flex min-w-0 items-center gap-1.5 md:text-xs">
    {#if needsReview}
      <Badge variant="warning" class="shrink-0">Review</Badge>
    {/if}
    {#if liked}
      <Heart
        class="text-primary size-3.5 shrink-0"
        fill="currentColor"
        role="img"
        aria-label="In favourites"
      />
    {/if}
    <span class="truncate">{line}</span>
    {#if songsStore.hasMixedSources}
      <SharedByBadge {song} variant="icon" class="shrink-0" />
    {/if}
  </span>
  {#if opensPlayer}<span class="sr-only">. Opens Now Playing</span>{/if}
</span>
