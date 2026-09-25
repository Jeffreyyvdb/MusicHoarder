<script lang="ts">
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import { SegmentedControl } from '$lib/components/ui/segmented-control';
  import type { AiLyrics, LyricsSource } from '$lib/lyrics/ai-lyrics.svelte';
  import type { LyricsDoc } from '$lib/lyrics/lyrics-doc.svelte';
  import LyricsCompareColumn from './LyricsCompareColumn.svelte';

  /**
   * "Compare versions" on a phone: a nested sheet over Now Playing with a LRCLIB | AI switch, one
   * version at a time, each with "Use as player default". It is mounted by TrackPanel itself, not
   * by the lyrics view, so ⋯ › Lyrics › Compare versions… opens it from the artwork or Info too.
   */
  type Props = {
    open: boolean;
    ai: AiLyrics;
    songId: number;
    lrclibDoc: LyricsDoc;
    aiDoc: LyricsDoc;
    isCurrentlyLoaded: boolean;
    lrclibUrl?: string;
  };
  let {
    open = $bindable(false),
    ai,
    songId,
    lrclibDoc,
    aiDoc,
    isCurrentlyLoaded,
    lrclibUrl
  }: Props = $props();

  let side = $state<LyricsSource>('lrclib');
  const items: { value: LyricsSource; label: string }[] = [
    { value: 'lrclib', label: 'LRCLIB' },
    { value: 'transcribed', label: 'AI' }
  ];
</script>

<BottomSheet.Root
  bind:open
  nested
  class="dark"
  title="Compare lyrics"
  description="Pick which version the player shows."
>
  {#snippet trailing()}
    <BottomSheet.Action prominent onclick={() => (open = false)}>Done</BottomSheet.Action>
  {/snippet}
  <div class="flex flex-col gap-3 px-4">
    <SegmentedControl {items} bind:value={side} label="Lyrics version" />
    <div class="flex h-[50svh] min-h-0 flex-col">
      <LyricsCompareColumn
        {ai}
        {songId}
        source={side}
        doc={side === 'lrclib' ? lrclibDoc : aiDoc}
        {isCurrentlyLoaded}
        lrclibUrl={side === 'lrclib' ? lrclibUrl : undefined}
      />
    </div>
  </div>
</BottomSheet.Root>
