<script lang="ts">
  import type { DiscographyAlbum, DiscographyTrack } from '$lib/types';
  import { Button } from '$lib/components/ui/button';
  import { Badge } from '$lib/components/ui/badge';
  import { Progress } from '$lib/components/ui/progress';
  import {
    Disc,
    Check,
    Download,
    Loader2,
    ChevronDown,
    ChevronUp,
    CheckCircle2
  } from '@lucide/svelte';

  type Props = {
    album: DiscographyAlbum;
    downloadingTracks: Set<string>;
    onDownloadTrack: (trackId: string) => void;
    onDownloadAlbum: (albumId: string) => void;
  };

  const { album, downloadingTracks, onDownloadTrack, onDownloadAlbum }: Props = $props();

  let expanded = $state(false);

  $effect(() => {
    expanded = album.inLibrary;
  });

  const completionPercent = $derived(Math.round((album.tracksOwned / album.totalTracks) * 100));
  const missingTracks = $derived(album.tracks.filter((t) => !t.inLibrary));

  function formatDuration(seconds: number): string {
    const mins = Math.floor(seconds / 60);
    const secs = seconds % 60;
    return `${mins}:${secs.toString().padStart(2, '0')}`;
  }

  function effectiveStatus(track: DiscographyTrack) {
    if (downloadingTracks.has(track.id)) return 'downloading' as const;
    return track.downloadStatus;
  }
</script>

<div class="border-border bg-card overflow-hidden rounded-xl border">
  <div class="flex gap-4 p-4">
    <div class="bg-secondary size-24 shrink-0 overflow-hidden rounded-lg sm:size-32">
      {#if album.albumArt}
        <img
          src={album.albumArt}
          alt={album.name}
          class="size-full object-cover"
          crossorigin="anonymous"
        />
      {:else}
        <div class="flex size-full items-center justify-center">
          <Disc class="text-muted-foreground size-10" />
        </div>
      {/if}
    </div>

    <div class="min-w-0 flex-1">
      <div class="flex items-start justify-between gap-2">
        <div>
          <h3 class="truncate font-semibold">{album.name}</h3>
          <p class="text-muted-foreground text-sm">{album.year}</p>
          <Badge variant="outline" class="mt-1 text-xs capitalize">{album.type}</Badge>
        </div>
      </div>

      <div class="mt-3">
        <div class="mb-1 flex items-center justify-between text-xs">
          <span class="text-muted-foreground">
            {album.tracksOwned} of {album.totalTracks} tracks
          </span>
          <span
            class={completionPercent === 100
              ? 'text-primary font-medium'
              : 'text-muted-foreground'}
          >
            {completionPercent}%
          </span>
        </div>
        <Progress value={completionPercent} class="h-1.5" />
      </div>

      <div class="mt-3 flex flex-wrap items-center gap-2">
        {#if completionPercent < 100}
          <Button
            size="sm"
            variant="default"
            class="h-8"
            onclick={() => onDownloadAlbum(album.id)}
          >
            <Download class="mr-1.5 size-3.5" />
            Get Missing ({missingTracks.length})
          </Button>
        {/if}
        <Button size="sm" variant="ghost" class="h-8" onclick={() => (expanded = !expanded)}>
          {#if expanded}
            <ChevronUp class="mr-1.5 size-3.5" />
            Hide Tracks
          {:else}
            <ChevronDown class="mr-1.5 size-3.5" />
            Show Tracks
          {/if}
        </Button>
      </div>
    </div>
  </div>

  {#if expanded}
    <div class="border-border bg-secondary/20 border-t px-2 py-2">
      {#each album.tracks as track (track.id)}
        {@const status = effectiveStatus(track)}
        <div
          class="hover:bg-secondary/50 flex items-center gap-3 rounded-lg px-3 py-2 transition-colors"
        >
          <div class="w-6 shrink-0 text-center">
            {#if track.inLibrary}
              <CheckCircle2 class="text-primary mx-auto size-4" />
            {:else}
              <span class="text-muted-foreground text-sm">{track.trackNumber}</span>
            {/if}
          </div>

          <div class="min-w-0 flex-1">
            <p
              class="truncate text-sm {track.inLibrary
                ? 'font-medium'
                : 'text-muted-foreground'}"
            >
              {track.name}
            </p>
          </div>

          <span class="text-muted-foreground shrink-0 text-xs">
            {formatDuration(track.duration)}
          </span>

          <div class="flex w-20 shrink-0 justify-end">
            {#if track.inLibrary}
              <Badge variant="secondary" class="text-xs">
                <Check class="mr-1 size-3" />
                Owned
              </Badge>
            {:else if status === 'downloading'}
              <Button size="sm" variant="ghost" disabled class="h-7 px-2">
                <Loader2 class="size-3 animate-spin" />
              </Button>
            {:else if status === 'available'}
              <Button
                size="sm"
                variant="ghost"
                class="text-primary hover:bg-primary/10 hover:text-primary h-7 px-2"
                onclick={() => onDownloadTrack(track.id)}
              >
                <Download class="mr-1 size-3" />
                Get
              </Button>
            {:else}
              <span class="text-muted-foreground text-xs">Unavailable</span>
            {/if}
          </div>
        </div>
      {/each}
    </div>
  {/if}
</div>
