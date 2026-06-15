<script lang="ts">
  import { untrack } from 'svelte';
  import { Dialog as DialogPrimitive } from 'bits-ui';
  import { Loader2 } from '@lucide/svelte';
  import TrackPanel from '$lib/components/file-browser/TrackPanel.svelte';
  import { coverUrlForSong, coverThumbUrl } from '$lib/api-client';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { playerStore } from '$lib/stores/player.svelte';

  // The single global mount point for the song-detail panel — now an
  // Apple-Music-style full-screen overlay (portaled via bits-ui Dialog, so it
  // gets focus-trap, Escape-to-close and body scroll-lock for free, and it no
  // longer sits in the app-shell flex row, so opening it no longer shrinks the
  // page). One responsive layout serves desktop and mobile.
  const r = $derived(songDetail.resolved);

  // Ambient backdrop: a large, heavily-blurred fill of the track's cover behind
  // a theme-aware scrim. Own-cover URLs get a modest size (the blur hides
  // detail); external URLs pass through unchanged.
  const coverUrl = $derived(r ? (coverUrlForSong(r.song) ?? r.album.coverUrl ?? null) : null);
  const ambientUrl = $derived(coverThumbUrl(coverUrl, 600));

  // Keep the dataset live while the panel is open so a song opened off-Library
  // refreshes after enrichment. The initial fetch is owned by songDetail.open()
  // (via ensureLoaded) — deliberately NOT called here: reading songsStore's
  // isLoading inside this effect while loads write it creates a feedback cycle.
  $effect(() => {
    if (!songDetail.isOpen) return;
    songsStore.startLive();
    return () => songsStore.stopLive();
  });

  // Hide the global MiniPlayer only while the detail panel is actually open (the
  // overlay has its own transport), then restore it on close.
  $effect(() => {
    if (!songDetail.isOpen) return;
    return playerStore.registerPanel();
  });

  // Follow playback: when a new song starts while the panel is showing the song
  // that was playing, re-target the panel to it (manual play or queue
  // auto-advance). A panel opened for a different (browsed) song stays put.
  // Plain `let` — only a between-runs memo, must not be a dependency.
  let lastPlayingId: number | null = null;
  $effect(() => {
    const playingId = playerStore.currentSong?.id ?? null;
    if (playingId === null) return; // stop/clear: keep the last real song as the anchor
    const prev = lastPlayingId;
    lastPlayingId = playingId;
    if (playingId === prev) return;
    // untrack: open() writes songDetail.target; tracked reads of it here would
    // make this effect re-fire on its own write (effect_update_depth_exceeded).
    untrack(() => {
      if (songDetail.isOpen && songDetail.target?.songId === prev) {
        songDetail.open(playingId);
      }
    });
  });

  function onResetEnrichment() {
    void songsStore.loadSongs();
  }
</script>

<DialogPrimitive.Root open={songDetail.isOpen} onOpenChange={(open) => !open && songDetail.close()}>
  <DialogPrimitive.Portal>
    <DialogPrimitive.Overlay
      class="data-open:animate-in data-closed:animate-out data-closed:fade-out-0 data-open:fade-in-0 fixed inset-0 z-[60] bg-black/40 duration-150"
    />
    <DialogPrimitive.Content
      class="data-open:animate-in data-open:fade-in-0 data-closed:animate-out data-closed:fade-out-0 fixed inset-0 z-[60] flex flex-col overflow-hidden outline-none duration-150"
    >
      <!-- Ambient album-art backdrop + theme-aware scrim -->
      {#if ambientUrl}
        <img
          src={ambientUrl}
          alt=""
          aria-hidden="true"
          class="absolute inset-0 size-full scale-110 object-cover opacity-50 blur-3xl"
        />
      {/if}
      <div class="bg-background/80 absolute inset-0 backdrop-blur-2xl"></div>

      <DialogPrimitive.Title class="sr-only">Track details</DialogPrimitive.Title>
      <DialogPrimitive.Description class="sr-only">
        View track metadata, lyrics, fingerprint, and enrichment sources
      </DialogPrimitive.Description>

      <div class="relative z-10 flex h-full min-h-0 flex-col">
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
    </DialogPrimitive.Content>
  </DialogPrimitive.Portal>
</DialogPrimitive.Root>
