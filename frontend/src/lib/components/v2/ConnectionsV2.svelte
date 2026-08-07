<script lang="ts">
  import { Loader2, X } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { Badge } from '$lib/components/ui/badge';
  import { Button } from '$lib/components/ui/button';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import {
    fetchSpotifyStatus,
    navidrome,
    soulseek,
    sync,
    type NavidromeStatus,
    type SoulseekStatus,
    type SoulseekUpgrade,
    type SpotifyStatusResponse,
    type SyncStatus
  } from '$lib/api-client';

  // Every integration in one place. Until now each was reported somewhere different — Spotify on
  // its own page, Soulseek and library sync buried in Settings, and Navidrome nowhere at all
  // (it had no endpoint), so a user could not tell whether their likes were syncing.
  let spotifyStatus = $state<SpotifyStatusResponse | null>(null);
  let navidromeStatus = $state<NavidromeStatus | null>(null);
  let soulseekStatus = $state<SoulseekStatus | null>(null);
  let syncStatus = $state<SyncStatus | null>(null);
  let upgrades = $state<SoulseekUpgrade[]>([]);
  let loaded = $state(false);
  let cancelling = $state<number | null>(null);

  async function loadAll(): Promise<void> {
    const [sp, nd, slsk, sy, up] = await Promise.all([
      fetchSpotifyStatus().catch(() => null),
      navidrome.getStatus().catch(() => null),
      soulseek.getStatus().catch(() => null),
      sync.getStatus().catch(() => null),
      soulseek.listUpgrades(undefined, 25).catch(() => [] as SoulseekUpgrade[])
    ]);
    spotifyStatus = sp;
    navidromeStatus = nd;
    soulseekStatus = slsk;
    syncStatus = sy;
    upgrades = up;
    loaded = true;
  }

  $effect(() => {
    void loadAll();
  });

  async function cancelUpgrade(id: number): Promise<void> {
    cancelling = id;
    try {
      await soulseek.cancelUpgrade(id);
      toast.success('Upgrade cancelled.');
      upgrades = upgrades.filter((u) => u.id !== id);
    } catch (err) {
      toast.error(err instanceof Error ? err.message : 'Could not cancel the upgrade.');
    } finally {
      cancelling = null;
    }
  }

  /** Queued/searching/downloading requests are the only ones worth offering a cancel for. */
  const activeUpgrades = $derived(
    upgrades.filter((u) => ['Queued', 'Searching', 'Downloading', 'AwaitingIngest'].includes(u.status))
  );
</script>

