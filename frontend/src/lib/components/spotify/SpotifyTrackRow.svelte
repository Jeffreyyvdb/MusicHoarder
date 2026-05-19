<script lang="ts">
  import type { SpotifyApiTrack, SpotifyLibraryMatchStatus } from '$lib/api-client';
  import { Button } from '$lib/components/ui/button';
  import * as Tooltip from '$lib/components/ui/tooltip';
  import {
    Music,
    CheckCircle2,
    Link2,
    ChevronDown,
    ChevronUp,
    Columns2,
    Download
  } from '@lucide/svelte';

  type Props = {
    track: SpotifyApiTrack;
    index: number;
    showDateAdded: boolean;
    expanded: boolean;
    onToggleExpand: () => void;
  };
  const { track, index, showDateAdded, expanded, onToggleExpand }: Props = $props();

  function formatDuration(ms: number): string {
    const totalSeconds = Math.floor(ms / 1000);
    const mins = Math.floor(totalSeconds / 60);
    const secs = totalSeconds % 60;
    return `${mins}:${secs.toString().padStart(2, '0')}`;
  }

  function formatDateAdded(dateStr: string): string {
    const date = new Date(dateStr);
    return date.toLocaleDateString(undefined, {
      month: 'short',
      day: 'numeric',
      year: 'numeric'
    });
  }

  function formatMatchConfidence(confidence: number | null | undefined): string {
    if (confidence == null || !Number.isFinite(confidence)) return '';
    const pct = confidence <= 1 ? Math.round(confidence * 100) : Math.round(confidence);
    return `${pct}%`;
  }

  const m = $derived(track.libraryMatch);
  const status = $derived(m?.matchStatus as SpotifyLibraryMatchStatus | undefined);
  const songId = $derived(m?.matchedSongId);
  const isPossible = $derived(status === 'PossibleMatch');
  const hasMatchInfo = $derived(Boolean(m && status));

  function handleKeyDown(e: KeyboardEvent) {
    if (!isPossible) return;
    if (e.key === 'Enter' || e.key === ' ') {
      e.preventDefault();
      onToggleExpand();
    }
  }
</script>

