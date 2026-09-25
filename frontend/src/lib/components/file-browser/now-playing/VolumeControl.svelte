<script lang="ts">
  import { Volume1, Volume2, VolumeX } from '@lucide/svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn } from '$lib/utils';

  /**
   * Volume inside Now Playing: under the lg column's transport, and in the footer from md with a
   * mouse or trackpad. Now Playing hides the mini player — which is where the desktop volume lives
   * — so without this a desktop listener had to close the overlay to turn it down. Pointer-fine
   * only (the caller hides it on touch): iOS ignores `audio.volume`, and Apple
   * discourages an in-app volume slider on touch devices. Drawn with transforms and pointer
   * handlers, not a bits-ui Slider, for the same reflow reason as the scrubber.
   */
  let dragging = $state(false);
  const level = $derived(Math.max(0, Math.min(1, playerStore.volume)));

  function setFromX(target: HTMLElement, clientX: number) {
    const rect = target.getBoundingClientRect();
    if (rect.width <= 0) return;
    playerStore.setVolume((clientX - rect.left) / rect.width);
  }

  function onKeyDown(e: KeyboardEvent) {
    const steps: Record<string, number> = {
      ArrowRight: 0.05,
      ArrowUp: 0.05,
      ArrowLeft: -0.05,
      ArrowDown: -0.05
    };
    let next: number | null = null;
    if (e.key in steps) next = level + steps[e.key];
    else if (e.key === 'Home') next = 0;
    else if (e.key === 'End') next = 1;
    if (next === null) return;
    e.preventDefault();
    playerStore.setVolume(next);
  }
</script>

<div class="flex items-center gap-3">
  <button
    type="button"
    class="text-muted-foreground hover:text-foreground focus-visible:ring-ring flex size-8 shrink-0 items-center justify-center rounded-full outline-none focus-visible:ring-2"
    aria-label={level === 0 ? 'Unmute' : 'Mute'}
    onclick={() => playerStore.toggleMute()}
  >
    {#if level === 0}
      <VolumeX class="size-4" />
    {:else}
      <Volume1 class="size-4" />
    {/if}
  </button>
  <div
    role="slider"
    tabindex="0"
    aria-label="Volume"
    aria-valuemin={0}
    aria-valuemax={100}
    aria-valuenow={Math.round(level * 100)}
    aria-valuetext={`${Math.round(level * 100)}%`}
    class="group/vol relative flex h-4 min-w-0 flex-1 cursor-pointer touch-none items-center outline-none select-none"
    onpointerdown={(e) => {
      const target = e.currentTarget as HTMLElement;
      target.setPointerCapture(e.pointerId);
      dragging = true;
      setFromX(target, e.clientX);
    }}
    onpointermove={(e) => {
      const target = e.currentTarget as HTMLElement;
      if (target.hasPointerCapture(e.pointerId)) setFromX(target, e.clientX);
    }}
    onpointerup={() => (dragging = false)}
    onpointercancel={() => (dragging = false)}
    onkeydown={onKeyDown}
  >
    <div
      class={cn(
        'bg-foreground/25 relative h-1 w-full overflow-hidden rounded-full transition-[height] duration-150 group-hover/vol:h-1.5 group-focus-visible/vol:h-1.5',
        dragging && 'h-1.5'
      )}
    >
      <div
        class="bg-foreground absolute inset-0 origin-left rounded-full"
        style="transform: scaleX({level})"
      ></div>
    </div>
  </div>
  <Volume2 class="text-muted-foreground size-4 shrink-0" aria-hidden="true" />
</div>
