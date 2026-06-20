<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Badge } from '$lib/components/ui/badge';
  import { Heart, ListVideo, RefreshCw, Loader2, Music2 } from '@lucide/svelte';
  import {
    fetchExportedPlaylists,
    regenerateExportedPlaylists,
    type ExportedPlaylist
  } from '$lib/api-client';

  let playlists = $state<ExportedPlaylist[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let regenerating = $state(false);
  let banner = $state<{ type: 'success' | 'error'; message: string } | null>(null);

  async function load(quiet = false) {
    if (!quiet) loading = true;
    error = null;
    try {
      const result = await fetchExportedPlaylists();
      playlists = result.playlists;
    } catch (err) {
      if (!quiet) error = err instanceof Error ? err.message : 'Failed to load playlists';
    } finally {
      if (!quiet) loading = false;
    }
  }

  $effect(() => {
    void load();
  });

  async function onRegenerate() {
    regenerating = true;
    banner = null;
    try {
      await regenerateExportedPlaylists();
      banner = {
        type: 'success',
        message: 'Regenerating playlists in the background — this can take a minute. Refreshing…'
      };
      // The export runs off the request path; poll a couple of times so coverage refreshes once it lands.
      setTimeout(() => void load(true), 4000);
      setTimeout(() => void load(true), 12000);
    } catch (err) {
      banner = { type: 'error', message: err instanceof Error ? err.message : 'Failed to start regeneration' };
    } finally {
      regenerating = false;
    }
  }

  function coveragePct(p: ExportedPlaylist): number {
    if (p.spotifyTrackTotal <= 0) return 0;
    return Math.min(100, Math.round((p.matchedTrackCount / p.spotifyTrackTotal) * 100));
  }

  function relativeTime(iso: string | null | undefined): string {
    if (!iso) return 'never';
    const then = new Date(iso).getTime();
    const diffMs = Date.now() - then;
    const mins = Math.round(diffMs / 60000);
    if (mins < 1) return 'just now';
    if (mins < 60) return `${mins}m ago`;
    const hrs = Math.round(mins / 60);
    if (hrs < 24) return `${hrs}h ago`;
    return new Date(iso).toLocaleDateString();
  }

  const totalMatched = $derived(playlists.reduce((sum, p) => sum + p.matchedTrackCount, 0));
</script>

<div class="flex min-h-0 flex-1 flex-col overflow-hidden">
  <!-- Header -->
  <div class="border-border bg-card/30 border-b px-4 py-5 md:px-6">
    <div class="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
      <div>
        <div class="flex items-center gap-2">
          <ListVideo class="size-5" />
          <h1 class="text-2xl font-bold">Playlists</h1>
          <Badge variant="secondary">{playlists.length}</Badge>
        </div>
        <p class="text-muted-foreground mt-1 text-sm">
          Your Spotify Liked Songs and playlists, mirrored as <code>.m3u8</code> files in your library
          for Navidrome / Plex / Jellyfin. Each lists the matched local tracks in Spotify order. Connect
          Spotify on the <a href="/spotify" class="underline">Spotify</a> page.
        </p>
      </div>
      <div class="flex items-center gap-2">
        <Button onclick={onRegenerate} disabled={regenerating}>
          {#if regenerating}
            <Loader2 class="size-4 animate-spin" />
          {:else}
            <RefreshCw class="size-4" />
          {/if}
          Regenerate
        </Button>
      </div>
    </div>
  </div>

  {#if banner}
    <div
      class="mx-4 mt-3 rounded-md border px-3 py-2 text-sm md:mx-6 {banner.type === 'success'
        ? 'border-[#1DB954]/30 bg-[#1DB954]/10 text-[#1DB954]'
        : 'border-destructive/30 bg-destructive/10 text-destructive'}"
    >
      {banner.message}
    </div>
  {/if}

  <ScrollArea class="min-h-0 flex-1">
    <div class="px-4 py-4 md:px-6">
      {#if loading}
        <div class="text-muted-foreground flex items-center gap-2 py-12 text-sm">
          <Loader2 class="size-4 animate-spin" /> Loading playlists…
        </div>
      {:else if error}
        <div class="border-destructive/30 bg-destructive/10 text-destructive rounded-md border px-3 py-2 text-sm">
          {error}
        </div>
      {:else if playlists.length === 0}
        <div class="border-border bg-card text-muted-foreground rounded-lg border p-8 text-center text-sm">
          <ListVideo class="mx-auto mb-3 size-8 opacity-50" />
          <p class="font-medium">No playlists exported yet.</p>
          <p class="mt-1">
            Once Spotify is connected, your Liked Songs and playlists are exported automatically on a
            schedule. Hit <span class="font-medium">Regenerate</span> to run it now.
          </p>
        </div>
      {:else}
        <div class="text-muted-foreground mb-3 text-xs">
          {totalMatched.toLocaleString()} local tracks across {playlists.length} playlist{playlists.length === 1 ? '' : 's'}
        </div>
        <div class="flex flex-col gap-2">
          {#each playlists as p (p.id)}
            {@const pct = coveragePct(p)}
            <div class="border-border bg-card flex flex-col gap-2 rounded-lg border p-3">
              <div class="flex items-center gap-3">
                {#if p.kind === 'LikedSongs'}
                  <Heart class="size-4 shrink-0 text-[#1DB954]" />
                {:else}
                  <Music2 class="text-muted-foreground size-4 shrink-0" />
                {/if}
                <div class="min-w-0 flex-1">
                  <div class="truncate text-sm font-medium">{p.name}</div>
                  <div class="text-muted-foreground truncate font-mono text-xs" title={p.filePath}>
                    {p.filePath}
                  </div>
                </div>
                <div class="shrink-0 text-right">
                  <div class="text-sm font-medium tabular-nums">
                    {p.matchedTrackCount.toLocaleString()} / {p.spotifyTrackTotal.toLocaleString()}
                  </div>
                  <div class="text-muted-foreground text-xs">{relativeTime(p.lastGeneratedAtUtc)}</div>
                </div>
              </div>
              <div class="bg-muted h-1.5 overflow-hidden rounded-full">
                <div
                  class="h-full rounded-full bg-[#1DB954] transition-[width] duration-300"
                  style="width: {pct}%;"
                ></div>
              </div>
            </div>
          {/each}
        </div>
      {/if}
    </div>
  </ScrollArea>
</div>