{#snippet rowContent()}
  <span class="text-muted-foreground w-8 shrink-0 text-right text-xs tabular-nums">
    {index + 1}
  </span>

  <div class="bg-secondary size-10 shrink-0 overflow-hidden rounded">
    {#if track.albumArt}
      <img
        src={track.albumArt}
        alt={track.album}
        class="size-full object-cover"
        crossorigin="anonymous"
      />
    {:else}
      <div class="flex size-full items-center justify-center">
        <Music class="text-muted-foreground size-4" />
      </div>
    {/if}
  </div>

  <div class="min-w-0 flex-1">
    <p class="truncate text-sm font-medium">{track.title}</p>
    <p class="text-muted-foreground truncate text-xs">{track.artist}</p>
  </div>

  <span class="text-muted-foreground hidden max-w-[200px] truncate text-sm md:block">
    {track.album}
  </span>

  {#if showDateAdded}
    <span class="text-muted-foreground hidden w-24 shrink-0 text-right text-xs lg:block">
      {formatDateAdded(track.addedAt)}
    </span>
  {/if}

  <span class="text-muted-foreground w-12 shrink-0 text-right text-xs">
    {formatDuration(track.durationMs)}
  </span>

  <div class="flex min-w-[120px] max-w-[168px] shrink-0 items-center justify-end gap-1">
    {#if !hasMatchInfo}
      <span
        class="text-muted-foreground hidden text-[10px] whitespace-nowrap sm:inline"
        title="Match pending"
      >
        —
      </span>
    {/if}
    {#if status === 'InLibrary' && songId != null}
      <Button
        size="sm"
        variant="outline"
        class="border-primary/40 bg-primary/15 text-primary hover:bg-primary/25 h-8 px-2.5 text-xs font-medium"
        href={`/app?song=${songId}`}
        onclick={(e: MouseEvent) => e.stopPropagation()}
      >
        <CheckCircle2 class="size-3.5 shrink-0" />
        In library
      </Button>
    {/if}
    {#if status === 'PossibleMatch' && songId != null}
      <div class="flex items-center gap-0.5">
        <Button
          size="sm"
          variant="outline"
          class="h-8 border-amber-500/45 bg-amber-500/15 px-2.5 text-xs font-medium text-amber-900 hover:bg-amber-500/25 dark:text-amber-200"
          title="Open best-guess local track"
          href={`/app?song=${songId}`}
          onclick={(e: MouseEvent) => e.stopPropagation()}
        >
          ~{formatMatchConfidence(m?.matchConfidence)}
          <Link2 class="size-3.5 shrink-0" />
        </Button>
        {#if expanded}
          <ChevronUp class="text-muted-foreground size-3.5 shrink-0" />
        {:else}
          <ChevronDown class="text-muted-foreground size-3.5 shrink-0" />
        {/if}
      </div>
    {/if}
    {#if status === 'NotInLibrary'}
      <Tooltip.Root>
        <Tooltip.Trigger>
          {#snippet child({ props })}
            <span {...props} class="inline-flex">
              <Button
                type="button"
                size="sm"
                variant="outline"
                disabled
                class="h-8 border-rose-500/35 bg-rose-500/10 px-2.5 text-xs font-medium text-rose-900 dark:text-rose-200"
              >
                <Download class="size-3.5 shrink-0" />
                Download
              </Button>
            </span>
          {/snippet}
        </Tooltip.Trigger>
        <Tooltip.Content side="left" class="max-w-[220px]">
          <span class="font-medium">Coming soon</span>
          <span class="mt-1 block text-[11px] leading-snug opacity-90">
            Track acquisition will be wired here later.
          </span>
        </Tooltip.Content>
      </Tooltip.Root>
    {/if}
  </div>
{/snippet}

<div class="rounded-lg">
  {#if isPossible}
    <div
      role="button"
      tabindex="0"
      onclick={onToggleExpand}
      onkeydown={handleKeyDown}
      class="group hover:bg-secondary/50 flex cursor-pointer items-center gap-3 px-3 py-2.5 transition-colors"
    >
      {@render rowContent()}
    </div>
  {:else}
    <div class="group hover:bg-secondary/50 flex items-center gap-3 px-3 py-2.5 transition-colors">
      {@render rowContent()}
    </div>
  {/if}

  {#if isPossible && expanded && songId != null}
    <div class="border-border/80 bg-secondary/20 border-t px-3 py-3 md:px-5">
      <p class="text-muted-foreground mb-2 flex items-center gap-1.5 text-xs font-medium">
        <Columns2 class="size-3.5" />
        Spotify vs local ({formatMatchConfidence(m?.matchConfidence)})
      </p>
      <div class="grid gap-3 md:grid-cols-2">
        <div class="border-border bg-card/50 space-y-1 rounded-lg border p-2.5">
          <p class="text-muted-foreground text-[10px] tracking-wide uppercase">Spotify</p>
          <p class="text-sm font-medium">{track.title}</p>
          <p class="text-muted-foreground text-xs">{track.artist}</p>
          <p class="text-muted-foreground text-xs">{track.album}</p>
        </div>
        <div class="border-border bg-card/50 space-y-1 rounded-lg border p-2.5">
          <div class="flex items-center justify-between gap-2">
            <p class="text-muted-foreground text-[10px] tracking-wide uppercase">MusicHoarder</p>
            <a
              href={`/app?song=${songId}`}
              class="text-primary inline-flex items-center gap-1 text-xs hover:underline"
            >
              Open
              <Link2 class="size-3" />
            </a>
          </div>
          <p class="text-sm font-medium">{m?.matchedTitle ?? '—'}</p>
          <p class="text-muted-foreground text-xs">{m?.matchedArtist ?? '—'}</p>
          {#if m?.matchedEnrichmentStatus}
            <p class="text-muted-foreground text-xs">
              Enrichment: {m.matchedEnrichmentStatus}
            </p>
          {/if}
        </div>
      </div>
    </div>
  {/if}
</div>
