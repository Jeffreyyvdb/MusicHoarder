<script lang="ts">
  import { Music, Pause, Play, Volume2, VolumeX, X } from '@lucide/svelte';
  import { playerStore, attachAudioElement } from '$lib/stores/player.svelte';
  import { Button } from '$lib/components/ui/button';
  import { Slider } from '$lib/components/ui/slider';

  function formatTime(seconds: number): string {
    if (!Number.isFinite(seconds) || Number.isNaN(seconds) || seconds < 0) return '0:00';
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  let audioEl: HTMLAudioElement | null = $state(null);

  $effect(() => {
    if (!audioEl) return;
    return attachAudioElement(audioEl);
  });

  const progressPercent = $derived(
    playerStore.duration > 0 ? (playerStore.currentTime / playerStore.duration) * 100 : 0
  );
  const maxDuration = $derived(playerStore.duration > 0 ? playerStore.duration : 1);
</script>

<!-- Hidden audio element — always mounted so it persists across page navigation -->
<audio bind:this={audioEl} preload="metadata" style="display: none"></audio>

{#if playerStore.currentSong}
  {@const song = playerStore.currentSong}
  <div
    class="border-border bg-sidebar fixed right-0 bottom-0 left-0 z-50 border-t shadow-[0_-4px_24px_oklch(0%_0_0/0.08)] dark:shadow-[0_-4px_20px_rgba(0,0,0,0.35)]"
  >
    <div class="bg-muted block h-0.5 w-full sm:hidden" aria-hidden="true">
      <div
        class="bg-primary h-full transition-[width] duration-200 ease-linear"
        style="width: {progressPercent}%"
      ></div>
    </div>

    <div class="flex h-[56px] items-center gap-2 px-3 sm:h-[64px] sm:gap-3 sm:px-4">
      <button
        type="button"
        onclick={() => playerStore.requestShowDetails()}
        class="hover:bg-primary/10 flex min-w-0 flex-1 items-center gap-2.5 rounded-md p-1 text-left transition-colors sm:w-48 sm:flex-none sm:shrink-0"
      >
        <div class="bg-primary/20 flex size-10 shrink-0 items-center justify-center rounded-md">
          <Music class="text-primary size-5" />
        </div>
        <div class="min-w-0">
          <p class="truncate text-sm leading-tight font-medium">{song.title}</p>
          <p class="text-muted-foreground truncate text-xs leading-tight">{song.artist}</p>
        </div>
      </button>

      <Button
        variant="ghost"
        size="icon"
        class="text-foreground hover:text-primary hover:bg-primary/10 size-9 shrink-0"
        onclick={() => playerStore.togglePlay()}
        aria-label={playerStore.isPlaying ? 'Pause' : 'Play'}
      >
        {#if playerStore.isPlaying}
          <Pause class="size-5" />
        {:else}
          <Play class="size-5 translate-x-px" />
        {/if}
      </Button>

      <span
        class="text-muted-foreground hidden w-9 shrink-0 text-right text-xs tabular-nums sm:block"
      >
        {formatTime(playerStore.currentTime)}
      </span>

      <Slider
        type="single"
        value={playerStore.duration > 0 ? playerStore.currentTime : 0}
        max={maxDuration}
        min={0}
        step={0.01}
        class="hidden min-w-0 flex-1 cursor-pointer sm:flex [&_[data-slot=slider-track]]:h-2 [&_[data-slot=slider-thumb]]:size-4"
        onValueChange={(val) => {
          if (typeof val === 'number') playerStore.seek(val);
        }}
        aria-label="Seek"
      />

      <span
        class="text-muted-foreground hidden w-9 shrink-0 text-xs tabular-nums sm:block"
      >
        {formatTime(playerStore.duration)}
      </span>

      <div class="hidden shrink-0 items-center gap-1 sm:flex">
        <Button
          variant="ghost"
          size="icon"
          class="text-muted-foreground hover:text-foreground size-8"
          onclick={() => playerStore.setVolume(playerStore.volume === 0 ? 0.8 : 0)}
          aria-label={playerStore.volume === 0 ? 'Unmute' : 'Mute'}
        >
          {#if playerStore.volume === 0}
            <VolumeX class="size-4" />
          {:else}
            <Volume2 class="size-4" />
          {/if}
        </Button>
        <Slider
          type="single"
          value={playerStore.volume}
          max={1}
          min={0}
          step={0.02}
          class="w-20 shrink-0 cursor-pointer"
          onValueChange={(val) => {
            if (typeof val === 'number') playerStore.setVolume(val);
          }}
          aria-label="Volume"
        />
      </div>

      <Button
        variant="ghost"
        size="icon"
        class="text-muted-foreground hover:text-foreground size-8 shrink-0"
        onclick={() => playerStore.stop()}
        aria-label="Close player"
      >
        <X class="size-4" />
      </Button>
    </div>
  </div>
{/if}
