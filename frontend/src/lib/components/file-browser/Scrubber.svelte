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

<!-- The iOS Now Playing scrubber: a 4px capsule that grows to 8px while it is being dragged (and
     on hover/focus with a mouse), filled in the label colour — Apple fills progress with the text
     colour, not the tint, which stays reserved for things you tap. No thumb on touch, as on iOS:
     the finger is the thumb and the bar's growth is the feedback. A fine pointer gets a thumb on
     hover/focus/drag so a click target is visible. The root is 16px tall; an invisible `after:`
     pseudo-element grows the hit area to 44px without moving the bar: 20px up, 8px down, because
     the times row sits 6px under it and the speed capsule there is a control of its own. The
     pseudo-element needs its horizontal insets too — with only `inset-y` it has no width and
     catches nothing. `z-10` keeps this hit area above the capsule's (a later sibling). -->
<div
  role="slider"
  tabindex="0"
  aria-valuemin={0}
  aria-valuemax={100}
  aria-valuenow={Math.round(progress * 100)}
  aria-valuetext={`${formatElapsed(effectiveCurrentTime)} of ${formatDuration(effectiveDuration)}`}
  aria-label="Track progress"
  aria-disabled={!canSeek || undefined}
  data-dragging={dragging || undefined}
  class={cn(
    "group/seek relative z-10 flex h-4 w-full touch-none items-center rounded-full outline-none select-none after:absolute after:inset-x-0 after:-top-5 after:-bottom-2 after:content-['']",
    canSeek ? 'cursor-pointer' : 'cursor-default'
  )}
  onpointerdown={onPointerDown}
  onpointermove={onPointerMove}
  onpointerup={onPointerUp}
  onpointercancel={onPointerUp}
  onlostpointercapture={onPointerUp}
  onkeydown={onKeyDown}
>
  <div
    class={cn(
      'bg-foreground/25 relative h-1 w-full overflow-hidden rounded-full transition-[height] duration-150 ease-out group-focus-visible/seek:h-2 motion-reduce:transition-none pointer-fine:group-hover/seek:h-2',
      dragging && 'h-2'
    )}
  >
    <div
      class="bg-foreground absolute inset-0 origin-left rounded-full"
      style="transform: scaleX({progress})"
    ></div>
  </div>
  <div
    class={cn(
      'pointer-events-none absolute size-3 -translate-x-1/2 rounded-full bg-white opacity-0 shadow-[0_1px_4px_rgb(0_0_0/0.35)] transition-opacity pointer-coarse:hidden pointer-fine:group-hover/seek:opacity-100 pointer-fine:group-focus-visible/seek:opacity-100',
      dragging && 'opacity-100'
    )}
    style="left: {progress * 100}%"
  ></div>
</div>
