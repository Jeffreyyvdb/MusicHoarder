<script lang="ts">
  import { AlertTriangle, CircleCheck, Languages, Loader2, Sparkles, Timer } from '@lucide/svelte';
  import { Badge } from '$lib/components/ui/badge';
  import { Button } from '$lib/components/ui/button';
  import { SegmentedControl } from '$lib/components/ui/segmented-control';
  import LyricsPanel from '$lib/components/file-browser/LyricsPanel.svelte';
  import type { ApiSong } from '$lib/api-client';
  import type { AiLyrics, LyricsViewMode } from '$lib/lyrics/ai-lyrics.svelte';
  import type { LyricsDoc } from '$lib/lyrics/lyrics-doc.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn } from '$lib/utils';
  import LyricsCompareColumn from './LyricsCompareColumn.svelte';

  /**
   * Lyrics mode: the karaoke viewer filling the middle of Now Playing, with a quiet status row
   * above it for what used to be scattered (and partly lg-only): the AI run's progress, the
   * "already in English" note, the outdated-translation warning, and — for admins — the timing
   * verdict with its "Check the timing" button. The Original / Pronunciation / Translation
   * control sits here too when a translation exists.
   *
   * Compare: at lg the two versions split the column side by side (as before). On a phone it is
   * a nested sheet (LyricsCompareSheet) that TrackPanel mounts, so it opens from any mode.
   */
  type Props = {
    ai: AiLyrics;
    song: ApiSong;
    /** The stored LRCLIB lyrics (timing verdict, re-check). */
    lrclibDoc: LyricsDoc;
    /** The AI transcription as a document (for the compare sheet / split). */
    aiDoc: LyricsDoc;
    isOwner: boolean;
    isCurrentlyLoaded: boolean;
    /** lg two-column layout: compare opens as a split in this column. */
    wide: boolean;
    lrclibUrl: string | undefined;
  };
  const { ai, song, lrclibDoc, aiDoc, isOwner, isCurrentlyLoaded, wide, lrclibUrl }: Props =
    $props();

  const viewDoc = $derived(ai.showAiInViewer ? aiDoc : lrclibDoc);
  const seek = $derived(
    isCurrentlyLoaded ? (timeMs: number) => playerStore.seek(timeMs / 1000) : undefined
  );
  const currentTimeMs = $derived(isCurrentlyLoaded ? playerStore.currentTime * 1000 : null);

  const viewItems: { value: LyricsViewMode; label: string }[] = [
    { value: 'original', label: 'Original' },
    { value: 'pronunciation', label: 'Pronunciation' },
    { value: 'translation', label: 'Translation' }
  ];
  const showViewControl = $derived(ai.hasTranslation && !ai.comparing);

  // What the status row has to say, most urgent first; at most one line of AI state.
  const aiLine = $derived.by(() => {
    if (ai.enhanceState === 'transcribing')
      return { busy: true, text: 'Step 1 of 2 — listening to the audio to sync the lyrics…' };
    if (ai.enhanceState === 'translating')
      return { busy: true, text: 'Step 2 of 2 — writing the pronunciation guide and translation…' };
    if (ai.enhanceState === 'success') return { done: true, text: 'Done' };
    if (ai.translationStale && ai.hasTranslation)
      return {
        warn: true,
        text: 'Lyrics changed — pronunciation and translation are outdated. Run it again to refresh.'
      };
    if (ai.translationIsEnglish) return { text: 'Lyrics are already in English.' };
    return null;
  });
  const timingSuspect = $derived(
    isOwner && lrclibDoc.hasSynced && lrclibDoc.syncStatus === 'Suspect'
  );
  const showTimingRow = $derived(
    isOwner && lrclibDoc.hasSynced && (timingSuspect || lrclibDoc.verifyMessage !== null)
  );
  // lg keeps the old control bar's "Player shows" line: it is the answer to "which lyrics am I
  // looking at?" when both exist.
  const sourceLine = $derived.by(() => {
    if (!wide || !isOwner) return null;
    if (ai.canCompare)
      return `Player shows: ${ai.preferredSource === 'transcribed' ? `AI-synced · ${ai.transcription?.model ?? 'whisper'}` : 'LRCLIB'}`;
    if (ai.transcription)
      return `AI transcription${ai.transcription.model ? ` · ${ai.transcription.model}` : ''}`;
    return null;
  });
  const hasStatus = $derived(
    Boolean(aiLine) ||
      Boolean(ai.enhanceError) ||
      Boolean(ai.enhanceNote) ||
      showTimingRow ||
      Boolean(sourceLine) ||
      ai.comparing
  );
</script>

