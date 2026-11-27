<script lang="ts" module>
  export interface QueuePreviewEntry {
    id: number;
    title: string;
    artist: string;
    durationSeconds?: number | null;
    onSelect: () => void;
  }
</script>

<script lang="ts">
  import { ChevronDown } from '@lucide/svelte';
  import { untrack, type Snippet } from 'svelte';
  import { MediaQuery } from 'svelte/reactivity';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import NowPlayingArt from '#lib/components/file-browser/now-playing/NowPlayingArt.svelte';
  import VolumeControl from '#lib/components/file-browser/now-playing/VolumeControl.svelte';
  import {
    arrive,
    glide,
    type Stage
  } from '#lib/components/file-browser/now-playing/mode-motion.ts';
  import { skinHousing, useSkinOverride } from '#lib/hardware/hardware-mode.svelte.ts';
  import { formatDuration } from '#lib/formatters.ts';
  import { playerStore } from '#lib/stores/player.svelte.ts';
  import { cn } from '#lib/utils.ts';

  /**
   * The responsive Now Playing frame shared by the in-app overlay (`TrackPanel`) and the public
   * share page (`/share/[token]`): wide two-column (`lg`), phone-landscape (`sideways`), and
   * compact portrait — plus the SPEC-01 MH-1 Player console (`data-mh-console`), all `data-mh-np`
   * hooks, and the `arrive`/`glide` mode transitions. Keeping one layout component guarantees the
   * share player and the in-app player stay visually and structurally identical.
   */
  type Props = {
    wide: boolean;
    /** Active non-artwork stage (`null` while showing artwork/player). */
    stage: Stage;
    trackTitle: string;
    trackArtist: string;
    albumTitle: string;
    coverUrl: string | null;
    isCurrentlyLoaded: boolean;
    isCurrentlyPlaying: boolean;
    artPlaying: boolean;
    durationSeconds?: number | null;
    metaLine?: string;
    /** Music plays on another Connect device — hide this device's volume slider/knob. */
    elsewhere?: boolean;
    /** Show the MH-1 console's built-in LCD queue preview. */
    showQueuePreview?: boolean;
    queuePreview?: readonly QueuePreviewEntry[];
    /** When provided (in-app overlay): renders the grabber, close button and drag handles. */
    onClose?: () => void;
    /** Tap on the condensed top-bar track header returns to the artwork. */
    onShowArtwork: () => void;
    headerLeft?: Snippet;
    headerCenter?: Snippet;
    headerRight?: Snippet;
    /** Compact condensed top bar trailing item before `headerRight` (e.g. Heart in-app). */
    condensedRight?: Snippet;
    titleBlock: Snippet<[withHeart?: boolean]>;
    /** Extra controls inside MH-1's `data-mh-console-like` slot before Volume (e.g. Heart + Like). */
    consoleLike?: Snippet;
    transport: Snippet<[layout?: Snippet<[Snippet, Snippet, Snippet]>]>;
    bottomRow: Snippet;
    modeContent: Snippet;
  };
  const {
    wide,
    stage,
    trackTitle,
    trackArtist,
    albumTitle,
    coverUrl,
    isCurrentlyLoaded,
    isCurrentlyPlaying,
    artPlaying,
    durationSeconds = null,
    metaLine = '',
    elsewhere = false,
    showQueuePreview = false,
    queuePreview = [],
    onClose,
    onShowArtwork,
    headerLeft,
    headerCenter,
    headerRight,
    condensedRight,
    titleBlock,
    consoleLike,
    transport,
    bottomRow,
    modeContent
  }: Props = $props();

  // A phone in landscape: the manifest does not lock orientation, and the portrait stack (art,
  // title, a 190pt transport) overflows 393pt of height.
  const sideways = new MediaQuery('(orientation: landscape) and (max-height: 500px)');
  const skinForced = useSkinOverride();

  const playerOnly = $derived(wide && stage === null);

  // Mode changes, choreographed (`now-playing/mode-motion.ts`). At lg the player glides between
  // centred and left and the right column's view arrives. On a phone, and a phone on its side, the
  // new middle arrives, and the cover shrinks into the top bar's thumbnail or grows back out of it.
  let asideEl = $state<HTMLElement>();
  let playerBlockEl = $state<HTMLElement>();
  let columnEl = $state<HTMLElement>();
  let middleEl = $state<HTMLElement>();
  let artEl = $state<HTMLElement>();
  let readoutEl = $state<HTMLElement>();
  let thumbEl = $state<HTMLElement>();
  let moveFrom: DOMRect | undefined;
  let shownStage: Stage | undefined;
  let shownWide: boolean | undefined;
  $effect.pre(() => {
    const next = stage;
    const isWide = wide;
    untrack(() => {
      moveFrom = undefined;
      if (shownWide !== isWide || next === shownStage) return;
      const from = shownStage === null ? (isWide ? playerBlockEl : artEl) : undefined;
      const to = next === null ? (isWide ? playerBlockEl : thumbEl) : undefined;
      moveFrom = (from ?? to)?.getBoundingClientRect();
    });
  });
  $effect(() => {
    const isWide = wide;
    const next = stage;
    untrack(() => {
      const changed = shownWide === isWide && next !== shownStage;
      if (isWide) {
        if (moveFrom && asideEl && playerBlockEl) glide(asideEl, playerBlockEl, moveFrom);
        if (changed && next && columnEl) arrive(columnEl, shownStage === null);
      } else if (changed) {
        const cover = next === null ? artEl : thumbEl;
        if (moveFrom && cover) glide(cover, cover, moveFrom);
        if (next === null && moveFrom && readoutEl) arrive(readoutEl, false);
        else if (middleEl) arrive(middleEl, false);
      }
      moveFrom = undefined;
      shownStage = next;
      shownWide = isWide;
    });
  });
