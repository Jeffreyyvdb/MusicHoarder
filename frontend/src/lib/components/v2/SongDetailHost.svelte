<script lang="ts">
  import { Loader2 } from '@lucide/svelte';
  import * as Sheet from '$lib/components/ui/sheet';
  import TrackPanel from '$lib/components/file-browser/TrackPanel.svelte';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';

  // The single global mount point for the song-detail panel. On desktop it's a
  // floating right-docked pane that pushes page content (a flex sibling of the
  // Sidebar.Inset); on mobile it's a bottom Sheet. Mirrors the navigation
  // sidebar's floating look (bg-sidebar, rounded, bordered, shadowed, inset).
  const isMobile = new IsMobile();
  const r = $derived(songDetail.resolved);

  // Keep the dataset live while the panel is open so a song opened off-Library
  // refreshes after enrichment. The initial fetch is owned by songDetail.open()
  // (via ensureLoaded) — deliberately NOT called here: reading songsStore's
  // isLoading inside this effect while loads write it creates a feedback cycle.
  $effect(() => {
    if (!songDetail.isOpen) return;
    songsStore.startLive();
    return () => songsStore.stopLive();
  });

  // Hide the global MiniPlayer only while the detail panel is actually open (it
  // has its own waveform player), then restore it on close.
  $effect(() => {
    if (!songDetail.isOpen) return;
    return playerStore.registerPanel();
  });

  function onResetEnrichment() {
    void songsStore.loadSongs();
  }
</script>

{#if isMobile.current}
  <Sheet.Root open={songDetail.isOpen} onOpenChange={(open) => !open && songDetail.close()}>
    <Sheet.Content side="bottom" class="data-[side=bottom]:h-[88dvh] gap-0 p-0 [&>button]:hidden">
      <Sheet.Title class="sr-only">Track details</Sheet.Title>
      <Sheet.Description class="sr-only">
        View track metadata, lyrics, fingerprint, and enrichment sources
      </Sheet.Description>
      {#if r}
        <TrackPanel
          album={r.album}
          song={r.song}
          trackIndex={r.index}
          onClose={() => songDetail.close()}
          {onResetEnrichment}
          timelineHref={`/track/${r.song.id}`}
        />
      {:else}
        <div class="text-muted-foreground flex h-full items-center justify-center gap-2 text-sm">
          <Loader2 class="size-4 animate-spin" /> Loading track…
        </div>
      {/if}
    </Sheet.Content>
  </Sheet.Root>
{:else if songDetail.isOpen}
  <aside class="hidden h-svh min-h-0 w-[calc(400px+1.5rem)] shrink-0 flex-col p-3 md:flex">
    <div
      class="border-sidebar-border bg-sidebar flex h-full min-h-0 flex-1 flex-col overflow-hidden rounded-2xl border shadow-[0_4px_24px_oklch(0%_0_0/0.08)] dark:shadow-[0_4px_20px_rgba(0,0,0,0.35)]"
    >
      {#if r}
        <TrackPanel
          album={r.album}
          song={r.song}
          trackIndex={r.index}
          onClose={() => songDetail.close()}
          {onResetEnrichment}
          timelineHref={`/track/${r.song.id}`}
        />
      {:else}
        <div class="text-muted-foreground flex h-full items-center justify-center gap-2 text-sm">
          <Loader2 class="size-4 animate-spin" /> Loading track…
        </div>
      {/if}
    </div>
  </aside>
{/if}