<div class={cn('flex min-h-0 flex-1 flex-col gap-3', !wide && 'px-6')}>
  {#if hasStatus || showViewControl}
    <div
      class={cn(
        'flex shrink-0 flex-col gap-2',
        wide ? 'mx-auto w-full max-w-3xl px-1' : 'items-center text-center'
      )}
    >
      {#if aiLine}
        <p class="text-footnote text-muted-foreground flex items-center gap-1.5" role="status">
          {#if aiLine.busy}
            <Loader2 class="size-3.5 shrink-0 animate-spin" />
          {:else if aiLine.done}
            <CircleCheck class="text-primary size-3.5 shrink-0" />
          {:else if aiLine.warn}
            <AlertTriangle class="text-warning-text size-3.5 shrink-0" />
          {:else}
            <Languages class="size-3.5 shrink-0" />
          {/if}
          <span>{aiLine.text}</span>
        </p>
      {/if}
      {#if ai.enhanceError}
        <p class="text-footnote text-destructive-text" role="alert">{ai.enhanceError}</p>
      {:else if ai.enhanceNote}
        <p class="text-footnote text-muted-foreground">{ai.enhanceNote}</p>
      {/if}
      {#if sourceLine && !ai.comparing}
        <p class="text-footnote text-muted-foreground flex items-center gap-1.5">
          <Sparkles class="size-3.5 shrink-0" />
          {sourceLine}
        </p>
      {/if}
      {#if showTimingRow}
        <!-- The timing verdict and the button that settles it. Admin-only: verifying is a server
             write that can rewrite the stored LRC, and a Suspect verdict is a maintenance signal
             about the library rather than something a listener can act on. -->
        <div class={cn('flex flex-wrap items-center gap-2', !wide && 'justify-center')}>
          {#if timingSuspect}
            <Badge variant="warning" title={lrclibDoc.syncIssue ?? undefined}>
              <AlertTriangle />
              Timing looks wrong
            </Badge>
            <Button
              variant="subtle"
              size="sm"
              class="gap-1.5 pointer-coarse:h-11 pointer-coarse:px-4"
              disabled={lrclibDoc.verifyState === 'checking'}
              onclick={() => lrclibDoc.verifyTiming()}
            >
              {#if lrclibDoc.verifyState === 'checking'}
                <Loader2 class="size-3.5 animate-spin" />
                Listening…
              {:else}
                <Timer class="size-3.5" />
                Check the timing
              {/if}
            </Button>
          {/if}
        </div>
        {#if timingSuspect && lrclibDoc.syncIssue}
          <p class="text-footnote text-muted-foreground">{lrclibDoc.syncIssue}</p>
        {/if}
        {#if lrclibDoc.verifyMessage}
          <p class="text-footnote text-muted-foreground" role="status">{lrclibDoc.verifyMessage}</p>
        {/if}
      {/if}
      {#if wide && ai.comparing}
        <div class="flex items-center justify-between gap-2">
          <p class="text-footnote text-muted-foreground">
            Compare the two versions, then pick the player's default.
          </p>
          <Button
            variant="gray"
            class="h-8 rounded-full px-4 pointer-coarse:h-11"
            onclick={() => ai.toggleCompare()}
          >
            Done
          </Button>
        </div>
      {/if}
      {#if showViewControl}
        <SegmentedControl
          items={viewItems}
          value={ai.lyricsView}
          onValueChange={(v) => ai.setLyricsView(v)}
          label="Lyrics view"
          class={cn(wide ? 'w-auto self-center' : 'max-w-sm')}
        />
      {/if}
    </div>
  {/if}

  {#if wide && ai.comparing}
    <!-- Side by side: LRCLIB vs AI, each with its own player-default chooser. -->
    <div class="flex min-h-0 w-full flex-1 gap-4">
      <LyricsCompareColumn
        {ai}
        songId={song.id}
        source="lrclib"
        doc={lrclibDoc}
        {isCurrentlyLoaded}
        {lrclibUrl}
      />
      <LyricsCompareColumn
        {ai}
        songId={song.id}
        source="transcribed"
        doc={aiDoc}
        {isCurrentlyLoaded}
      />
    </div>
  {:else}
    <!-- The big synced viewer, showing the chosen default (AI when preferred or the only option,
         else LRCLIB). The mask fades lines out under the chrome at both edges, as Apple Music's
         lyrics do. -->
    <div
      class={cn(
        'flex min-h-0 flex-1 flex-col [mask-image:linear-gradient(to_bottom,transparent,#000_24px,#000_calc(100%-40px),transparent)] pt-3',
        wide && 'mx-auto w-full max-w-3xl'
      )}
    >
      {#key ai.viewerKey}
        <LyricsPanel
          variant="theater"
          songId={song.id}
          doc={viewDoc}
          isInstrumental={song.isInstrumental ?? undefined}
          {currentTimeMs}
          onSeek={seek}
          secondarySynced={ai.secondarySynced}
          secondaryPlain={ai.secondaryPlain}
        />
      {/key}
    </div>
  {/if}
</div>
