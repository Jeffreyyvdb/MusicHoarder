<script lang="ts">
  import { Check } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import LyricsPanel from '$lib/components/file-browser/LyricsPanel.svelte';
  import type { AiLyrics, LyricsSource } from '$lib/lyrics/ai-lyrics.svelte';
  import type { LyricsDoc } from '$lib/lyrics/lyrics-doc.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn } from '$lib/utils';

  /**
   * One version in "Compare versions": its name, the "Use as player default" choice (or a
   * "Player default ✓" once it is), and the lyrics as the boxed panel with its status chrome.
   * The lg split shows two side by side; the phone's sheet shows one at a time.
   */
  type Props = {
    ai: AiLyrics;
    songId: number;
    source: LyricsSource;
    doc: LyricsDoc;
    isCurrentlyLoaded: boolean;
    lrclibUrl?: string;
    class?: string;
  };
  const {
    ai,
    songId,
    source,
    doc,
    isCurrentlyLoaded,
    lrclibUrl,
    class: className
  }: Props = $props();

  const label = $derived(
    source === 'lrclib' ? 'LRCLIB' : `AI · ${ai.transcription?.model ?? 'whisper'}`
  );
  const seek = $derived(
    isCurrentlyLoaded ? (timeMs: number) => playerStore.seek(timeMs / 1000) : undefined
  );
  const currentTimeMs = $derived(isCurrentlyLoaded ? playerStore.currentTime * 1000 : null);
</script>

<div class={cn('flex min-h-0 flex-1 flex-col gap-2', className)}>
  <div class="flex min-h-11 items-center justify-between gap-2 px-1 md:min-h-8">
    <span class="text-footnote text-muted-foreground font-semibold">{label}</span>
    {#if ai.preferredSource === source}
      <span class="text-footnote text-muted-foreground inline-flex items-center gap-1">
        <Check class="text-primary size-3.5" /> Player default
      </span>
    {:else}
      <Button
        variant="tinted"
        class="h-8 rounded-full px-3 pointer-coarse:h-11 pointer-coarse:px-4"
        disabled={ai.preferSaving}
        onclick={() => ai.setPreferred(source)}
      >
        Use as player default
      </Button>
    {/if}
  </div>
  {#if source === 'lrclib'}
    <LyricsPanel variant="panel" {songId} {doc} {currentTimeMs} onSeek={seek} {lrclibUrl} />
  {:else}
    {#key ai.transcription?.at}
      <LyricsPanel variant="panel" {songId} {doc} {currentTimeMs} onSeek={seek} />
    {/key}
  {/if}
</div>
