<script lang="ts">
  import { ListMusic, Menu, X } from '@lucide/svelte';
  import Cover from '#lib/components/file-browser/Cover.svelte';
  import { EmptyState } from '#lib/components/ui/empty-state/index.ts';
  import { swipeRow } from '#lib/actions/swipe-row.ts';
  import { REMOVE_FROM_QUEUE_ICON } from '#lib/queue/queue-actions.ts';
  import { playerStore, type PlayerSong } from '#lib/stores/player.svelte.ts';
  import { cn } from '#lib/utils.ts';

  /**
   * Now Playing's Queue mode: the song playing, then what follows it — the songs added by hand
   * ("Next in queue", which play first and can be cleared) and the rest of the list ("Up next").
   *
   * Tap a row to play it now (the ones it skips stay behind it, for Previous). Drag a row by its
   * handle to move it within its section; the handle also takes ↑/↓ from a keyboard. Swipe a row
   * left to take it out — with a pointer that is the ✕ the row shows on hover, which a screen
   * reader always finds.
   *
   * The queue can be the whole library, so only the head of the list is drawn: everything added by
   * hand and the next {@link LIST_ROWS} of the list; Show more draws that many again.
   */

  type Props = {
    /** At lg the queue is the right column; on a phone it is the middle of the player. */
    wide?: boolean;
  };
  const { wide = false }: Props = $props();

  const LIST_ROWS = 100;
  let listRows = $state(LIST_ROWS);

  const current = $derived(playerStore.currentSong);
  const upNext = $derived(playerStore.upNext);
  const addedFrom = $derived(playerStore.upNextAddedFrom);
  const addedEnd = $derived(addedFrom + playerStore.upNextAdded);
  const editable = $derived(playerStore.queueEditable);
  const shown = $derived(Math.min(upNext.length, Math.max(addedEnd, 0) + listRows));

  type Section = { kind: 'added' | 'list'; start: number; end: number };
  /**
   * Runs of the upcoming songs, in order. Normally "Next in queue" then "Up next"; a Previous that
   * stepped back past the added songs leaves the song it came from in front of them, as a short
   * "Up next" of its own — said as it is, rather than hidden.
   */
  const sections = $derived.by(() => {
    const out: Section[] = [];
    const push = (kind: Section['kind'], start: number, end: number) => {
      const e = Math.min(end, shown);
      if (e > start) out.push({ kind, start, end: e });
    };
    if (addedEnd > addedFrom) {
      push('list', 0, addedFrom);
      push('added', addedFrom, addedEnd);
      push('list', addedEnd, upNext.length);
    } else {
      push('list', 0, upNext.length);
    }
    return out;
  });

  /**
   * Stable row keys while the queue changes under them: a song's id and which copy of it this is
   * (a song can be queued twice), so moving or removing one row does not rebuild the others.
   */
  const keys = $derived.by(() => {
    const seen = new Map<number, number>();
    return upNext.slice(0, shown).map((song) => {
      const n = seen.get(song.id) ?? 0;
      seen.set(song.id, n + 1);
      return `${song.id}:${n}`;
    });
  });

  // ── Reorder by dragging the handle ─────────────────────────────────────────
  let scroller = $state<HTMLElement | null>(null);
  let drag = $state<{
    pos: number;
    start: number;
    end: number;
    pointerId: number;
    y0: number;
    scroll0: number;
    clientY: number;
    rowH: number;
  } | null>(null);

  /** Bumped when an edge scroll moves the list under a still finger (scrollTop is not reactive). */
  let scrollTick = $state(0);
  /** How far the dragged row has travelled: the finger, plus what the list scrolled under it. */
  const dragDy = $derived.by(() => {
    void scrollTick;
    return drag ? drag.clientY - drag.y0 + ((scroller?.scrollTop ?? 0) - drag.scroll0) : 0;
  });
  const target = $derived.by(() => {
    if (!drag) return -1;
    const raw = drag.pos + Math.round(dragDy / drag.rowH);
    return Math.min(drag.end - 1, Math.max(drag.start, raw));
  });

  /** How far a row is moved while a drag is on: the dragged one follows the finger. */
  function offsetFor(pos: number): number {
    if (!drag) return 0;
    const { rowH } = drag;
    if (pos === drag.pos) {
      const min = (drag.start - drag.pos) * rowH;
      const max = (drag.end - 1 - drag.pos) * rowH;
      return Math.min(max, Math.max(min, dragDy));
    }
    if (target > drag.pos && pos > drag.pos && pos <= target) return -rowH;
    if (target < drag.pos && pos < drag.pos && pos >= target) return rowH;
    return 0;
  }

  function startDrag(e: PointerEvent, pos: number, section: Section) {
    if (!editable || drag || (e.pointerType === 'mouse' && e.button !== 0)) return;
    const handle = e.currentTarget as HTMLElement;
    const row = handle.closest('li');
    if (!row) return;
    e.preventDefault();
    // The row's swipe-to-remove listens on the row; a drag by the handle is not a swipe.
    e.stopPropagation();
    try {
      handle.setPointerCapture(e.pointerId);
    } catch {
      return;
    }
    drag = {
      pos,
      start: section.start,
      end: section.end,
      pointerId: e.pointerId,
      y0: e.clientY,
      scroll0: scroller?.scrollTop ?? 0,
      clientY: e.clientY,
      rowH: row.offsetHeight
    };
  }

  function moveDrag(e: PointerEvent) {
    if (!drag || e.pointerId !== drag.pointerId) return;
    drag.clientY = e.clientY;
    edgeScroll();
  }

  function endDrag(e: PointerEvent) {
    if (!drag || e.pointerId !== drag.pointerId) return;
    const from = drag.pos;
    const to = target;
    drag = null;
    stopEdgeScroll();
    if (e.type === 'pointerup' && to >= 0 && to !== from) playerStore.moveInQueue(from, to);
  }

  // Near the scroller's top or bottom edge a drag scrolls the list, so a row can travel further
  // than the screen. Frames are asked for only while that is happening.
  const EDGE_ZONE = 56;
  const EDGE_SPEED = 10;
  let edgeFrame: number | null = null;
  function edgeScroll() {
    if (edgeFrame !== null || !drag || !scroller) return;
    const step = () => {
      edgeFrame = null;
      if (!drag || !scroller) return;
      const rect = scroller.getBoundingClientRect();
      const y = drag.clientY;
      let delta = 0;
      if (y < rect.top + EDGE_ZONE) delta = -EDGE_SPEED;
      else if (y > rect.bottom - EDGE_ZONE) delta = EDGE_SPEED;
      if (delta === 0) return;
      const before = scroller.scrollTop;
      scroller.scrollTop = before + delta;
      if (scroller.scrollTop === before) return;
      scrollTick += 1;
      edgeFrame = requestAnimationFrame(step);
    };
    edgeFrame = requestAnimationFrame(step);
  }
  function stopEdgeScroll() {
    if (edgeFrame !== null) cancelAnimationFrame(edgeFrame);
    edgeFrame = null;
  }
  $effect(() => () => stopEdgeScroll());

  // ── Keyboard reorder, announced ────────────────────────────────────────────
  let announcement = $state('');
  function onHandleKeydown(e: KeyboardEvent, pos: number, section: Section, song: PlayerSong) {
    if (!editable) return;
    const step = e.key === 'ArrowUp' ? -1 : e.key === 'ArrowDown' ? 1 : 0;
    if (step === 0) return;
    e.preventDefault();
    const to = pos + step;
    if (to < section.start || to >= section.end) return;
    playerStore.moveInQueue(pos, to);
    announcement = `${song.title}, moved to ${to - section.start + 1} of ${section.end - section.start}`;
    // Keep the focus on the moved song's handle, which is now another row.
    requestAnimationFrame(() => {
      scroller?.querySelector<HTMLElement>(`[data-queue-handle="${to}"]`)?.focus();
    });
  }

  function remove(pos: number, song: PlayerSong) {
    playerStore.removeFromQueue(pos);
    announcement = `${song.title} removed from the queue`;
  }

  const moreCount = $derived(upNext.length - shown);
