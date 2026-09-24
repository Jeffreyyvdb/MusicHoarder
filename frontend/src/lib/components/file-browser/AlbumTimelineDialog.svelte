<script lang="ts">
  import { AlertTriangle, Loader2 } from '@lucide/svelte';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import TimelineList from '$lib/components/v2/TimelineList.svelte';
  import {
    fetchAlbumTimeline,
    type AlbumTimelineApiEvent,
    type AlbumTimelineResponse
  } from '$lib/api-client';
  import {
    providerColor,
    providerLabel,
    type TimelineEvent,
    type TimelineTint
  } from '$lib/review-helpers';

  type Props = {
    open?: boolean;
    artist: string;
    album: string;
  };
  let { open = $bindable(false), artist, album }: Props = $props();

  let timeline = $state<AlbumTimelineResponse | null>(null);
  let loading = $state(false);
  let loadError = $state<string | null>(null);

  // Fetch lazily on first open; refetch when the dialog targets a different album.
  $effect(() => {
    const a = artist;
    const t = album;
    timeline = null;
    loadError = null;
    if (!open || !a || !t) return;
    let cancelled = false;
    loading = true;
    void fetchAlbumTimeline(a, t)
      .then((result) => {
        if (cancelled) return;
        timeline = result;
      })
      .catch(() => {
        if (cancelled) return;
        loadError = 'Could not load the album timeline.';
      })
      .finally(() => {
        if (!cancelled) loading = false;
      });
    return () => {
      cancelled = true;
    };
  });

  const TINTS: TimelineTint[] = ['ok', 'warn', 'err', 'info', 'neutral'];

  function toTimelineEvent(e: AlbumTimelineApiEvent): TimelineEvent {
    return {
      key: e.key,
      time: e.timeUtc,
      stage: e.stage,
      tint: TINTS.includes(e.tint as TimelineTint) ? (e.tint as TimelineTint) : 'neutral',
      provider: e.provider
        ? { label: providerLabel(e.provider), color: providerColor(e.provider), pct: e.pct ?? null }
        : null,
      description: e.description,
      deltaMs: null
    };
  }

  const events = $derived<TimelineEvent[]>((timeline?.events ?? []).map(toTimelineEvent));
</script>

<!-- A sheet on a phone (content height, up to nearly full), a dialog on desktop. The sheet body
     scrolls, so the timeline needs no scroller of its own. -->
<BottomSheet.Root
  bind:open
  title="Album timeline"
  description="Where “{album}” by {artist} got its data — discovery, providers and library writes."
  bodyClass="px-4 md:px-6"
  class="md:max-w-2xl"
>
  {#snippet trailing()}
    <BottomSheet.Action prominent onclick={() => (open = false)}>Done</BottomSheet.Action>
  {/snippet}

  {#if loading}
    <div
      class="text-muted-foreground text-subheadline flex items-center justify-center gap-2 py-10 md:text-sm"
    >
      <Loader2 class="size-4 animate-spin" />
      Loading timeline…
    </div>
  {:else if loadError}
    <div
      class="bg-card text-muted-foreground text-subheadline flex items-center justify-center gap-2 rounded-xl px-4 py-8 md:text-sm"
    >
      <AlertTriangle class="text-warning-text size-4" aria-hidden="true" />
      {loadError}
    </div>
  {:else if events.length === 0}
    <div
      class="bg-card text-muted-foreground text-subheadline rounded-xl px-4 py-8 text-center md:text-sm"
    >
      No events recorded for this album yet.
    </div>
  {:else}
    <TimelineList {events} showDate />
    <p class="text-footnote text-muted-foreground mt-2 px-1">
      Per-track enrichment is rolled up per provider — open a track’s own timeline for the full
      detail.
    </p>
  {/if}
</BottomSheet.Root>
