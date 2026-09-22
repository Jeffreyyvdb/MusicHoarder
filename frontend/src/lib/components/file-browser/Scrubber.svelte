<script lang="ts">
  import { playerStore } from '$lib/stores/player.svelte';
  import { seekTargetForKey } from '$lib/player-seek';
  import { formatDuration } from '$lib/formatters';
  import { cn } from '$lib/utils';

  type Props = {
    /** Whether this is the currently-loaded track. When false, the fill locks at 0%. */
    isActive: boolean;
    /** Optional fixed duration (seconds) override when no audio is loaded. */
    fallbackDuration?: number;
  };

  const { isActive, fallbackDuration = 0 }: Props = $props();

  const effectiveDuration = $derived(
    isActive && playerStore.duration > 0 ? playerStore.duration : fallbackDuration
  );
  const effectiveCurrentTime = $derived(isActive ? playerStore.currentTime : 0);
  const progress = $derived(
    isActive && effectiveDuration > 0
      ? Math.max(0, Math.min(1, playerStore.currentTime / effectiveDuration))
      : 0
  );
  const canSeek = $derived(isActive && effectiveDuration > 0);

  // Drives the thumb + thickened track on a touch drag, which produces no `:hover` at all
  // (Tailwind gates `hover:` behind `@media (hover: hover)`).
  let dragging = $state(false);

  function seekToClientX(target: HTMLElement, clientX: number) {
    const rect = target.getBoundingClientRect();
    if (rect.width <= 0) return;
    const ratio = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width));
    playerStore.seek(ratio * effectiveDuration);
  }

  function onPointerDown(e: PointerEvent) {
    if (!canSeek) return;
    const target = e.currentTarget as HTMLElement;
    target.setPointerCapture(e.pointerId);
    dragging = true;
    seekToClientX(target, e.clientX);
  }

  function onPointerMove(e: PointerEvent) {
    if (!canSeek) return;
    const target = e.currentTarget as HTMLElement;
    if (!target.hasPointerCapture(e.pointerId)) return;
    seekToClientX(target, e.clientX);
  }

  function onPointerUp() {
    dragging = false;
  }

  function onKeyDown(e: KeyboardEvent) {
    if (!canSeek) return;
    const next = seekTargetForKey(e.key, playerStore.currentTime, effectiveDuration);
    if (next === null) return;
    e.preventDefault();
    playerStore.seek(next);
  }

  // formatDuration reads "—" at 0 seconds (right for a not-yet-loaded total), which would
  // announce as "em dash" for the elapsed side at the very start of a track — so the elapsed
  // half of aria-valuetext gets its own zero-is-zero formatter.
  function formatElapsed(seconds: number): string {
    if (!Number.isFinite(seconds) || seconds < 0) return '0:00';
    const m = Math.floor(seconds / 60);
    const s = Math.floor(seconds % 60);
    return `${m}:${s.toString().padStart(2, '0')}`;
  }
</script>

<!-- Honest Apple-Music-style scrubber: a 3px hairline capsule that thickens on
     hover/drag, a scaleX progress fill, and a thumb revealed on hover/focus/drag
     (and at rest on a coarse pointer, which gets no hover at all) — the same
     pattern as the MiniPlayer's seek line so every transport surface shares one
     progress control. The root's own box is only 16px tall, so an invisible
     `after:` pseudo-element (the same expanded-hit-region pattern as
     ui/switch) grows the real hit target to 40px without touching the hairline. -->
<div
  role="slider"
  tabindex="0"
  aria-valuemin={0}
  aria-valuemax={100}
  aria-valuenow={Math.round(progress * 100)}
  aria-valuetext={`${formatElapsed(effectiveCurrentTime)} of ${formatDuration(effectiveDuration)}`}
  aria-label="Track progress"
  class="group/seek relative flex h-4 w-full cursor-pointer touch-none items-center rounded-full outline-none select-none after:absolute after:-inset-y-3 after:content-['']"
  onpointerdown={onPointerDown}
  onpointermove={onPointerMove}
  onpointerup={onPointerUp}
  onpointercancel={onPointerUp}
  onlostpointercapture={onPointerUp}
  onkeydown={onKeyDown}
>
  <div
    class={cn(
      'bg-foreground/20 relative h-[3px] w-full overflow-hidden rounded-full transition-[height] duration-150 ease-out group-hover/seek:h-[7px] group-focus-visible/seek:h-[7px] motion-reduce:transition-none',
      dragging && 'h-[7px]'
    )}
  >
    <div
      class="bg-primary absolute inset-0 origin-left rounded-full"
      style="transform: scaleX({progress})"
    ></div>
  </div>
  <div
    class={cn(
      'border-ring pointer-events-none absolute size-3 -translate-x-1/2 rounded-full border bg-white opacity-0 shadow-sm transition-opacity group-hover/seek:opacity-100 group-focus-visible/seek:opacity-100 pointer-coarse:opacity-100',
      dragging && 'opacity-100'
    )}
    style="left: {progress * 100}%"
  ></div>
</div>