</script>

{#snippet sectionHeader(section: Section)}
  <div class="flex min-h-11 items-end justify-between gap-3 px-4 pt-4 pb-1.5">
    <h3 class="text-headline md:text-[15px] md:font-semibold">
      {section.kind === 'added' ? 'Next in queue' : 'Up next'}
    </h3>
    {#if section.kind === 'added' && editable}
      <button
        type="button"
        class="text-subheadline text-muted-foreground focus-visible:ring-ring pointer-fine:hover:text-foreground relative rounded-full px-1 font-medium after:absolute after:-inset-x-2 after:-inset-y-3 after:content-[''] focus-visible:ring-2 focus-visible:outline-hidden"
        onclick={() => playerStore.clearAddedToQueue()}
      >
        Clear queue
      </button>
    {/if}
  </div>
{/snippet}

{#snippet songText(song: PlayerSong)}
  <span class="flex min-w-0 flex-1 flex-col">
    <span class="text-body truncate md:text-sm md:font-medium">{song.title}</span>
    <span class="text-subheadline text-muted-foreground truncate md:text-[13px]">{song.artist}</span
    >
  </span>
{/snippet}

<div
  bind:this={scroller}
  class={cn(
    'no-scrollbar min-h-0 flex-1 overflow-y-auto overscroll-contain pb-6',
    '[mask-image:linear-gradient(to_bottom,transparent,#000_12px,#000_calc(100%-24px),transparent)]',
    wide ? 'pt-0' : 'pt-1'
  )}
>
  {#if current}
    <div class="flex min-h-11 items-end px-4 pt-2 pb-1.5">
      <h3 class="text-headline md:text-[15px] md:font-semibold">Now playing</h3>
    </div>
    <div class="flex h-16 items-center gap-3 px-4 md:h-14">
      <Cover
        artist={current.artist}
        title={current.album ?? current.title}
        coverUrl={current.coverUrl}
        size={48}
        corner={6}
        caption={false}
        dprCap={3}
        class="md:size-10!"
      />
      {@render songText(current)}
    </div>
  {/if}

  {#if upNext.length === 0}
    <EmptyState
      icon={ListMusic}
      title="Nothing up next"
      hint={editable
        ? 'Swipe right on a song, or choose Add to queue in its menu, to play it next.'
        : 'This music is playing on another device, and its queue is there.'}
    />
  {/if}

  {#each sections as section (`${section.kind}:${section.start}`)}
    {@render sectionHeader(section)}
    <ol class="relative" aria-label={section.kind === 'added' ? 'Next in queue' : 'Up next'}>
      {#each upNext.slice(section.start, section.end) as song, i (keys[section.start + i])}
        {@const pos = section.start + i}
        {@const dragging = drag?.pos === pos}
        {@const offset = offsetFor(pos)}
        <li
          use:swipeRow={{
            side: 'trailing',
            tone: 'destructive',
            icon: REMOVE_FROM_QUEUE_ICON,
            onswipe: () => remove(pos, song),
            disabled: !editable || drag !== null
          }}
          class={cn(
            'group relative flex h-16 items-center pr-1 md:h-14',
            dragging
              ? 'z-10 rounded-lg bg-[var(--np-fill)] shadow-lg backdrop-blur-xl'
              : drag && 'transition-transform duration-200 ease-out'
          )}
          style:transform={offset ? `translateY(${offset}px)` : undefined}
        >
          <button
            type="button"
            class="focus-visible:ring-ring flex h-full min-w-0 flex-1 items-center gap-3 rounded-lg pl-4 text-left focus-visible:ring-2 focus-visible:outline-hidden focus-visible:ring-inset active:bg-[var(--np-fill)]"
            aria-label={`Play ${song.title} by ${song.artist} now`}
            disabled={!editable}
            onclick={() => playerStore.skipToInQueue(pos)}
          >
            <Cover
              artist={song.artist}
              title={song.album ?? song.title}
              coverUrl={song.coverUrl}
              size={48}
              corner={6}
              caption={false}
              dprCap={3}
              class="md:size-10!"
            />
            {@render songText(song)}
          </button>
          {#if editable}
            <!-- With a pointer: shown on hover. On touch the swipe is the way, and this stays for
                 screen readers only. -->
            <button
              type="button"
              class="text-muted-foreground focus-visible:ring-ring pointer-fine:hover:text-foreground grid size-9 shrink-0 place-items-center rounded-full opacity-0 transition-opacity group-hover:opacity-100 focus-visible:opacity-100 focus-visible:ring-2 focus-visible:outline-hidden pointer-coarse:sr-only"
              aria-label={`Remove ${song.title} from the queue`}
              title="Remove from queue"
              onclick={() => remove(pos, song)}
            >
              <X class="size-4" />
            </button>
            <button
              type="button"
              data-queue-handle={pos}
              data-drag-ignore
              class={cn(
                'text-muted-foreground focus-visible:ring-ring grid size-11 shrink-0 touch-none place-items-center rounded-full select-none focus-visible:ring-2 focus-visible:outline-hidden',
                dragging ? 'text-foreground cursor-grabbing' : 'cursor-grab'
              )}
              aria-label={`Reorder ${song.title}`}
              aria-describedby="mh-queue-reorder-hint"
              onpointerdown={(e) => startDrag(e, pos, section)}
              onpointermove={moveDrag}
              onpointerup={endDrag}
              onpointercancel={endDrag}
              onkeydown={(e) => onHandleKeydown(e, pos, section, song)}
            >
              <Menu class="size-5" />
            </button>
          {/if}
        </li>
      {/each}
    </ol>
  {/each}

  {#if moreCount > 0}
    <div class="flex items-center justify-between gap-3 px-4 pt-3">
      <p class="text-footnote text-muted-foreground">
        and {moreCount.toLocaleString()} more {moreCount === 1 ? 'song' : 'songs'}
      </p>
      <button
        type="button"
        class="text-subheadline focus-visible:ring-ring bg-secondary pointer-fine:hover:bg-secondary-hover h-11 shrink-0 rounded-full px-4 font-medium focus-visible:outline-hidden focus-visible:ring-2 md:h-8 md:px-3"
        onclick={() => (listRows += LIST_ROWS)}
      >
        Show more
      </button>
    </div>
  {/if}

  <span id="mh-queue-reorder-hint" class="sr-only">Use the up and down arrow keys to move it.</span>
  <span class="sr-only" role="status" aria-live="polite">{announcement}</span>
</div>