</script>

{#snippet closeButton()}
  {#if onClose}
    <Button
      variant="ghost"
      size="icon"
      onclick={onClose}
      class="focus-visible:ring-ring relative z-10 size-11 shrink-0 rounded-full hover:bg-transparent focus-visible:ring-2"
      aria-label="Close"
      title="Close (Esc)"
    >
      <ChevronDown class="size-7" />
    </Button>
  {/if}
{/snippet}

{#snippet coverArt(size: number, className: string, playing = artPlaying)}
  <NowPlayingArt
    artist={trackArtist}
    title={albumTitle}
    {coverUrl}
    {size}
    corner={12}
    {playing}
    class={className}
  />
{/snippet}

{#snippet playerDevice(art: Snippet, volumeClass: string)}
  {@const elapsed =
    isCurrentlyLoaded && playerStore.currentTime > 0
      ? formatDuration(playerStore.currentTime)
      : '0:00'}
  {@const upcoming = queuePreview.slice(0, wide ? 5 : 3)}
  <section data-mh-console aria-label="MH-1 Player">
    <div data-mh-console-plate data-playing={isCurrentlyPlaying || undefined} aria-hidden="true">
      <strong>MH-1</strong> Player
    </div>
    {#snippet layout(scrubber: Snippet, times: Snippet, buttons: Snippet)}
      <div data-mh-console-art>{@render art()}</div>
      <div data-mh-np="readout">
        {#if metaLine}<p data-mh-console-meta>{metaLine}</p>{/if}
        {@render titleBlock(false)}
        <span data-mh-console-clock aria-hidden="true">{elapsed}</span>
      </div>
      <div data-mh-console-rocker>{@render buttons()}</div>
      <div data-mh-console-like>
        {@render consoleLike?.()}
        {#if !elsewhere}
          <div class={volumeClass} data-mh-np="volume">
            <VolumeControl />
          </div>
        {/if}
      </div>
      <div data-mh-console-scrub data-drag-ignore>
        {@render scrubber()}
        {@render times()}
      </div>
    {/snippet}
    <div data-mh-np="keys">{@render transport(layout)}</div>
    {#if showQueuePreview}
      <div data-mh-console-queue>
        <div aria-hidden="true"><span>No</span><span>Title</span><span>Time</span></div>
        <ol aria-label="Queue preview">
          <li aria-current="true">
            <span>01</span><span>{trackTitle}</span><span>{formatDuration(durationSeconds)}</span>
          </li>
          {#each upcoming as entry, index (`${entry.id}:${index}`)}
            <li>
              <button
                type="button"
                onclick={entry.onSelect}
                aria-label={`Play ${entry.title} by ${entry.artist} now`}
              >
                <span>{String(index + 2).padStart(2, '0')}</span>
                <span>{entry.title} <small>{entry.artist}</small></span>
                <span>{formatDuration(entry.durationSeconds)}</span>
              </button>
            </li>
          {/each}
        </ol>
      </div>
    {/if}
  </section>
{/snippet}

<!-- Compact top bar, where a mouse can drag Now Playing down when dismissible (a touch can
     anywhere): the grabber (a tap closes too), ⌄, and the ⋯ menu. Outside the artwork the song is
     condensed into the bar — tap it to go back to the art. -->
{#snippet topBar()}
  <div class="shrink-0 touch-none select-none" data-drag-handle={onClose ? '' : undefined}>
    {#if onClose}
      <!-- 12pt tall with a 44pt hit strip (16pt above and below). It fades while a sheet is up
           over Now Playing, so two grabbers never stack (see the style block). -->
      <button
        type="button"
        tabindex="-1"
        aria-hidden="true"
        class="np-grabber relative mx-auto flex h-3 w-24 justify-center transition-opacity duration-200 after:absolute after:inset-x-0 after:-top-4 after:-bottom-4 focus-visible:outline-hidden"
        onclick={onClose}
      >
        <span class="bg-foreground/35 mt-1.5 block h-[5px] w-9 rounded-full"></span>
      </button>
    {/if}
    <div class="flex h-14 items-center gap-1 px-2">
      {#if onClose}
        {@render closeButton()}
      {:else if stage === null && headerLeft}
        {@render headerLeft()}
      {/if}
      {#if stage !== null}
        <button
          type="button"
          class={cn(
            'focus-visible:ring-ring relative z-10 flex min-w-0 flex-1 items-center gap-3 rounded-lg py-1 text-left focus-visible:ring-2 focus-visible:outline-hidden',
            !onClose && 'pl-2'
          )}
          aria-label={`${trackTitle} by ${trackArtist}. Show the artwork`}
          onclick={onShowArtwork}
        >
          <span class="flex shrink-0" bind:this={thumbEl}>
            <NowPlayingArt
              artist={trackArtist}
              title={albumTitle}
              {coverUrl}
              size={44}
              corner={6}
              class="shrink-0 !shadow-[0_2px_8px_rgb(0_0_0/0.35)]"
            />
          </span>
          <span class="flex min-w-0 flex-col">
            <span class="text-headline truncate">{trackTitle}</span>
            <span class="text-subheadline text-muted-foreground truncate">{trackArtist}</span>
          </span>
        </button>
        {@render condensedRight?.()}
      {:else if headerCenter}
        {@render headerCenter()}
      {:else}
        <div class="flex-1"></div>
      {/if}
      {@render headerRight?.()}
    </div>
  </div>
{/snippet}

<!-- A window wide enough for two columns is not dragged down, as on an iPad's full screen. -->
<div class="flex h-full max-h-full min-h-0 flex-col" data-drag-ignore={wide || undefined}>
  {#if wide}
    {@const mh1 = skinHousing(playerDevice, skinForced())}
    <!-- lg: close (or brand) and ⋯ in a bar; art, title and transport on the left; the mode on the right. -->
    <header class="flex shrink-0 items-center justify-between px-5 pt-3 pb-1">
      {#if onClose}
        {@render closeButton()}
      {:else if headerLeft}
        {@render headerLeft()}
      {:else}
        <div class="w-11 shrink-0"></div>
      {/if}
      {#if headerCenter}
        {@render headerCenter()}
      {:else}
        <div class="flex-1"></div>
      {/if}
      {@render headerRight?.()}
    </header>
    <div
      class={cn(
        'grid min-h-0 flex-1 gap-12 px-12 pb-8 xl:gap-16',
        playerOnly
          ? 'grid-cols-[minmax(0,560px)] justify-center'
          : 'grid-cols-[minmax(300px,380px)_minmax(0,1fr)] xl:grid-cols-[400px_minmax(0,1fr)]'
      )}
      data-mh-np-layout={playerOnly ? 'solo' : 'split'}
    >
      <!-- The column scrolls only when the window is too short even for the smaller art. Its box
           reaches 48px past the column on either side and down into the grid's bottom padding —
           a scroll container clips its contents, and the art's 60px shadow would otherwise end
           in hard edges at the column's sides. `my-auto` (not justify-center) centres the block
           while it fits and lets it overflow downward only, so its top is never unreachable —
           which is why the ScrollArea's content element is made a flex column. -->
      <aside class="-mx-12 -mb-8 flex min-h-0 flex-col" bind:this={asideEl}>
        <ScrollArea
          class="min-h-0 flex-1 [&>[data-slot=scroll-area-viewport]>*]:flex [&>[data-slot=scroll-area-viewport]>*]:flex-col"
          viewportClass="px-12 pt-6 pb-8"
        >
          {#if mh1}
            <div class="my-auto" bind:this={playerBlockEl}>
              {#snippet art()}{@render coverArt(400, 'w-full', true)}{/snippet}
              {@render mh1(art, 'hidden pointer-fine:block')}
              <div class="mt-4" data-mh-np="modes">{@render bottomRow()}</div>
            </div>
          {:else}
            <div
              class="my-auto flex w-full flex-col"
              data-mh-np="body"
              data-playing={isCurrentlyPlaying || undefined}
              bind:this={playerBlockEl}
            >
              <!-- The art gives way first on a short window (the transport, title and volume below it
                 need ~430px), and never shrinks under 200px. -->
              <div data-mh-np="screen">
                {@render coverArt(
                  400,
                  cn(
                    'mx-auto w-full',
                    playerOnly
                      ? 'max-w-[min(100%,56vh,max(200px,calc(100svh-var(--mh-np-chrome,460px))))]'
                      : 'max-w-[min(100%,46vh,max(200px,calc(100svh-var(--mh-np-chrome,460px))))]'
                  )
                )}
              </div>
              <div class="mt-6" data-mh-np="readout">
                {@render titleBlock()}
                {#if metaLine}
                  <p class="text-footnote text-muted-foreground mt-1.5 tabular-nums">{metaLine}</p>
                {/if}
              </div>
              <div class="mt-5" data-mh-np="keys">{@render transport()}</div>
              <div class="mt-2" data-mh-np="modes">{@render bottomRow()}</div>
              {#if !elsewhere}
                <div class="mt-4 hidden pointer-fine:block" data-mh-np="volume">
                  <VolumeControl />
                </div>
              {/if}
            </div>
          {/if}
        </ScrollArea>
      </aside>
      <div class={cn('flex min-h-0 flex-col pt-2', playerOnly && 'hidden')} bind:this={columnEl}>
        {@render modeContent()}
      </div>
    </div>
  {:else if sideways.current}
    <!-- A phone on its side: the middle on the left, the title block and the transport on the
         right. Same top bar, same modes. -->
    <div
      class="flex h-full min-h-0 w-full flex-col"
      data-mh-np="body"
      data-playing={isCurrentlyPlaying || undefined}
    >
      {@render topBar()}
      <div class="flex min-h-0 flex-1 gap-6 pr-6 pl-4">
        <div class="flex min-h-0 min-w-0 flex-1 flex-col" bind:this={middleEl}>
          {#if stage === null}
            <div
              class="flex min-h-0 flex-1 touch-none items-center justify-center pb-3 select-none"
              data-drag-handle={onClose ? '' : undefined}
            >
              <div class="w-[min(100%,calc(100svh-120px))]" data-mh-np="screen" bind:this={artEl}>
                <NowPlayingArt
                  artist={trackArtist}
                  title={albumTitle}
                  {coverUrl}
                  size={360}
                  corner={12}
                  playing={artPlaying}
                  class="w-full"
                />
              </div>
            </div>
          {/if}
          {@render modeContent()}
        </div>
        <!-- Scrolls only if the title block carries chips on the shortest phones. -->
        <div class="no-scrollbar flex w-[min(360px,46%)] shrink-0 flex-col overflow-y-auto pb-2">
          <div class="my-auto">
            {#if stage === null}
              <div class="mb-3" data-mh-np="readout" bind:this={readoutEl}>
                {@render titleBlock()}
              </div>
            {/if}
            <div data-mh-np="keys">{@render transport()}</div>
            <div class="mt-1" data-mh-np="modes">{@render bottomRow()}</div>
          </div>
        </div>
      </div>
    </div>
  {:else}
    {@const device = stage === null ? skinHousing(playerDevice, skinForced()) : undefined}
    <div
      class="mx-auto flex h-full min-h-0 w-full max-w-xl flex-col"
      data-mh-np="body"
      data-playing={isCurrentlyPlaying || undefined}
    >
      {@render topBar()}

      <div class="flex min-h-0 flex-1 flex-col" bind:this={middleEl}>
        {#if device}
          <div class="flex min-h-0 flex-1 flex-col overflow-y-auto px-4 py-3">
            <div class="my-auto">
              {#snippet art()}{@render coverArt(360, 'w-full', true)}{/snippet}
              {@render device(art, 'hidden md:pointer-fine:block')}
            </div>
          </div>
        {:else if stage === null}
          <!-- The artwork and title, where a mouse can drag Now Playing down too. -->
          <div
            class="flex min-h-0 flex-1 touch-none flex-col justify-center px-8 select-none"
            data-drag-handle={onClose ? '' : undefined}
          >
            <div
              class="mx-auto flex w-full max-w-[min(100%,40svh,329px)] flex-col md:max-w-[min(100%,40svh,440px)]"
            >
              <div data-mh-np="screen" bind:this={artEl}>
                {@render coverArt(360, 'w-full')}
              </div>
              <div class="mt-7" data-mh-np="readout" bind:this={readoutEl}>
                {@render titleBlock()}
              </div>
            </div>
          </div>
        {/if}
        {@render modeContent()}
      </div>

      <footer class="shrink-0 px-7 pt-3 pb-2">
        {#if !device}
          <div data-mh-np="keys">{@render transport()}</div>
        {/if}
        <div class="mt-1" data-mh-np="modes">{@render bottomRow()}</div>
        <!-- md–lg with a mouse (a narrow desktop window, an iPad with a trackpad): Now Playing hides
             the mini player that holds the volume, so it needs its own here too. -->
        {#if !elsewhere && !device}
          <div class="mt-3 hidden md:pointer-fine:block" data-mh-np="volume">
            <VolumeControl />
          </div>
        {/if}
      </footer>
    </div>
  {/if}
</div>

<style>
  /* While a sheet is up over Now Playing (compare, manage video, a confirmation), it stops 12pt
     short of the top, and the overlay's own grabber would peek out above the sheet's. Every sheet
     that can be open while this overlay is up is nested over it, and sheets are portaled to the
     body, so ask the body. */
  :global(body:has([data-slot='bottom-sheet-content'][data-state='open'])) .np-grabber {
    opacity: 0;
  }
</style>
