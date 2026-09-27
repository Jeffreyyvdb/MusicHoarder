<script lang="ts">
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { Button } from '$lib/components/ui/button';
  import { Switch } from '$lib/components/ui/switch';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import {
    Heart,
    ListVideo,
    FileAudio,
    Loader2,
    Music2,
    AlertCircle,
    CircleCheck,
    X
  } from '@lucide/svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import {
    fetchPlaylistCollections,
    subscribePlaylist,
    unsubscribePlaylist,
    regenerateExportedPlaylists,
    type PlaylistCollection
  } from '$lib/api-client';
  import { toast } from 'svelte-sonner';

  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);

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
      const res = await subscribePlaylist({
        kind: c.kind,
        spotifyPlaylistId: c.spotifyPlaylistId,
        name: c.name
      });
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
      banner = {
        type: 'error',
        message: err instanceof Error ? err.message : 'Failed to add playlist'
      };
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
      // Turning sync off deletes the playlist file at once. Subscribing again writes it back, so
      // this is an Undo rather than a confirmation in front of every switch — held for ten
      // seconds, not sonner's four, so there is time to read it and reach Undo.
      toast(`Stopped syncing “${c.name}”`, {
        description: 'Its .m3u8 file was removed from your library.',
        duration: 10_000,
        action: { label: 'Undo', onClick: () => void onSubscribe(c) }
      });
    } catch (err) {
      banner = {
        type: 'error',
        message: err instanceof Error ? err.message : 'Failed to remove playlist'
      };
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
        message:
          'Regenerating synced playlists in the background — this can take a minute. Refreshing…'
      };
      setTimeout(() => void load(true), 4000);
      setTimeout(() => void load(true), 12000);
    } catch (err) {
      banner = {
        type: 'error',
        message: err instanceof Error ? err.message : 'Failed to start regeneration'
      };
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

  // Sync is a list-row switch now (it was a Sync / Remove button pair): on subscribes, off removes.
  // The switch is bound through a getter/setter: it shows the request's intent while it runs and
  // the collection's real state after, so a failed request puts it back. (A one-way `checked`
  // leaves the thumb flipped over data that did not change.)
  let pendingOn = $state<Record<string, boolean>>({});
  function syncShown(c: PlaylistCollection): boolean {
    const key = keyOf(c);
    return key in pendingOn ? pendingOn[key] : c.subscribed;
  }
  async function onToggle(c: PlaylistCollection, on: boolean) {
    const key = keyOf(c);
    pendingOn = { ...pendingOn, [key]: on };
    try {
      if (on) await onSubscribe(c);
      else await onUnsubscribe(c);
    } finally {
      const rest = { ...pendingOn };
      delete rest[key];
      pendingOn = rest;
    }
  }

  function subtitleOf(c: PlaylistCollection): string {
    if (!c.subscribed)
      return `${c.spotifyTrackTotal.toLocaleString()} track${c.spotifyTrackTotal === 1 ? '' : 's'}${
        c.ownerName ? ` · ${c.ownerName}` : ''
      }`;
    if (!c.lastGeneratedAtUtc) return 'Generating…';
    return `${c.matchedTrackCount.toLocaleString()} of ${c.spotifyTrackTotal.toLocaleString()} matched · ${relativeTime(c.lastGeneratedAtUtc)}`;
  }
</script>

{#snippet regenerateAction()}
  <Button
    variant="outline"
    size="sm"
    class="h-8 gap-1.5 px-2.5"
    onclick={onRegenerate}
    disabled={regenerating || subscribedCount === 0}
  >
    {#if regenerating}<Loader2 class="size-4 animate-spin" />{:else}<FileAudio
        class="size-4"
      />{/if}
    <span class="text-nav-sm">Regenerate</span>
  </Button>
{/snippet}

{#snippet regenerateItem()}
  <DropdownMenu.Item disabled={regenerating || subscribedCount === 0} onSelect={onRegenerate}>
    <FileAudio /> Regenerate .m3u8 files
  </DropdownMenu.Item>
{/snippet}

<div class="flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1" viewportClass="overscroll-contain">
    <!-- The four-line blurb was the longest in the app; the page's own empty and
         not-connected states say the same thing where it's actually needed. -->
    <PageToolbarV2
      title="Playlist sync"
      meta="{subscribedCount.toLocaleString()} of {collections.length.toLocaleString()} mirrored as .m3u8"
      actions={compact ? undefined : regenerateAction}
      more={compact ? regenerateItem : undefined}
    />

    {#if banner}
      <div class="px-4 pt-2 md:px-7">
        <div
          role={banner.type === 'error' ? 'alert' : 'status'}
          class="text-subheadline flex items-start gap-2 rounded-xl py-1 pr-1 pl-4 md:text-sm {banner.type ===
          'success'
            ? 'bg-primary/10 text-foreground'
            : 'bg-destructive/10 text-destructive-text'}"
        >
          {#if banner.type === 'success'}
            <CircleCheck class="text-primary mt-2.5 size-4 shrink-0" aria-hidden="true" />
          {:else}
            <AlertCircle class="mt-2.5 size-4 shrink-0" aria-hidden="true" />
          {/if}
          <p class="flex-1 py-2">{banner.message}</p>
          <Button
            variant="ghost"
            size="icon"
            class="size-11 shrink-0 rounded-full md:size-8"
            aria-label="Dismiss"
            onclick={() => (banner = null)}
          >
            <X />
          </Button>
        </div>
      </div>
    {/if}

    {#if loading}
      <div
        role="status"
        class="text-body text-muted-foreground flex items-center justify-center gap-2 py-12 md:text-sm"
      >
        <Loader2 class="size-4 animate-spin" aria-hidden="true" /> Loading playlists…
      </div>
    {:else if error}
      <div class="px-4 pt-2 md:px-7">
        <div
          role="alert"
          class="bg-destructive/10 text-destructive-text text-subheadline rounded-xl px-4 py-3 md:text-sm"
        >
          {error}
        </div>
      </div>
    {:else}
      {#if !spotifyConnected}
        <div class="text-body px-4 py-6 md:px-7 md:text-sm">
          <p class="font-medium">Spotify isn’t connected.</p>
          <p class="text-muted-foreground mt-1">
            Connect it on the <a href="/spotify" class="text-primary hover:underline">Spotify</a> page
            to browse your Liked Songs and playlists and choose which to sync.
          </p>
        </div>
      {:else if spotifyError}
        <div class="px-4 pt-2 md:px-7">
          <div class="bg-muted text-muted-foreground text-footnote rounded-xl px-4 py-3 md:text-xs">
            {spotifyError}
          </div>
        </div>
      {/if}

      {#if collections.length === 0}
        <div class="text-muted-foreground px-6 py-16 text-center">
          <ListVideo class="text-muted-foreground mx-auto mb-3 size-8" aria-hidden="true" />
          <p class="text-body text-foreground font-medium md:text-sm">Nothing to show yet.</p>
          <p class="text-subheadline mt-1 md:text-sm">
            {#if spotifyConnected}
              No Liked Songs or playlists were found on your Spotify account.
            {:else}
              Connect Spotify to pick playlists to sync.
            {/if}
          </p>
        </div>
      {:else}
        <!-- One row per collection with a Sync switch; a synced row shows its coverage as a thin bar
             and the file it writes. -->
        <ul class="pt-2 md:max-w-3xl" aria-label="Collections">
          {#each collections as c (keyOf(c))}
            {@const pct = coveragePct(c)}
            {@const busy = busyKeys.has(keyOf(c))}
            <li class="group/row md:hover:bg-accent flex items-center gap-3 pl-4 md:px-7">
              {#if c.imageUrl}
                <img
                  src={c.imageUrl}
                  alt=""
                  class="mt-2.5 size-11 shrink-0 self-start rounded-sm object-cover md:mt-0 md:size-10 md:self-center"
                />
              {:else}
                <div
                  class="bg-muted mt-2.5 flex size-11 shrink-0 items-center justify-center self-start rounded-sm md:mt-0 md:size-10 md:self-center"
                  aria-hidden="true"
                >
                  {#if c.kind === 'LikedSongs'}
                    <Heart class="text-primary size-5" />
                  {:else}
                    <Music2 class="text-muted-foreground size-5" />
                  {/if}
                </div>
              {/if}
              <div
                class="after:bg-separator relative flex min-h-16 min-w-0 flex-1 items-center gap-3 self-stretch py-2.5 pr-4 after:absolute after:inset-x-0 after:bottom-0 after:h-(--hairline) group-last/row:after:hidden md:min-h-14 md:pr-0"
              >
                <div class="min-w-0 flex-1">
                  <div class="text-body truncate md:text-sm md:font-medium">{c.name}</div>
                  <div
                    class="text-subheadline text-muted-foreground truncate tabular-nums md:text-xs"
                  >
                    {subtitleOf(c)}
                  </div>
                  {#if c.subscribed && c.lastGeneratedAtUtc}
                    <div
                      role="progressbar"
                      aria-label="{c.name} coverage"
                      aria-valuemin={0}
                      aria-valuemax={100}
                      aria-valuenow={pct}
                      class="bg-muted mt-1.5 h-1 max-w-80 overflow-hidden rounded-full"
                    >
                      <div
                        class="bg-primary h-full rounded-full transition-[width] duration-300 motion-reduce:transition-none"
                        style="width: {pct}%;"
                      ></div>
                    </div>
                  {/if}
                  {#if c.subscribed && c.filePath}
                    <div
                      class="text-footnote text-muted-foreground mt-1 truncate text-left font-mono [direction:rtl] md:text-[11px]"
                      title={c.filePath}
                    >
                      <bdi>{c.filePath}</bdi>
                    </div>
                  {/if}
                </div>
                {#if busy}
                  <Loader2
                    class="text-muted-foreground size-4 shrink-0 animate-spin"
                    aria-hidden="true"
                  />
                {/if}
                <Switch
                  bind:checked={() => syncShown(c), (on) => void onToggle(c, on)}
                  disabled={busy || (c.subscribed && c.id == null)}
                  aria-label="Sync {c.name}"
                />
              </div>
            </li>
          {/each}
        </ul>
        <p class="text-footnote text-muted-foreground px-4 pt-2 md:px-7 md:text-xs">
          Synced collections are written as .m3u8 playlists in your library, for Navidrome, Plex and
          Jellyfin. Turning one off removes its file; turning it back on writes it again.
        </p>
      {/if}
    {/if}
  </ScrollArea>
</div>
