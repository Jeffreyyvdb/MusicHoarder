<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Badge } from '$lib/components/ui/badge';
  import FilterChip from '$lib/components/v2/FilterChip.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { Switch } from '$lib/components/ui/switch';
  import {
    Gift,
    Heart,
    Download,
    RefreshCw,
    Trash2,
    Loader2,
    AlertCircle,
    CheckCircle2,
    ChevronRight,
    Music,
    Info
  } from '@lucide/svelte';
  import {
    albumCompletionSummary,
    fetchWishlist,
    fetchWishlistSources,
    runAlbumCompletion,
    setWishlistSourceAutoSync,
    removeWishlistSource,
    retryWishlistItem,
    retryFailedWishlistItems,
    removeWishlistItem,
    triggerWishlistDownload,
    fetchSettings,
    updateSettings,
    openProgressStream,
    type WishlistItem,
    type WishlistSource,
    type WishlistItemStatus,
    type ProgressSnapshot
  } from '$lib/api-client';
  import { songDetail } from '$lib/stores/song-detail.svelte';

  type Filter = WishlistItemStatus | 'All';

  const FILTERS: Filter[] = [
    'All',
    'Pending',
    'Downloading',
    'Downloaded',
    'SkippedOwned',
    'Failed',
    'NotFound'
  ];

  let sources = $state<WishlistSource[]>([]);
  let items = $state<WishlistItem[]>([]);
  let total = $state(0);
  // Album completion's queue, kept apart from the list you curated. Collapsed by default: it's
  // machinery you occasionally want to inspect, not something to scroll past every visit.
  let albumFillItems = $state<WishlistItem[]>([]);
  let albumFillTotal = $state(0);
  let albumFillOpen = $state(false);
  let statusFilter = $state<Filter>('All');
  let loading = $state(true);
  let error = $state<string | null>(null);
  let triggering = $state(false);
  let retryingFailed = $state(false);
  let banner = $state<{ type: 'success' | 'error'; message: string } | null>(null);
  let busyItems = $state(new Set<number>());
  let busySources = $state(new Set<number>());
  let progress = $state<ProgressSnapshot | null>(null);

  // Wishlist-download config: `downloadsEnabled` is the deploy-time feature switch; `autoDownload` is the
  // runtime toggle the owner flips below. Until settings load, null means "unknown" so nothing flashes.
  let downloadsEnabled = $state<boolean | null>(null);
  let autoDownload = $state<boolean | null>(null);
  let autoDownloadBusy = $state(false);
  let albumCompletion = $state<boolean | null>(null);
  let albumCompletionBusy = $state(false);
  let completionRunning = $state(false);

  async function loadSources() {
    try {
      const result = await fetchWishlistSources();
      sources = result.sources;
    } catch {
      // Non-fatal — the item list is the primary view.
    }
  }

  async function loadSettings() {
    try {
      const settings = await fetchSettings();
      downloadsEnabled = settings.downloads.enabled;
      autoDownload = settings.downloads.autoDownload;
      albumCompletion = settings.downloads.albumCompletion ?? false;
    } catch {
      // Non-fatal — the download buttons still work; we just can't show the auto-download state.
    }
  }

  async function onToggleAlbumCompletion() {
    if (albumCompletion === null) return;
    const next = !albumCompletion;
    albumCompletionBusy = true;
    banner = null;
    try {
      await updateSettings({ downloads: { albumCompletion: next } });
      albumCompletion = next;

      if (!next) {
        banner = {
          type: 'success',
          message:
            'Album completion off — nothing new will be queued. Already-queued tracks still download.'
        };
        return;
      }

      // Run a pass immediately. The background loop ticks hourly, so without this, switching the
      // feature on appears to do nothing at all for up to an hour.
      await runCompletionPass('Album completion on.');
    } catch (err) {
      banner = {
        type: 'error',
        message: err instanceof Error ? err.message : 'Failed to update album completion'
      };
    } finally {
      albumCompletionBusy = false;
    }
  }

  /** Runs one pass and reports the outcome — including, explicitly, a zero. */
  async function runCompletionPass(prefix = '') {
    const result = await runAlbumCompletion();
    banner = { type: 'success', message: `${prefix} ${albumCompletionSummary(result)}`.trim() };
    if (result.tracksQueued > 0) {
      albumFillOpen = true;
      await loadItems(true);
    }
  }

  async function onRunCompletionNow() {
    completionRunning = true;
    banner = null;
    try {
      await runCompletionPass();
    } catch (err) {
      banner = {
        type: 'error',
        message: err instanceof Error ? err.message : 'Failed to run album completion'
      };
    } finally {
      completionRunning = false;
    }
  }

  async function onToggleAutoDownload() {
    if (autoDownload === null) return;
    const next = !autoDownload;
    autoDownloadBusy = true;
    banner = null;
    try {
      await updateSettings({ downloads: { autoDownload: next } });
      autoDownload = next;
      banner = {
        type: 'success',
        message: next
          ? 'Auto-download on — new liked tracks will download automatically.'
          : 'Auto-download off — use “Download now” to fetch pending tracks.'
      };
    } catch (err) {
      banner = { type: 'error', message: err instanceof Error ? err.message : 'Failed to update auto-download' };
    } finally {
      autoDownloadBusy = false;
    }
  }

  async function loadItems(quiet = false) {
    // Quiet reloads (live polling during a download) skip the spinner + error banner so the list
    // updates in place without flicker; only an explicit load surfaces those.
    if (!quiet) loading = true;
    error = null;
    const status = statusFilter === 'All' ? undefined : statusFilter;
    try {
      // Two separate paged requests, not one list partitioned client-side: album-fill items can
      // outnumber real wishlist rows by orders of magnitude and would consume every page.
      const [mine, fill] = await Promise.all([
        fetchWishlist(status, 0, 200, 'user'),
        fetchWishlist(status, 0, 200, 'albumfill')
      ]);
      items = mine.items;
      total = mine.total;
      albumFillItems = fill.items;
      albumFillTotal = fill.total;
    } catch (err) {
      if (!quiet) error = err instanceof Error ? err.message : 'Failed to load wishlist';
    } finally {
      if (!quiet) loading = false;
    }
  }

  $effect(() => {
    void loadSources();
    void loadSettings();
  });

  // Reload the item list whenever the status filter changes.
  $effect(() => {
    void statusFilter;
    void loadItems();
  });

  // Live download progress: refresh the list when a download run finishes.
  $effect(() => {
    let wasRunning = false;
    const close = openProgressStream((snapshot) => {
      progress = snapshot;
      const running = snapshot.download?.status === 'Running';
      if (running) wasRunning = true;
      else if (wasRunning) {
        wasRunning = false;
        void loadItems();
        void loadSources();
      }
    });
    return () => close();
  });

  // While a download run is active, poll the list so rows move Pending → Downloading → Downloaded
  // live instead of only refreshing once the run completes. Quiet reload = no spinner flicker.
  $effect(() => {
    if (!downloadingNow) return;
    const id = setInterval(() => void loadItems(true), 3000);
    return () => clearInterval(id);
  });

  async function onTriggerDownload() {
    triggering = true;
    banner = null;
    try {
      await triggerWishlistDownload();
      banner = { type: 'success', message: 'Download started.' };
    } catch (err) {
      banner = { type: 'error', message: err instanceof Error ? err.message : 'Failed to start download' };
    } finally {
      triggering = false;
    }
  }

  async function onRetryAllFailed() {
    retryingFailed = true;
    banner = null;
    try {
      const { reset } = await retryFailedWishlistItems();
      banner = { type: 'success', message: `Requeued ${reset} item${reset === 1 ? '' : 's'} — click “Download now” to retry.` };
      await loadItems();
    } catch (err) {
      banner = { type: 'error', message: err instanceof Error ? err.message : 'Failed to requeue items' };
    } finally {
      retryingFailed = false;
    }
  }

  function setBusyItem(id: number, busy: boolean) {
    const next = new Set(busyItems);
    if (busy) next.add(id);
    else next.delete(id);
    busyItems = next;
  }

  async function onRetry(item: WishlistItem) {
    setBusyItem(item.id, true);
    try {
      await retryWishlistItem(item.id);
      await loadItems();
    } catch (err) {
      banner = { type: 'error', message: err instanceof Error ? err.message : 'Retry failed' };
    } finally {
      setBusyItem(item.id, false);
    }
  }

  async function onRemoveItem(item: WishlistItem) {
    setBusyItem(item.id, true);
    try {
      await removeWishlistItem(item.id);
      items = items.filter((i) => i.id !== item.id);
      total = Math.max(0, total - 1);
    } catch (err) {
      banner = { type: 'error', message: err instanceof Error ? err.message : 'Remove failed' };
    } finally {
      setBusyItem(item.id, false);
    }
  }

  function setBusySource(id: number, busy: boolean) {
    const next = new Set(busySources);
    if (busy) next.add(id);
    else next.delete(id);
    busySources = next;
  }

  async function onToggleAutoSync(source: WishlistSource) {
    setBusySource(source.id, true);
    try {
      const result = await setWishlistSourceAutoSync(source.id, !source.autoSync);
      sources = sources.map((s) => (s.id === source.id ? { ...s, autoSync: result.autoSync } : s));
    } catch (err) {
      banner = { type: 'error', message: err instanceof Error ? err.message : 'Update failed' };
    } finally {
      setBusySource(source.id, false);
    }
  }

  async function onRemoveSource(source: WishlistSource) {
    setBusySource(source.id, true);
    try {
      await removeWishlistSource(source.id);
      sources = sources.filter((s) => s.id !== source.id);
    } catch (err) {
      banner = { type: 'error', message: err instanceof Error ? err.message : 'Remove failed' };
    } finally {
      setBusySource(source.id, false);
    }
  }

  // Three token-based treatments: neutral (queued/in-progress/skipped), destructive (failed), and
  // primary (downloaded) — no hardcoded brand/traffic-light hexes.
  function statusBadgeClass(status: WishlistItemStatus): string {
    switch (status) {
      case 'Downloaded':
        return 'border-0 bg-primary/15 text-primary';
      case 'Failed':
      case 'NotFound':
        return 'border-0 bg-destructive/15 text-destructive';
      default:
        return 'border-0 bg-muted text-muted-foreground';
    }
  }

  // A small status dot to reinforce the badge without relying on color alone — neutral/destructive/primary.
  function statusDotClass(status: WishlistItemStatus): string {
    switch (status) {
      case 'Downloaded':
        return 'bg-primary';
      case 'Failed':
      case 'NotFound':
        return 'bg-destructive';
      default:
        return 'bg-muted-foreground/60';
    }
  }

  function fmtDuration(ms: number): string {
    const total = Math.round(ms / 1000);
    const m = Math.floor(total / 60);
    const s = total % 60;
    return `${m}:${s.toString().padStart(2, '0')}`;
  }

  const downloadingNow = $derived(progress?.download?.status === 'Running');
  // Show the toggle/hint only when the feature is available on this deployment.
  const showAutoDownloadControl = $derived(downloadsEnabled === true && autoDownload !== null);
  const autoDownloadOff = $derived(showAutoDownloadControl && autoDownload === false);
