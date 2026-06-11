<script lang="ts">
  import { AlertTriangle, Loader2 } from '@lucide/svelte';
  import * as Dialog from '$lib/components/ui/dialog';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import TimelineList from '$lib/components/v2/TimelineList.svelte';
  import {
    fetchAlbumTimeline,
    type AlbumTimelineApiEvent,
    type AlbumTimelineResponse
  } from '$lib/api-client';
  import { providerColor, providerLabel, type TimelineEvent, type TimelineTint } from '$lib/review-helpers';

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

<Dialog.Root bind:open>
  <Dialog.Content class="sm:max-w-2xl">
    <Dialog.Header>
      <Dialog.Title>Album timeline</Dialog.Title>
      <Dialog.Description>
        Where “{album}” by {artist} got its data — discovery, providers, and library writes.
      </Dialog.Description>
    </Dialog.Header>

    <div class="flex max-h-[70vh] min-h-0 flex-col">
      {#if loading}
        <div class="text-muted-foreground flex items-center justify-center gap-2 py-10 text-[12.5px]">
          <Loader2 class="size-4 animate-spin" />
          Loading timeline…
        </div>
      {:else if loadError}
        <div
          class="border-border bg-card text-muted-foreground flex items-center justify-center gap-2 rounded-lg border border-dashed px-3.5 py-8 text-[12.5px]"
        >
          <AlertTriangle class="size-4 text-amber-500" />
          {loadError}
        </div>
      {:else if events.length === 0}
        <div
          class="border-border bg-card text-muted-foreground rounded-lg border border-dashed px-3.5 py-8 text-center text-[12px]"
        >
          No events recorded for this album yet.
        </div>
      {:else}
        <ScrollArea class="min-h-0 flex-1">
          <TimelineList {events} showDate />
        </ScrollArea>
        <p class="text-muted-foreground/70 mt-2 shrink-0 text-[11px]">
          Per-track enrichment is rolled up per provider — open a track’s own timeline for the full detail.
        </p>
      {/if}
    </div>
  </Dialog.Content>
</Dialog.Root>
