<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Badge } from '$lib/components/ui/badge';
  import { Heart, ListVideo, RefreshCw, Loader2, Music2, Plus, X } from '@lucide/svelte';
  import {
    fetchPlaylistCollections,
    subscribePlaylist,
    unsubscribePlaylist,
    regenerateExportedPlaylists,
    type PlaylistCollection
  } from '$lib/api-client';

  let collections = $state<PlaylistCollection[]>([]);
  let spotifyConnected = $state(true);
  let spotifyError = $state<string | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let regenerating = $state(false);
  let banner = $state<{ type: 'success' | 'error'; message: string } | null>(null);
  let busyKeys = $state(new Set<string>());

  function keyOf(c: PlaylistCollection): string {
    return `${c.kind}\u0000${c.spotifyPlaylistId ?? ''}`;
  }

  function setBusy(key: string, on: boolean) {
    const next = new Set(busyKeys);
    if (on) next.add(key);
    else next.delete(key);
    busyKeys = next;
  }

  async function load(quiet = false) {
    if (!quiet) loading = true;
    error = null;
    try {
      const result = await fetchPlaylistCollections();
      // Dedupe by key as a safety net — the keyed {#each} below throws on a duplicate key, so never
      // let two collections with the same (kind, playlistId) reach the render.
      const seen = new Set<string>();
      collections = result.collections.filter((c) => {
        const k = keyOf(c);
        if (seen.has(k)) return false;
        seen.add(k);
        return true;
      });
      spotifyConnected = result.spotifyConnected;
      spotifyError = result.spotifyError ?? null;
    } catch (err) {
      if (!quiet) error = err instanceof Error ? err.message : 'Failed to load playlists';
    } finally {
      if (!quiet) loading = false;
    }
  }

  $effect(() => {
    void load();
  });

  async function onSubscribe(c: PlaylistCollection) {
    const key = keyOf(c);
    setBusy(key, true);
    banner = null;
    try {
      const res = await subscribePlaylist({ kind: c.kind, spotifyPlaylistId: c.spotifyPlaylistId, name: c.name });
      // Optimistic: flip to subscribed (with the new row id so Remove works at once) and show
      // "generating" until the background export lands.
      c.subscribed = true;
      c.id = res.id;
      c.lastGeneratedAtUtc = null;
      banner = {
        type: 'success',
        message: `Syncing “${c.name}” — generating its .m3u8 in the background. Refreshing…`
      };
      setTimeout(() => void load(true), 4000);
      setTimeout(() => void load(true), 12000);
    } catch (err) {
      banner = { type: 'error', message: err instanceof Error ? err.message : 'Failed to add playlist' };
    } finally {
      setBusy(key, false);
    }
  }

  async function onUnsubscribe(c: PlaylistCollection) {
    if (c.id == null) return;
    const key = keyOf(c);
    setBusy(key, true);
    banner = null;
    try {
      await unsubscribePlaylist(c.id);
      c.subscribed = false;
      c.id = null;
      c.matchedTrackCount = 0;
      c.filePath = null;
      c.lastGeneratedAtUtc = null;
      // When Spotify is disconnected the list only contains subscribed rows, so drop it from view.
      if (!spotifyConnected) {
        collections = collections.filter((x) => keyOf(x) !== key);
      }
    } catch (err) {
      banner = { type: 'error', message: err instanceof Error ? err.message : 'Failed to remove playlist' };
    } finally {
      setBusy(key, false);
    }
  }

  async function onRegenerate() {
    regenerating = true;
    banner = null;
    try {
      await regenerateExportedPlaylists();
      banner = {
        type: 'success',
        message: 'Regenerating synced playlists in the background — this can take a minute. Refreshing…'
      };
      setTimeout(() => void load(true), 4000);
      setTimeout(() => void load(true), 12000);
    } catch (err) {
      banner = { type: 'error', message: err instanceof Error ? err.message : 'Failed to start regeneration' };
    } finally {
      regenerating = false;
    }
  }

  function coveragePct(c: PlaylistCollection): number {
    if (c.spotifyTrackTotal <= 0) return 0;
    return Math.min(100, Math.round((c.matchedTrackCount / c.spotifyTrackTotal) * 100));
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

  const subscribedCount = $derived(collections.filter((c) => c.subscribed).length);
</script>

<div class="flex min-h-0 flex-1 flex-col overflow-hidden">
  <!-- Header -->
  <div class="border-border bg-card/30 border-b px-4 py-5 md:px-6">
    <div class="flex flex-col gap-4 sm:flex-row sm:items-center sm:justify-between">
      <div>
        <div class="flex items-center gap-2">
          <ListVideo class="size-5" />
          <h1 class="text-2xl font-semibold tracking-tight">Playlists</h1>
          <Badge variant="secondary">{subscribedCount} synced</Badge>
        </div>
        <p class="text-muted-foreground mt-1 text-sm">
          Pick which Spotify collections to mirror as <code>.m3u8</code> files in your library for
          Navidrome / Plex / Jellyfin. Nothing is synced until you add it — each synced file lists the
          matched local tracks in Spotify order. Connect Spotify on the
          <a href="/spotify" class="underline">Spotify</a> page.
        </p>
      </div>
      <div class="flex items-center gap-2">
        <Button onclick={onRegenerate} disabled={regenerating || subscribedCount === 0}>
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
        ? 'border-primary/30 bg-primary/10 text-primary'
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
      {:else}
        {#if !spotifyConnected}
          <div class="mb-3 py-6 text-sm">
            <p class="font-medium">Spotify isn’t connected.</p>
            <p class="text-muted-foreground mt-1">
              Connect it on the <a href="/spotify" class="underline">Spotify</a> page to browse your Liked
              Songs and playlists and choose which to sync.
            </p>
          </div>
        {:else if spotifyError}
          <div class="border-border bg-muted/40 text-muted-foreground mb-3 rounded-md border px-3 py-2 text-xs">
            {spotifyError}
          </div>
        {/if}

        {#if collections.length === 0}
          <div class="text-muted-foreground py-16 text-center text-sm">
            <ListVideo class="mx-auto mb-3 size-8 opacity-40" />
            <p class="font-medium text-foreground">Nothing to show yet.</p>
            <p class="mt-1">
              {#if spotifyConnected}
                No Liked Songs or playlists were found on your Spotify account.
              {:else}
                Connect Spotify to pick playlists to sync.
              {/if}
            </p>
          </div>
        {:else}
          <div class="text-muted-foreground mb-2 text-xs">
            {subscribedCount} synced of {collections.length} collection{collections.length === 1 ? '' : 's'}
          </div>
          <div class="divide-border divide-y">
            {#each collections as c (keyOf(c))}
              {@const pct = coveragePct(c)}
              {@const busy = busyKeys.has(keyOf(c))}
              <div class="hover:bg-secondary/40 flex flex-col gap-2 px-2 py-3 transition-colors">
                <div class="flex items-center gap-3">
                  {#if c.imageUrl}
                    <img src={c.imageUrl} alt="" class="size-10 shrink-0 rounded object-cover" />
                  {:else}
                    <div
                      class="bg-muted flex size-10 shrink-0 items-center justify-center rounded"
                    >
                      {#if c.kind === 'LikedSongs'}
                        <Heart class="size-5 text-primary" />
                      {:else}
                        <Music2 class="text-muted-foreground size-5" />
                      {/if}
                    </div>
                  {/if}
                  <div class="min-w-0 flex-1">
                    <div class="flex items-center gap-2">
                      <span class="truncate text-sm font-medium">{c.name}</span>
                      {#if c.subscribed}
                        <Badge variant="outline" class="border-primary/40 text-primary">Synced</Badge>
                      {/if}
                    </div>
                    <div class="text-muted-foreground truncate text-xs">
                      {#if c.subscribed}
                        {#if c.lastGeneratedAtUtc}
                          {c.matchedTrackCount.toLocaleString()} / {c.spotifyTrackTotal.toLocaleString()} matched
                          · {relativeTime(c.lastGeneratedAtUtc)}
                        {:else}
                          Generating…
                        {/if}
                      {:else}
                        {c.spotifyTrackTotal.toLocaleString()} track{c.spotifyTrackTotal === 1 ? '' : 's'}{c.ownerName
                          ? ` · ${c.ownerName}`
                          : ''}
                      {/if}
                    </div>
                    {#if c.subscribed && c.filePath}
                      <div class="text-muted-foreground/70 truncate font-mono text-[11px]" title={c.filePath}>
                        {c.filePath}
                      </div>
                    {/if}
                  </div>
                  <div class="shrink-0">
                    {#if c.subscribed}
                      <Button variant="outline" size="sm" disabled={busy} onclick={() => onUnsubscribe(c)}>
                        {#if busy}
                          <Loader2 class="size-4 animate-spin" />
                        {:else}
                          <X class="size-4" />
                        {/if}
                        Remove
                      </Button>
                    {:else}
                      <Button size="sm" disabled={busy} onclick={() => onSubscribe(c)}>
                        {#if busy}
                          <Loader2 class="size-4 animate-spin" />
                        {:else}
                          <Plus class="size-4" />
                        {/if}
                        Sync
                      </Button>
                    {/if}
                  </div>
                </div>
                {#if c.subscribed && c.lastGeneratedAtUtc}
                  <div class="bg-muted h-1.5 overflow-hidden rounded-full">
                    <div
                      class="h-full rounded-full bg-primary transition-[width] duration-300 motion-reduce:transition-none"
                      style="width: {pct}%;"
                    ></div>
                  </div>
                {/if}
              </div>
            {/each}
          </div>
        {/if}
      {/if}
    </div>
  </ScrollArea>
</div>
