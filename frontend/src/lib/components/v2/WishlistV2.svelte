<script lang="ts" module>
  /**
   * The download worker's `lastError` is raw provider output (a stderr tail, an exception message).
   * Recognise the failure classes that recur and say, in one sentence, what happened and what to do
   * next; the raw text stays one tap away for anyone who needs it. Order matters: a deferred
   * provider outage ("<provider> unavailable: …") must not read as "video unavailable", and the
   * server-setup and bot-check hints win over the generic classes they often co-occur with.
   */
  type DownloadErrorSummary = { summary: string; next: string | null };

  const DOWNLOAD_ERROR_CLASSES: { test: RegExp; summary: string; next: string | null }[] = [
    {
      test: /^[\w.-]+ unavailable: /i,
      summary: "A download source couldn't be reached, so this track wasn't really tried.",
      next: 'Nothing is wrong with the track.'
    },
    {
      test: /not configured|no download provider|javascript runtime|precondition check failed|read-only file system|out of date/i,
      summary: "The server's downloader needs attention before it can fetch this.",
      next: 'Check the download setup on the server, then retry.'
    },
    {
      test: /not a bot|bot check|confirm you[’']re not/i,
      summary: 'The source asked this server to sign in (a bot check).',
      next: 'Add signed-in browser cookies on the server, then retry.'
    },
    {
      test: /age[- ]?restrict|confirm your age|inappropriate for some users/i,
      summary: 'The source is age-restricted.',
      next: "Retrying won't help without signed-in cookies on the server — or remove it."
    },
    {
      test: /not (?:made this video )?available in your country|geo[- ]?(?:restrict|block)|blocked in your (?:country|region)/i,
      summary: "The source is blocked in this server's region.",
      next: "Retrying from here won't help — remove it."
    },
    {
      test: /private video|video is private/i,
      summary: 'The source video is private.',
      next: 'Remove it, or retry if it is made public.'
    },
    {
      test: /\b429\b|too many requests|rate[- ]?limit|quota/i,
      summary: 'The source is rate-limiting this server.',
      next: 'Nothing is wrong with the track — a later retry usually works.'
    },
    {
      test: /no downloadable audio stream|requested format is not available|only images are available|po[ _]token/i,
      summary: "The source didn't offer a downloadable audio stream to this server.",
      next: 'Nothing is wrong with the track — a later retry usually works.'
    },
    {
      test: /video (?:is )?unavailable|not available|has been removed|been terminated|copyright|no longer available/i,
      summary: 'The source for this track was removed or is unavailable.',
      next: 'Retry later, or remove it.'
    },
    {
      test: /no results|no acceptable|no lossless source|candidates failed|no strictly better|not found|no resolvable/i,
      summary: 'No source had a match for this track.',
      next: 'Retry later, or remove it.'
    },
    {
      test: /timed out|timeout|connection|network|unreachable|reset by peer/i,
      summary: 'The download timed out or lost its connection.',
      next: null
    }
  ];

  function describeDownloadError(raw: string): DownloadErrorSummary {
    for (const c of DOWNLOAD_ERROR_CLASSES) {
      if (c.test.test(raw)) return { summary: c.summary, next: c.next };
    }
    return { summary: 'The download failed.', next: null };
  }
</script>

<script lang="ts">
  import { Button } from '$lib/components/ui/button';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Badge } from '$lib/components/ui/badge';
  import FilterChip from '$lib/components/v2/FilterChip.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { Switch } from '$lib/components/ui/switch';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import { toast } from 'svelte-sonner';
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
    Info,
    Lock
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
    isPermissionError,
    type WishlistItem,
    type WishlistSource,
    type WishlistItemStatus,
    type ProgressSnapshot
  } from '$lib/api-client';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { formatRelativeFuture } from '$lib/formatters';

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

  // The wire enum stays the filter value; people read these.
  const STATUS_LABEL: Record<Filter, string> = {
    All: 'All',
    Pending: 'Pending',
    Downloading: 'Downloading',
    Downloaded: 'Downloaded',
    SkippedOwned: 'Skipped',
    Failed: 'Failed',
    NotFound: 'Not found'
  };

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
  // A 403 is a property of the account, not a transient failure: say so instead of offering a
  // Retry that can only fail the same way.
  let errorIsPermission = $state(false);
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
    errorIsPermission = false;
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
      if (!quiet) {
        error = err instanceof Error ? err.message : 'Failed to load wishlist';
        errorIsPermission = isPermissionError(err);
      }
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
      banner = { type: 'success', message: `Requeued ${reset} item${reset === 1 ? '' : 's'} — use “Download now” to retry.` };
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

  // Removing an item is common and cheap, so it gets an Undo instead of a confirm. The API has no
  // way to put a deleted row back (DELETE is a hard delete and there is no add-item endpoint), so
  // the undo is faithful by never sending the DELETE until the window closes: the row hides now,
  // and the request goes out when the toast auto-closes or is dismissed. Undo just un-hides it.
  // A reload inside the window drops the removal, which fails safe — the item is still there.
  const UNDO_MS = 5000;
  let pendingRemoval = $state(new Set<number>());

  function setPendingRemoval(id: number, pending: boolean) {
    const next = new Set(pendingRemoval);
    if (pending) next.add(id);
    else next.delete(id);
    pendingRemoval = next;
  }

  async function commitRemoveItem(item: WishlistItem) {
    if (!pendingRemoval.has(item.id)) return;
    try {
      await removeWishlistItem(item.id);
      if (items.some((i) => i.id === item.id)) {
        items = items.filter((i) => i.id !== item.id);
        total = Math.max(0, total - 1);
      }
      if (albumFillItems.some((i) => i.id === item.id)) {
        albumFillItems = albumFillItems.filter((i) => i.id !== item.id);
        albumFillTotal = Math.max(0, albumFillTotal - 1);
      }
    } catch (err) {
      // A toast, not the banner: this can land after you've left the page.
      toast.error(`Couldn't remove “${item.title}”`, {
        description: err instanceof Error ? err.message : undefined
      });
    } finally {
      setPendingRemoval(item.id, false);
    }
  }

  function onRemoveItem(item: WishlistItem) {
    if (pendingRemoval.has(item.id)) return;
    setPendingRemoval(item.id, true);
    // Exactly one of undo / commit ever runs, whichever of the toast's exits fires first.
    let settled = false;
    const commit = () => {
      if (settled) return;
      settled = true;
      void commitRemoveItem(item);
    };
    toast('Removed from wishlist', {
      description: item.title,
      duration: UNDO_MS,
      action: {
        label: 'Undo',
        onClick: () => {
          if (settled) return;
          settled = true;
          setPendingRemoval(item.id, false);
        }
      },
      onAutoClose: commit,
      onDismiss: commit
    });
  }

  const visibleItems = $derived(items.filter((i) => !pendingRemoval.has(i.id)));
  const visibleAlbumFill = $derived(albumFillItems.filter((i) => !pendingRemoval.has(i.id)));
  const visibleTotal = $derived(Math.max(0, total - (items.length - visibleItems.length)));
  const visibleAlbumFillTotal = $derived(
    Math.max(0, albumFillTotal - (albumFillItems.length - visibleAlbumFill.length))
  );

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

  // Removing a source drops its subscription and sync history (items it already added stay). That
  // is not undone by adding it again, so it gets the same AlertDialog as removing a person.
  let sourceToRemove = $state<WishlistSource | null>(null);
  let removeSourceOpen = $state(false);

  function askRemoveSource(source: WishlistSource) {
    sourceToRemove = source;
    removeSourceOpen = true;
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
        return 'border-0 bg-destructive/15 text-destructive-text';
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
        return 'bg-muted-foreground-dim';
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
    meta="{visibleTotal.toLocaleString()} queued"
    metaFrom="lg"
  >
    {#snippet filters()}
      {#each FILTERS as f (f)}
        <FilterChip pressed={statusFilter === f} onclick={() => (statusFilter = f)}>
          {STATUS_LABEL[f]}
        </FilterChip>
      {/each}
    {/snippet}
    {#snippet actions()}
      {#if showAutoDownloadControl}
        <!-- Labelled at every width: a bare switch says nothing on a phone, where the title
             tooltip never shows. Below sm there is no room in the bar, so they move to the
             band underneath instead of losing their words. -->
        <div class="hidden items-center gap-1.5 sm:flex">
          {@render downloadSwitches()}
        </div>
      {/if}
      <Button
        variant="outline"
        size="sm"
        class="h-8 gap-1.5 px-2.5"
        onclick={onRetryAllFailed}
        disabled={retryingFailed || downloadingNow}
        aria-label="Retry failed"
        title="Put every failed or not-found item back in the queue so the next download retries it"
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
        aria-label={downloadingNow ? `Downloading ${progress?.downloaded ?? 0}` : 'Download now'}
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

  {#if showAutoDownloadControl}
    <div class="border-border flex shrink-0 flex-wrap items-center gap-2 border-b px-4 py-2 sm:hidden">
      {@render downloadSwitches()}
    </div>
  {/if}

  {#if banner}
    <div
      class="mx-4 mt-3 rounded-md border px-3 py-2 text-sm md:mx-6 {banner.type === 'success'
        ? 'border-primary/30 bg-primary/10 text-primary'
        : 'border-destructive/30 bg-destructive/10 text-destructive-text'}"
    >
      {banner.message}
    </div>
  {/if}

  {#if autoDownloadOff && !downloadingNow && !errorIsPermission}
    <div
      class="border-border bg-muted/40 text-muted-foreground mx-4 mt-3 flex items-start gap-2 rounded-md border px-3 py-2 text-sm md:mx-6"
    >
      <Info class="mt-0.5 size-4 shrink-0" />
      <span>
        Auto-download is off — Pending tracks stay queued and won't download on their own. Turn on
        <span class="font-medium">Auto-download</span> to fetch new likes automatically, or use
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
                  <Badge variant="outline" class="shrink-0 text-[11px]">
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
                aria-label={`Remove ${source.name}`}
                title="Remove source"
                disabled={busySources.has(source.id)}
                onclick={() => askRemoveSource(source)}
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

    {#if error && errorIsPermission}
      <div class="flex flex-col items-center justify-center px-6 py-12 text-center">
        <Lock class="text-muted-foreground mb-3 size-9" />
        <p class="text-foreground font-medium">{error}</p>
        <p class="text-muted-foreground mt-1 max-w-sm text-sm">
          This account can't manage the wishlist, so trying again won't change anything.
        </p>
      </div>
    {:else if error}
      <div class="flex flex-col items-center justify-center py-12 text-center">
        <AlertCircle class="text-destructive-text mb-3 size-10" />
        <p class="text-muted-foreground">{error}</p>
        <Button variant="outline" size="sm" class="mt-4" onclick={() => loadItems()}>Retry</Button>
      </div>
    {:else if loading}
      <div class="flex items-center justify-center py-12">
        <Loader2 class="text-muted-foreground size-6 animate-spin" />
      </div>
    {:else if visibleItems.length === 0 && visibleAlbumFillTotal === 0}
      <div class="flex flex-col items-center justify-center py-12 text-center">
        <Heart class="text-muted-foreground mb-3 size-10" />
        <p class="text-muted-foreground">No wishlist items{statusFilter === 'All' ? ' yet' : ` (${STATUS_LABEL[statusFilter].toLowerCase()})`}.</p>
      </div>
    {:else}
      {#if visibleItems.length > 0}
        <div class="divide-border divide-y px-2 md:px-4">
          {#each visibleItems as item (item.id)}
            {@render itemRow(item)}
          {/each}
        </div>
      {/if}

      <!-- Deliberately outside the "you have items" branch: turning album completion on for the
           first time leaves you with zero requested items and a full fill queue, which is exactly
           when hiding it would read as "it did nothing". -->
      {#if visibleAlbumFillTotal > 0}
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
            <span class="text-muted-foreground-dim tabular-nums">{visibleAlbumFillTotal.toLocaleString()}</span>
            <span class="text-muted-foreground-dim hidden sm:inline">
              · tracks queued to complete albums you already own part of
            </span>
          </button>
          {#if albumFillOpen}
            <div class="divide-border divide-y">
              {#each visibleAlbumFill as item (item.id)}
                {@render itemRow(item)}
              {/each}
            </div>
          {/if}
        </div>
      {/if}
    {/if}
  </ScrollArea>
</div>

{#snippet downloadSwitches()}
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
    <span class="select-none">Auto-download</span>
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
    <span class="select-none">Complete albums</span>
  </label>
{/snippet}

<AlertDialog.Root bind:open={removeSourceOpen}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Remove “{sourceToRemove?.name ?? 'this source'}”?</AlertDialog.Title>
      <AlertDialog.Description>
        Its tracks stop syncing into your wishlist and its sync history is dropped — adding it again
        starts over. Tracks it already added stay on the wishlist and in your library.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action
        variant="destructive"
        onclick={() => {
          if (sourceToRemove) void onRemoveSource(sourceToRemove);
        }}
      >
        Remove source
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

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
                : ''}{item.fallbackFromProvider && item.status === 'Downloaded'
                ? ` · ${item.fallbackFromProvider} was unavailable${item.libraryBuildStatus === 'Done' ? ', upgrade pending' : ''}`
                : ''}
            </div>
            {#if item.lastError && (item.status === 'Failed' || item.status === 'NotFound')}
              {@const why = describeDownloadError(item.lastError)}
              <p class="mt-0.5 text-xs">
                <span class="text-destructive-text">{why.summary}</span>
                {#if why.next}<span class="text-muted-foreground"> {why.next}</span>{/if}
              </p>
              <!-- The raw worker output, readable on a phone (a title tooltip never shows there). -->
              <details class="text-xs">
                <summary
                  class="text-muted-foreground hover:text-foreground w-fit cursor-pointer py-1 select-none pointer-coarse:py-2"
                >
                  Details
                </summary>
                <pre
                  class="bg-muted/60 text-muted-foreground mt-0.5 mb-1 max-h-40 overflow-auto rounded-md p-2 font-mono text-[11px] leading-snug break-all whitespace-pre-wrap">{item.lastError}</pre>
              </details>
            {/if}
            {#if item.status === 'Failed'}
              <div class="text-muted-foreground mt-0.5 truncate text-xs">
                {#if item.nextAttemptAtUtc}
                  Retries automatically {formatRelativeFuture(item.nextAttemptAtUtc)}{item.attemptCount > 0
                    ? ` · ${item.attemptCount} attempt${item.attemptCount === 1 ? '' : 's'} so far`
                    : ''}
                {:else}
                  No more automatic retries{item.attemptCount > 0
                    ? ` after ${item.attemptCount} attempt${item.attemptCount === 1 ? '' : 's'}`
                    : ''} — press Retry to try again
                {/if}
              </div>
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
            {item.status === 'Downloaded' && item.downloadedSongId == null
              ? 'Adding…'
              : (STATUS_LABEL[item.status] ?? item.status)}
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
