<script lang="ts">
  import type { SpotifyApiTrack, SpotifyLibraryMatchStatus } from '$lib/api-client';
  import { Button } from '$lib/components/ui/button';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { formatDate } from '$lib/formatters';
  import {
    Music,
    CircleCheck,
    Circle,
    Link2,
    ChevronDown,
    ChevronUp,
    Columns2,
    Gift
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
  const inWishlist = $derived(track.isInWishlist === true);
  const pct = $derived(formatMatchConfidence(m?.matchConfidence));

  function openInLibrary(e: MouseEvent) {
    e.stopPropagation();
    if (songId != null) songDetail.open(songId);
  }

  // A possible match can be compared side by side. The disclosure is its own button with a
  // visible chevron (the row used to expand on a tap with nothing saying it would, and was a
  // role=button holding the ~NN% button — two controls nested in one). "~NN%" stays the one
  // way to open the song.
  const compareId = $props.id();
  function toggleCompare(e: MouseEvent) {
    e.stopPropagation();
    onToggleExpand();
  }
</script>

{#snippet disclosure(extra: string)}
  <button
    type="button"
    aria-expanded={expanded}
    aria-controls={compareId}
    aria-label={expanded ? 'Hide the comparison' : 'Compare with the possible match'}
    class="text-muted-foreground hover:text-foreground focus-visible:ring-ring/50 relative grid shrink-0 place-items-center rounded-full outline-none focus-visible:ring-3 {extra}"
    onclick={toggleCompare}
  >
    {#if expanded}
      <ChevronUp class="size-full" aria-hidden="true" />
    {:else}
      <ChevronDown class="size-full" aria-hidden="true" />
    {/if}
  </button>
{/snippet}

<!--
  The library column. Desktop keeps its labelled pills; a phone gets ONE trailing glyph per state
  (a check in the library, "~87%" for a possible match, the wishlist's gift, an empty circle for
  not in the library), each with its word for VoiceOver — so the title keeps the width instead of
  a status column taking a third of the row.
-->
{#snippet libraryCompact()}
  {#if status === 'InLibrary' && songId != null}
    <button
      type="button"
      aria-label="In library — open the song"
      class="text-primary focus-visible:ring-ring/50 relative -mr-1 grid size-8 shrink-0 place-items-center rounded-full outline-none after:absolute after:-inset-1.5 focus-visible:ring-3"
      onclick={openInLibrary}
    >
      <CircleCheck class="size-[22px]" aria-hidden="true" />
    </button>
  {:else if status === 'PossibleMatch' && songId != null}
    <span class="flex shrink-0 items-center gap-1">
      <button
        type="button"
        aria-label="Possible match, {pct} — open the best guess"
        class="text-warning-text text-subheadline relative shrink-0 font-medium tabular-nums outline-none after:absolute after:-inset-x-1 after:-inset-y-3 focus-visible:underline"
        onclick={openInLibrary}
      >
        ~{pct}
      </button>
      <!-- 20px glyph, 44pt target (the pseudo-element). -->
      {@render disclosure('-mr-1 size-6 p-0.5 after:absolute after:-inset-2.5')}
    </span>
  {:else if inWishlist}
    <span role="img" aria-label="On your wishlist" class="text-muted-foreground shrink-0">
      <Gift class="size-5" aria-hidden="true" />
    </span>
  {:else if status === 'NotInLibrary'}
    <span role="img" aria-label="Not in library" class="text-muted-foreground-dim shrink-0">
      <Circle class="size-5" aria-hidden="true" />
    </span>
  {:else if !hasMatchInfo}
    <span class="text-muted-foreground-dim shrink-0">
      <span aria-hidden="true">—</span><span class="sr-only">Match pending</span>
    </span>
  {/if}
{/snippet}

{#snippet libraryWide()}
  <div class="flex max-w-[230px] min-w-[120px] shrink-0 items-center justify-end gap-1">
    {#if inWishlist && status !== 'InLibrary'}
      <!-- Once the track is in the library the "In library" affordance is primary; the wishlist
           pill is redundant there and crowds the narrow container, so only show it otherwise. -->
      <span
        class="bg-secondary text-foreground inline-flex h-7 items-center gap-1 rounded-md px-2 text-[11px] font-medium"
      >
        <Gift class="size-3.5 shrink-0" aria-hidden="true" />
        Wishlist
      </span>
    {/if}
    {#if !hasMatchInfo && !inWishlist}
      <span class="text-muted-foreground text-[11px] whitespace-nowrap">
        <span aria-hidden="true">—</span><span class="sr-only">Match pending</span>
      </span>
    {/if}
    {#if status === 'InLibrary' && songId != null}
      <Button
        size="sm"
        variant="tinted"
        class="h-7 gap-1 px-2.5 text-xs font-medium"
        title="Open this song in your library"
        onclick={openInLibrary}
      >
        <CircleCheck class="size-3.5 shrink-0" />
        In library
      </Button>
    {/if}
    {#if status === 'PossibleMatch' && songId != null}
      <div class="flex items-center gap-0.5">
        <Button
          size="sm"
          variant="gray"
          class="text-warning-text hover:text-warning-text h-7 gap-1 px-2.5 text-xs font-medium tabular-nums"
          title="Open best-guess local track"
          onclick={openInLibrary}
        >
          ~{pct}
          <Link2 class="size-3.5 shrink-0" />
        </Button>
        {@render disclosure('size-7 p-1.5')}
      </div>
    {/if}
    {#if status === 'NotInLibrary' && !inWishlist}
      <!-- Was a permanently disabled Download button explained only by a hover tooltip — a tap on
           a phone did nothing at all. Until per-track download exists, state the fact instead. -->
      <span class="text-muted-foreground text-[11px] whitespace-nowrap">Not in library</span>
    {/if}
  </div>
{/snippet}

{#snippet rowContent()}
  <span class="text-muted-foreground hidden w-8 shrink-0 text-right text-xs tabular-nums md:inline">
    {index + 1}
  </span>

  <div class="bg-muted size-11 shrink-0 overflow-hidden rounded-sm md:size-10">
    {#if track.albumArt}
      <img
        src={track.albumArt}
        alt=""
        loading="lazy"
        draggable="false"
        class="size-full object-cover"
        crossorigin="anonymous"
      />
    {:else}
      <div class="flex size-full items-center justify-center">
        <Music class="text-muted-foreground size-4" aria-hidden="true" />
      </div>
    {/if}
  </div>

  <!-- On a phone the hairline hangs off the text column, so it starts where the text does. -->
  <div
    class="after:bg-separator relative flex min-h-14 min-w-0 flex-1 items-center gap-3 self-stretch py-2 pr-4 after:absolute after:inset-x-0 after:bottom-0 after:h-(--hairline) group-last/track:after:hidden md:min-h-0 md:py-0 md:pr-0 md:after:hidden"
  >
    <div class="min-w-0 flex-1">
      <p class="text-body truncate md:text-sm md:font-medium">{track.title}</p>
      <p class="text-subheadline text-muted-foreground truncate md:text-xs">
        {track.artist}<span class="md:hidden">{track.album ? ` · ${track.album}` : ''}</span>
      </p>
    </div>

    <span class="text-muted-foreground hidden max-w-[200px] truncate text-sm md:block">
      {track.album}
    </span>

    {#if showDateAdded}
      <span class="text-muted-foreground hidden w-24 shrink-0 text-right text-xs lg:block">
        {formatDate(track.addedAt)}
      </span>
    {/if}

    <span
      class="text-footnote text-muted-foreground w-10 shrink-0 text-right tabular-nums md:w-12 md:text-xs"
    >
      {formatDuration(track.durationMs)}
    </span>

    <div class="flex items-center md:hidden">{@render libraryCompact()}</div>
    <div class="hidden md:contents">{@render libraryWide()}</div>
  </div>
{/snippet}

<div class="group/track">
  {#if isPossible}
    <!-- A click anywhere on the row is a pointer shortcut for the disclosure button, which is
         the control keyboard and VoiceOver users get; so the row itself carries no role. -->
    <!-- svelte-ignore a11y_click_events_have_key_events, a11y_no_static_element_interactions -->
    <div
      onclick={onToggleExpand}
      class="active:bg-accent md:hover:bg-accent flex cursor-pointer items-center gap-3 pl-4 transition-colors md:rounded-lg md:px-3 md:py-2.5"
    >
      {@render rowContent()}
    </div>
  {:else}
    <div
      class="md:hover:bg-accent flex items-center gap-3 pl-4 transition-colors md:rounded-lg md:px-3 md:py-2.5"
    >
      {@render rowContent()}
    </div>
  {/if}

  {#if isPossible && expanded && songId != null}
    <div id={compareId} class="bg-muted mx-4 mb-3 rounded-xl p-3 md:mx-3 md:px-4">
      <p class="text-footnote text-muted-foreground mb-2 flex items-center gap-1.5 font-medium">
        <Columns2 class="size-3.5" aria-hidden="true" />
        Spotify vs local ({pct})
      </p>
      <div class="grid gap-2 md:grid-cols-2 md:gap-3">
        <div class="bg-card space-y-0.5 rounded-lg p-3">
          <p class="text-caption-1 text-muted-foreground">Spotify</p>
          <p class="text-subheadline font-medium md:text-sm">{track.title}</p>
          <p class="text-footnote text-muted-foreground md:text-xs">{track.artist}</p>
          <p class="text-footnote text-muted-foreground md:text-xs">{track.album}</p>
        </div>
        <div class="bg-card space-y-0.5 rounded-lg p-3">
          <div class="flex items-center justify-between gap-2">
            <p class="text-caption-1 text-muted-foreground">MusicHoarder</p>
            <button
              type="button"
              onclick={openInLibrary}
              class="text-primary text-footnote relative inline-flex items-center gap-1 outline-none after:absolute after:-inset-x-2 after:-inset-y-3 hover:underline focus-visible:underline md:text-xs"
            >
              Open
              <Link2 class="size-3" aria-hidden="true" />
            </button>
          </div>
          <p class="text-subheadline font-medium md:text-sm">{m?.matchedTitle ?? '—'}</p>
          <p class="text-footnote text-muted-foreground md:text-xs">{m?.matchedArtist ?? '—'}</p>
          {#if m?.matchedEnrichmentStatus}
            <p class="text-footnote text-muted-foreground md:text-xs">
              Enrichment: {m.matchedEnrichmentStatus}
            </p>
          {/if}
        </div>
      </div>
    </div>
  {/if}
</div>
