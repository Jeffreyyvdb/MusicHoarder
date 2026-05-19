<script lang="ts">
  import type { ApiSong } from '$lib/api-client';
  import { Badge } from '$lib/components/ui/badge';
  import { Music, AlertTriangle } from '@lucide/svelte';

  type Props = {
    track: ApiSong;
    isSelected: boolean;
    onSelect: () => void;
  };
  const { track, isSelected, onSelect }: Props = $props();
</script>

<button
  type="button"
  onclick={onSelect}
  class="flex w-full min-w-0 max-w-full items-center gap-3 overflow-hidden rounded-lg p-3 text-left transition-colors {isSelected
    ? 'bg-primary/10 border-primary/20 border'
    : 'hover:bg-secondary'}"
>
  <div class="bg-secondary flex size-10 shrink-0 items-center justify-center rounded-lg">
    <Music class="text-muted-foreground size-5" />
  </div>
  <div class="min-w-0 flex-1 overflow-hidden">
    <p class="truncate text-sm font-medium">{track.title || track.fileName || 'Unknown'}</p>
    <p class="text-muted-foreground truncate text-xs">
      {track.artist || 'Unknown Artist'}
      {#if track.matchConfidence != null}
        <span class="ml-1">({Math.round(track.matchConfidence * 100)}%)</span>
      {/if}
    </p>
  </div>
  {#if track.matchWarnings && track.matchWarnings.length > 0}
    <Badge variant="outline" class="shrink-0 gap-1 border-amber-400/30 text-amber-400">
      <AlertTriangle class="size-3" />
      {track.matchWarnings.length}
    </Badge>
  {/if}
</button>
