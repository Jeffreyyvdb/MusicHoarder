<script lang="ts">
  import type { LyricsDoc } from '#lib/lyrics/lyrics-doc.svelte.ts';
  import { parseLrc } from '#lib/lyrics/parse-lrc.ts';

  const {
    doc,
    currentTimeMs,
    secondarySynced,
    secondaryPlain
  }: {
    doc: LyricsDoc;
    currentTimeMs: number | null;
    secondarySynced?: string;
    secondaryPlain?: string;
  } = $props();

  const lines = $derived(parseLrc(doc.synced ?? ''));
  const secondary = $derived(parseLrc(secondarySynced ?? ''));
  const active = $derived.by(() => {
    if (currentTimeMs === null) return -1;
    let index = -1;
    for (let i = 0; i < lines.length; i++) {
      if (lines[i].timeMs > currentTimeMs) break;
      index = i;
    }
    return index;
  });
  const secondaryText = $derived(
    active < 0
      ? ''
      : secondary.length === lines.length
        ? secondary[active]?.text
        : secondary.find((line) => line.timeMs === lines[active]?.timeMs)?.text
  );
</script>

{#if lines.length > 0}
  {#if active >= 0 && lines[active].text}
    <p
      class="text-center text-xl leading-snug font-semibold text-balance whitespace-pre-line [text-shadow:0_2px_8px_#000] sm:text-3xl"
    >
      {lines[active].text}
    </p>
    {#if secondaryText}
      <p
        class="mt-2 text-center text-base text-balance text-white/80 [text-shadow:0_2px_8px_#000] sm:text-xl"
      >
        {secondaryText}
      </p>
    {/if}
  {/if}
{:else}
  <!-- svelte-ignore a11y_no_noninteractive_tabindex (Keyboard users need to scroll plain lyrics.) -->
  <div
    class="pointer-events-auto min-h-0 overflow-y-auto overscroll-contain text-center text-lg whitespace-pre-line [text-shadow:0_2px_8px_#000]"
    tabindex="0"
    role="region"
    aria-label="Lyrics"
    data-video-lyrics-scroll
    data-drag-ignore
  >
    {doc.plain || doc.synced || 'No lyrics available for this song.'}
    {#if secondaryPlain}<p class="mt-3 text-white/80">{secondaryPlain}</p>{/if}
  </div>
{/if}
