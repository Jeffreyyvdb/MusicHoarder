<script lang="ts">
  import { playerStore } from '$lib/stores/player.svelte';

  type Props = {
    /** Stable seed (usually the track id) — determines the bar heights deterministically. */
    seed: number;
    /** Whether this is the currently-loaded track. When false, the head locks at 0%. */
    isActive: boolean;
    /** Number of bars in the waveform. */
    bars?: number;
    /** Optional fixed duration (seconds) override when no audio is loaded. */
    fallbackDuration?: number;
  };

  const { seed, isActive, bars = 100, fallbackDuration = 0 }: Props = $props();

  /** Linear-congruential PRNG seeded by the track id so the waveform is stable per song. */
  function seededHeights(s: number, n: number): number[] {
    let state = (Math.abs(Math.floor(s)) || 1) * 9301 + 49297;
    const out: number[] = [];
    for (let i = 0; i < n; i++) {
      state = (state * 9301 + 49297) % 233280;
      const r = state / 233280;
      const env = Math.sin((i / n) * Math.PI) * 0.7 + 0.3;
      out.push(0.15 + r * env * 0.85);
    }
    return out;
  }

  const heights = $derived(seededHeights(seed, bars));

  const effectiveDuration = $derived(
    isActive && playerStore.duration > 0 ? playerStore.duration : fallbackDuration
  );
  const progress = $derived(
    isActive && effectiveDuration > 0
      ? Math.max(0, Math.min(1, playerStore.currentTime / effectiveDuration))
      : 0
  );

  function onClick(e: MouseEvent) {
    if (!isActive || effectiveDuration <= 0) return;
    const target = e.currentTarget as HTMLDivElement;
    const rect = target.getBoundingClientRect();
    const ratio = (e.clientX - rect.left) / rect.width;
    playerStore.seek(Math.max(0, Math.min(1, ratio)) * effectiveDuration);
  }
</script>

<div
  role="slider"
  tabindex="0"
  aria-valuemin={0}
  aria-valuemax={100}
  aria-valuenow={Math.round(progress * 100)}
  aria-label="Track progress"
  class="relative flex h-9 cursor-pointer items-center gap-[1px]"
  onclick={onClick}
  onkeydown={(e) => {
    if (!isActive) return;
    if (e.key === 'ArrowLeft') playerStore.seek(Math.max(0, playerStore.currentTime - 5));
    if (e.key === 'ArrowRight') playerStore.seek(Math.min(effectiveDuration, playerStore.currentTime + 5));
  }}
>
  {#each heights as h, i (i)}
    {@const played = i / heights.length < progress}
    <div
      class="min-w-[1.5px] flex-1 rounded-[1px] transition-colors"
      style="height: {h * 100}%; background: {played ? 'var(--primary)' : 'oklch(0.72 0.005 260 / 0.55)'};"
    ></div>
  {/each}
  <div
    class="bg-primary pointer-events-none absolute top-0 bottom-0 w-[2px]"
    style="left: {progress * 100}%; box-shadow: 0 0 8px var(--primary); opacity: {isActive ? 1 : 0};"
  ></div>
</div>