</script>

<div class="flex min-h-0 flex-1 flex-col overflow-hidden">
  <!-- The status chips used to be their own row below a 105px title band; both
       fit here. The deleted blurb pointed at /spotify for sources — the Sources
       section further down already links there. -->
  <PageToolbarV2
    icon={Gift}
    title="Wishlist"
    meta="{total.toLocaleString()} queued"
    metaFrom="lg"
  >
    {#snippet filters()}
      {#each FILTERS as f (f)}
        <FilterChip pressed={statusFilter === f} onclick={() => (statusFilter = f)}>
          {f === 'SkippedOwned' ? 'Skipped' : f}
        </FilterChip>
      {/each}
    {/snippet}
    {#snippet actions()}
      {#if showAutoDownloadControl}
        <label
          class="border-border bg-card text-nav-sm flex h-8 cursor-pointer items-center gap-2 rounded-full border px-3"
          title="When on, newly liked tracks download automatically in the background. When off, use “Download now”."
        >
          <Switch
            checked={autoDownload ?? false}
            disabled={autoDownloadBusy}
            onCheckedChange={onToggleAutoDownload}
            aria-label="Auto-download new tracks"
          />
          <span class="hidden select-none sm:inline">Auto-download</span>
        </label>
        <label
          class="border-border bg-card text-nav-sm flex h-8 cursor-pointer items-center gap-2 rounded-full border px-3"
          title="When on, owning one track of an album queues the rest — a few albums per hour, and always behind anything you asked for. Nobody asked for those tracks directly, so they carry an album-fill origin."
        >
          <Switch
            checked={albumCompletion ?? false}
            disabled={albumCompletionBusy}
            onCheckedChange={onToggleAlbumCompletion}
            aria-label="Complete albums automatically"
          />
          <span class="hidden select-none sm:inline">Complete albums</span>
        </label>
      {/if}
      <Button
        variant="outline"
        size="sm"
        class="h-8 gap-1.5 px-2.5"
        onclick={onRetryAllFailed}
        disabled={retryingFailed || downloadingNow}
        title="Reset all Failed/NotFound items to Pending so the next download retries them"
      >
        {#if retryingFailed}
          <Loader2 class="size-4 animate-spin" />
        {:else}
          <RefreshCw class="size-4" />
        {/if}
        <span class="text-nav-sm hidden sm:inline">Retry failed</span>
      </Button>
      <Button
        size="sm"
        class="h-8 gap-1.5 px-2.5"
        onclick={onTriggerDownload}
        disabled={triggering || downloadingNow}
      >
        {#if triggering || downloadingNow}
          <Loader2 class="size-4 animate-spin" />
        {:else}
          <Download class="size-4" />
        {/if}
        <span class="text-nav-sm hidden sm:inline"
          >{downloadingNow ? `Downloading ${progress?.downloaded ?? 0}…` : 'Download now'}</span
        >
      </Button>
    {/snippet}
  </PageToolbarV2>

  {#if banner}
    <div
      class="mx-4 mt-3 rounded-md border px-3 py-2 text-sm md:mx-6 {banner.type === 'success'
        ? 'border-primary/30 bg-primary/10 text-primary'
        : 'border-destructive/30 bg-destructive/10 text-destructive'}"
    >
      {banner.message}
    </div>
  {/if}

  {#if autoDownloadOff && !downloadingNow}
    <div
      class="border-border bg-muted/40 text-muted-foreground mx-4 mt-3 flex items-start gap-2 rounded-md border px-3 py-2 text-sm md:mx-6"
    >
      <Info class="mt-0.5 size-4 shrink-0" />
      <span>
        Auto-download is off — Pending tracks stay queued and won't download on their own. Turn on
        <span class="font-medium">Auto-download</span> to fetch new likes automatically, or click
        <span class="font-medium">Download now</span> for a one-off sweep.
      </span>
    </div>
  {/if}

  <ScrollArea class="min-h-0 flex-1">
    <!-- Sources -->
    {#if sources.length > 0}
      <div class="border-border border-b px-4 py-4 md:px-6">
        <h2 class="text-muted-foreground mb-2 text-sm font-medium">Sources</h2>
        <div class="divide-border divide-y">
          {#each sources as source (source.id)}
            <div class="hover:bg-secondary/40 flex items-center gap-3 rounded-md px-1 py-2.5 transition-colors">
              {#if source.imageUrl}
                <img src={source.imageUrl} alt="" class="size-8 shrink-0 rounded object-cover" crossorigin="anonymous" />
              {:else}
                <Music class="text-muted-foreground size-4 shrink-0" />
              {/if}
              <div class="min-w-0 flex-1">
                <div class="flex items-center gap-2">
                  <span class="truncate text-sm font-medium">{source.name}</span>
                  <Badge variant="outline" class="shrink-0 text-[10px]">
                    {source.provider === 'deezer' ? 'Deezer' : 'Spotify'}
                  </Badge>
                </div>
                <div class="text-muted-foreground text-xs">
                  {source.sourceType === 'LikedSongs' ? 'Liked Songs' : 'Playlist'} · {source.itemCount} tracks{source.lastSyncedAtUtc
                    ? ` · synced ${new Date(source.lastSyncedAtUtc).toLocaleString()}`
                    : ''}
                </div>
              </div>
              <label class="text-muted-foreground flex cursor-pointer items-center gap-1.5 text-xs">
                <Switch
                  size="sm"
                  checked={source.autoSync}
                  disabled={busySources.has(source.id)}
                  onCheckedChange={() => onToggleAutoSync(source)}
                  aria-label="Auto-sync"
                />
                Auto-sync
              </label>
              <Button
                variant="ghost"
                size="icon"
                class="size-10 shrink-0"
                aria-label="Remove source"
                title="Remove source"
                disabled={busySources.has(source.id)}
                onclick={() => onRemoveSource(source)}
              >
                <Trash2 class="size-4" />
              </Button>
            </div>
          {/each}
        </div>
      </div>
    {/if}

    <!-- Items -->
    {#if albumCompletion && showAutoDownloadControl}
      <!-- The background loop is deliberately slow (hourly), so there has to be a way to ask now
           and, more importantly, to get an answer — "nothing queued" is a normal result and needs
           to be distinguishable from "broken". -->
      <div class="border-border flex items-center gap-3 border-t px-4 py-3 md:px-6">
        <Button
          variant="outline"
          size="sm"
          class="h-8 shrink-0 gap-1.5 px-2.5"
          onclick={onRunCompletionNow}
          disabled={completionRunning}
          title="Check your albums for missing tracks now, instead of waiting for the hourly pass"
        >
          {#if completionRunning}
            <Loader2 class="size-4 animate-spin" />
          {:else}
            <RefreshCw class="size-4" />
          {/if}
          <span class="text-nav-sm">Check albums now</span>
        </Button>
        <span class="text-muted-foreground text-xs">
          Runs automatically every hour, a few albums at a time.
        </span>
      </div>
    {/if}

    {#if error}
      <div class="flex flex-col items-center justify-center py-12 text-center">
        <AlertCircle class="text-destructive mb-3 size-10" />
        <p class="text-muted-foreground">{error}</p>
        <Button variant="outline" size="sm" class="mt-4" onclick={() => loadItems()}>Retry</Button>
      </div>
    {:else if loading}
      <div class="flex items-center justify-center py-12">
        <Loader2 class="text-muted-foreground size-6 animate-spin" />
      </div>
    {:else if items.length === 0 && albumFillTotal === 0}
      <div class="flex flex-col items-center justify-center py-12 text-center">
        <Heart class="text-muted-foreground mb-3 size-10" />
        <p class="text-muted-foreground">No wishlist items{statusFilter === 'All' ? ' yet' : ` (${statusFilter})`}.</p>
      </div>
    {:else}
      {#if items.length > 0}
        <div class="divide-border divide-y px-2 md:px-4">
          {#each items as item (item.id)}
            {@render itemRow(item)}
          {/each}
        </div>
      {/if}

      <!-- Deliberately outside the "you have items" branch: turning album completion on for the
           first time leaves you with zero requested items and a full fill queue, which is exactly
           when hiding it would read as "it did nothing". -->
      {#if albumFillTotal > 0}
        <!-- Album completion's own queue. Separate and collapsed: it's a background drip that can
             dwarf the list you curated, and it pages separately for the same reason. -->
        <div class="border-border mt-2 border-t px-2 md:px-4">
          <button
            type="button"
            onclick={() => (albumFillOpen = !albumFillOpen)}
            aria-expanded={albumFillOpen}
            class="text-muted-foreground hover:text-foreground flex w-full items-center gap-2 px-2 py-3 text-left text-xs transition-colors"
          >
            <ChevronRight class={`size-3.5 shrink-0 transition-transform ${albumFillOpen ? 'rotate-90' : ''}`} />
            <span class="font-medium">Album fill</span>
            <span class="tabular-nums opacity-70">{albumFillTotal.toLocaleString()}</span>
            <span class="hidden opacity-70 sm:inline">
              · tracks queued to complete albums you already own part of
            </span>
          </button>
          {#if albumFillOpen}
            <div class="divide-border divide-y">
              {#each albumFillItems as item (item.id)}
                {@render itemRow(item)}
              {/each}
            </div>
          {/if}
        </div>
      {/if}
    {/if}
  </ScrollArea>
</div>

{#snippet itemRow(item: WishlistItem)}
        <div class="hover:bg-secondary/40 flex items-center gap-3 px-2 py-2.5 transition-colors">
          <div class="bg-secondary size-10 shrink-0 overflow-hidden rounded">
            {#if item.albumArt}
              <img src={item.albumArt} alt="" class="size-full object-cover" crossorigin="anonymous" />
            {:else}
              <div class="flex size-full items-center justify-center">
                <Music class="text-muted-foreground size-4" />
              </div>
            {/if}
          </div>
          <div class="min-w-0 flex-1">
            <div class="truncate text-sm font-medium">{item.title}</div>
            <div class="text-muted-foreground truncate text-xs">
              {item.artist}{item.album ? ` · ${item.album}` : ''}{item.downloadProvider &&
              (item.status === 'Downloaded' || item.downloadedSongId != null)
                ? ` · via ${item.downloadProvider}`
                : ''}
            </div>
            {#if item.lastError && (item.status === 'Failed' || item.status === 'NotFound')}
              <div class="text-destructive mt-0.5 truncate text-xs" title={item.lastError}>{item.lastError}</div>
            {/if}
          </div>

          {#if item.downloadedSongId != null}
            <Button
              variant="outline"
              size="sm"
              class={`hidden h-8 shrink-0 px-2.5 text-xs font-medium sm:inline-flex ${
                item.libraryBuildStatus === 'Done'
                  ? 'border-primary/40 bg-primary/15 text-primary hover:bg-primary/25'
                  : ''
              }`}
              title="Open this song in your library"
              onclick={() => songDetail.open(item.downloadedSongId!)}
            >
              {#if item.libraryBuildStatus === 'Done'}
                <CheckCircle2 class="mr-1 size-3.5 shrink-0" />
                In library
              {:else}
                {item.libraryEnrichmentStatus ?? 'Processing'}
              {/if}
            </Button>
          {/if}

          <span class="text-muted-foreground hidden w-12 shrink-0 text-right text-xs sm:inline">
            {fmtDuration(item.durationMs)}
          </span>

          <Badge
            class="{statusBadgeClass(item.status)} gap-1.5"
            title={item.status === 'Downloaded' && item.downloadedSongId == null
              ? 'Downloaded — being added to your library'
              : undefined}
          >
            <span class="size-1.5 shrink-0 rounded-full {statusDotClass(item.status)}"></span>
            {item.status === 'SkippedOwned'
              ? 'Skipped'
              : item.status === 'Downloaded' && item.downloadedSongId == null
                ? 'Adding…'
                : item.status}
          </Badge>

          {#if item.status === 'Failed' || item.status === 'NotFound'}
            <Button
              variant="ghost"
              size="icon"
              class="size-10 shrink-0"
              aria-label="Retry"
              title="Retry"
              disabled={busyItems.has(item.id)}
              onclick={() => onRetry(item)}
            >
              <RefreshCw class="size-4" />
            </Button>
          {/if}
          <Button
            variant="ghost"
            size="icon"
            class="size-10 shrink-0"
            aria-label="Remove"
            title="Remove"
            disabled={busyItems.has(item.id)}
            onclick={() => onRemoveItem(item)}
          >
            <Trash2 class="size-4" />
          </Button>
        </div>
{/snippet}