<main class="flex min-h-0 flex-1 flex-col overflow-hidden">
  <div class="border-border border-b px-4 py-4 sm:px-7 sm:py-5">
    <h1 class="text-2xl font-semibold tracking-tight">Connections</h1>
    <p class="text-muted-foreground mt-1 text-xs">
      External services MusicHoarder talks to. All are configured through server environment
      variables — this page reports what they're doing, it doesn't change them.
    </p>
  </div>

  <ScrollArea class="min-h-0 flex-1">
    <div class="flex flex-col gap-5 px-4 py-5 sm:px-7">
      <section class="border-border bg-card rounded-lg border">
        <div class="divide-border divide-y">
          <!-- Spotify -->
          <div class="flex items-center gap-4 px-5 py-3.5">
            <div class="min-w-0 flex-1">
              <div class="text-[12.5px] font-medium">Spotify</div>
              <div class="text-muted-foreground text-[11.5px]">
                {#if !loaded}
                  Loading…
                {:else if !spotifyStatus}
                  Status unavailable.
                {:else if !spotifyStatus.hasCredentials}
                  No Spotify app credentials on the server — set the client id and secret first.
                {:else if spotifyStatus.connected && spotifyStatus.tokenExpired}
                  Connected, but the token has expired and needs reconnecting.
                {:else if spotifyStatus.connected}
                  Liked songs, playlists and release metadata.
                {:else}
                  Not connected. Sign in on the Spotify page to import liked songs and playlists.
                {/if}
              </div>
            </div>
            {#if loaded}
              {#if spotifyStatus?.connected && !spotifyStatus.tokenExpired}
                <Badge class="shrink-0">Connected</Badge>
              {:else if spotifyStatus?.connected}
                <Badge variant="secondary" class="shrink-0">Token expired</Badge>
              {:else}
                <Badge variant="outline" class="text-muted-foreground shrink-0">Not connected</Badge>
              {/if}
              <Button variant="outline" size="sm" href="/spotify" class="shrink-0">Open</Button>
            {/if}
          </div>

          <!-- Navidrome -->
          <div class="flex items-center gap-4 px-5 py-3.5">
            <div class="min-w-0 flex-1">
              <div class="text-[12.5px] font-medium">Navidrome</div>
              <div class="text-muted-foreground text-[11.5px]">
                {#if !loaded}
                  Loading…
                {:else if !navidromeStatus}
                  Status unavailable.
                {:else if !navidromeStatus.enabled}
                  Turned off. Set <code>Navidrome:Enabled</code> to re-enable like sync.
                {:else if !navidromeStatus.configured}
                  Enabled, but missing a server URL, username or password.
                {:else if navidromeStatus.connected}
                  Two-way like sync with {navidromeStatus.baseUrl}.
                {:else}
                  Configured for {navidromeStatus.baseUrl}, but the server didn't answer.
                {/if}
              </div>
            </div>
            {#if loaded && navidromeStatus}
              {#if navidromeStatus.connected}
                <Badge class="shrink-0">Connected</Badge>
              {:else if navidromeStatus.configured}
                <Badge variant="secondary" class="shrink-0">Unreachable</Badge>
              {:else if navidromeStatus.enabled}
                <Badge variant="outline" class="text-muted-foreground shrink-0">Incomplete</Badge>
              {:else}
                <Badge variant="outline" class="text-muted-foreground shrink-0">Off</Badge>
              {/if}
            {:else if loaded}
              <Badge variant="outline" class="text-muted-foreground shrink-0">Unavailable</Badge>
            {/if}
          </div>

          <!-- Soulseek -->
          <div class="flex items-center gap-4 px-5 py-3.5">
            <div class="min-w-0 flex-1">
              <div class="text-[12.5px] font-medium">Soulseek (slskd)</div>
              <div class="text-muted-foreground text-[11.5px]">
                {#if !loaded}
                  Loading…
                {:else if !soulseekStatus}
                  Status unavailable.
                {:else if soulseekStatus.configured}
                  {soulseekStatus.connected ? 'Connected' : 'Not connected'}{soulseekStatus.version
                    ? ` · slskd ${soulseekStatus.version}`
                    : ''}
                {:else}
                  Searches the Soulseek network for better-quality copies of your tracks.
                {/if}
              </div>
            </div>
            {#if loaded && soulseekStatus}
              {#if soulseekStatus.configured && soulseekStatus.connected}
                <Badge class="shrink-0">Connected</Badge>
              {:else if soulseekStatus.configured}
                <Badge variant="secondary" class="shrink-0">Disconnected</Badge>
              {:else}
                <Badge variant="outline" class="text-muted-foreground shrink-0">Not configured</Badge>
              {/if}
            {:else if loaded}
              <Badge variant="outline" class="text-muted-foreground shrink-0">Unavailable</Badge>
            {/if}
          </div>

          <!-- Library sync (MusicHoarder to MusicHoarder) -->
          <div class="flex flex-col gap-2 px-5 py-3.5">
            <div class="flex items-center gap-4">
              <div class="min-w-0 flex-1">
                <div class="text-[12.5px] font-medium">Library sync</div>
                <div class="text-muted-foreground text-[11.5px]">
                  {#if !loaded}
                    Loading…
                  {:else if !syncStatus}
                    Status unavailable.
                  {:else if syncStatus.mode === 'Push'}
                    Pushing built tracks to the receiving deployment.
                  {:else if syncStatus.mode === 'Receive'}
                    Receiving tracks pushed from another deployment.
                  {:else}
                    Not configured — this deployment neither pushes nor receives.
                  {/if}
                </div>
              </div>
              {#if loaded && syncStatus}
                {#if syncStatus.mode === 'Off'}
                  <Badge variant="outline" class="text-muted-foreground shrink-0">Off</Badge>
                {:else}
                  <Badge variant="secondary" class="shrink-0">{syncStatus.mode}</Badge>
                {/if}
              {:else if loaded}
                <Badge variant="outline" class="text-muted-foreground shrink-0">Unavailable</Badge>
              {/if}
            </div>
            {#if loaded && syncStatus?.mode === 'Push'}
              <div class="grid grid-cols-2 gap-2 sm:grid-cols-4">
                <div class="border-border bg-secondary/30 rounded-md border px-3 py-2">
                  <div class="font-mono text-sm font-semibold tabular-nums">
                    {syncStatus.outbox.synced}
                  </div>
                  <div class="text-muted-foreground text-[10.5px]">Synced</div>
                </div>
                <div class="border-border bg-secondary/30 rounded-md border px-3 py-2">
                  <div class="font-mono text-sm font-semibold tabular-nums">
                    {syncStatus.outbox.pending + syncStatus.outbox.uploading}
                  </div>
                  <div class="text-muted-foreground text-[10.5px]">Pending</div>
                </div>
                <div class="border-border bg-secondary/30 rounded-md border px-3 py-2">
                  <div class="font-mono text-sm font-semibold tabular-nums">
                    {syncStatus.outbox.failed}
                  </div>
                  <div class="text-muted-foreground text-[10.5px]">Failed</div>
                </div>
                <div class="border-border bg-secondary/30 rounded-md border px-3 py-2">
                  <div class="font-mono text-sm font-semibold tabular-nums">
                    {syncStatus.outbox.skippedRemoteBetter}
                  </div>
                  <div class="text-muted-foreground text-[10.5px]">Remote better</div>
                </div>
              </div>
            {/if}
          </div>
        </div>
      </section>

      <!-- Upgrade queue. Requesting an upgrade was already possible from a track or album, but
           the queue itself had no surface, so a request could not be seen or called off. -->
      {#if loaded && activeUpgrades.length > 0}
        <section class="border-border bg-card rounded-lg border">
          <header class="border-border border-b px-5 py-3.5">
            <h2 class="text-[13px] font-semibold">Upgrade queue</h2>
            <p class="text-muted-foreground text-[11.5px]">
              Tracks Soulseek is looking for a better copy of.
            </p>
          </header>
          <div class="divide-border divide-y">
            {#each activeUpgrades as u (u.id)}
              <div class="flex items-center gap-4 px-5 py-3">
                <div class="min-w-0 flex-1">
                  <div class="truncate text-[12.5px] font-medium">
                    {u.songTitle ?? `Song #${u.songId}`}
                  </div>
                  <div class="text-muted-foreground truncate text-[11.5px]">
                    {u.songArtist ?? 'Unknown artist'} · {u.status}
                    {#if u.songExtension}· current {u.songExtension.replace('.', '')}{/if}
                  </div>
                </div>
                <Button
                  variant="ghost"
                  size="sm"
                  class="text-muted-foreground hover:text-destructive shrink-0 gap-1.5"
                  disabled={cancelling !== null}
                  onclick={() => cancelUpgrade(u.id)}
                >
                  {#if cancelling === u.id}
                    <Loader2 class="size-3.5 animate-spin" />
                  {:else}
                    <X class="size-3.5" />
                  {/if}
                  Cancel
                </Button>
              </div>
            {/each}
          </div>
        </section>
      {/if}
    </div>
  </ScrollArea>
</main>
