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
  import { Badge } from '$lib/components/ui/badge';
  import FilterChip from '$lib/components/v2/FilterChip.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { Switch } from '$lib/components/ui/switch';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { cn } from '$lib/utils';
  import { toast } from 'svelte-sonner';
  import {
    Heart,
    Download,
    RotateCw,
    Trash2,
    Loader2,
    AlertCircle,
    CircleCheck,
    ChevronRight,
    Music,
    ListVideo,
    Lock,
    Disc3,
    Ellipsis,
    SlidersHorizontal,
    X
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
    playlistProviderLabel,
    type WishlistItem,
    type WishlistSource,
    type WishlistItemStatus,
    type ProgressSnapshot
  } from '$lib/api-client';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { formatRelativeFuture, formatRelativeTime } from '$lib/formatters';

  type Filter = WishlistItemStatus | 'All';

  // Compact (below md): items carry a status word and a ⋯ menu, and a row with a song opens it;
  // md+ keeps the explicit In library / Retry / Remove controls on each row.
  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);

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

  // Every switch on this page is bound through a getter/setter: it shows the request's intent
  // while it runs and the saved value after, so a failed request puts the thumb back. (A one-way
  // `checked` leaves it flipped over a setting that did not change.)
  let autoDownloadPending = $state<boolean | null>(null);
  let albumCompletionPending = $state<boolean | null>(null);
  let autoSyncPending = $state<Record<number, boolean>>({});

  async function onToggleAlbumCompletion() {
    if (albumCompletion === null) return;
    const next = !albumCompletion;
    albumCompletionBusy = true;
    albumCompletionPending = next;
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
      albumCompletionPending = null;
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
    autoDownloadPending = next;
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
      banner = {
        type: 'error',
        message: err instanceof Error ? err.message : 'Failed to update auto-download'
      };
    } finally {
      autoDownloadPending = null;
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
      banner = {
        type: 'error',
        message: err instanceof Error ? err.message : 'Failed to start download'
      };
    } finally {
      triggering = false;
    }
  }

  async function onRetryAllFailed() {
    retryingFailed = true;
    banner = null;
    try {
      const { reset } = await retryFailedWishlistItems();
      banner = {
        type: 'success',
        message: `Requeued ${reset} item${reset === 1 ? '' : 's'} — use “Download now” to retry.`
      };
      await loadItems();
    } catch (err) {
      banner = {
        type: 'error',
        message: err instanceof Error ? err.message : 'Failed to requeue items'
      };
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
  // Ten seconds: long enough to notice the wrong row went, read the toast and reach Undo.
  const UNDO_MS = 10_000;
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

  async function onToggleAutoSync(source: WishlistSource, on: boolean) {
    setBusySource(source.id, true);
    autoSyncPending = { ...autoSyncPending, [source.id]: on };
    try {
      const result = await setWishlistSourceAutoSync(source.id, on);
      sources = sources.map((s) => (s.id === source.id ? { ...s, autoSync: result.autoSync } : s));
    } catch (err) {
      banner = { type: 'error', message: err instanceof Error ? err.message : 'Update failed' };
    } finally {
      const rest = { ...autoSyncPending };
      delete rest[source.id];
      autoSyncPending = rest;
      setBusySource(source.id, false);
    }
  }

  const SOURCES_NOTE =
    'The switch is auto-sync: on, a source keeps following new tracks; off, it was a one-time snapshot.';

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
        return 'border-0 bg-primary/12 text-primary';
      case 'Failed':
      case 'NotFound':
        return 'border-0 bg-destructive/10 text-destructive-text';
      default:
        return 'border-0 bg-muted text-muted-foreground';
    }
  }

  function statusWord(item: WishlistItem): string {
    return item.status === 'Downloaded' && item.downloadedSongId == null
      ? 'Adding…'
      : (STATUS_LABEL[item.status] ?? item.status);
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
  const showCheckAlbums = $derived(albumCompletion === true && showAutoDownloadControl);

  // A phone opens on the list: Downloads and Sources (five rows and two footnotes) used to sit
  // above it and push "Requested" under the tab bar, so a status chip filtered a list nobody
  // could see. There they open in a sheet from the nav bar's More, and the one state that
  // explains the list (auto-download off: pending tracks wait) rides in the subtitle. md+ has the
  // room and keeps both sections inline.
  let settingsOpen = $state(false);
  const showSettingsRow = $derived(showAutoDownloadControl || sources.length > 0);
  $effect(() => {
    if (!compact) settingsOpen = false;
  });
  const hasDownloadsNote = $derived(
    (autoDownloadOff && !downloadingNow && !errorIsPermission) || albumCompletion === true
  );

  // The subtitle is the list's status summary: how many items this view holds, how many failed
  // (failed and not found, as Retry failed treats them — counted only when the whole list is
  // loaded, so the number is never short), then what the downloader is doing.
  const failedCount = $derived(
    statusFilter === 'All' && items.length >= total
      ? visibleItems.filter((i) => i.status === 'Failed' || i.status === 'NotFound').length
      : 0
  );
  const meta = $derived.by(() => {
    // Nothing until the list has loaded: "0 queued" over a spinner is not a status.
    if (loading || error) return undefined;
    const n = visibleTotal.toLocaleString();
    const parts = [
      statusFilter === 'All' ? `${n} queued` : `${n} ${STATUS_LABEL[statusFilter].toLowerCase()}`
    ];
    if (failedCount > 0) parts.push(`${failedCount.toLocaleString()} failed`);
    if (downloadingNow) parts.push(`downloading ${progress?.downloaded ?? 0}`);
    else if (compact && autoDownloadOff && !errorIsPermission) parts.push('auto-download off');
    return parts.join(' · ');
  });
</script>

{#snippet sectionHeader(title: string, count?: number, first = false)}
  <h2
    class={cn(
      'text-headline flex items-baseline gap-2 px-4 pb-1 md:px-7 md:pt-5 md:text-sm md:font-semibold',
      first ? 'pt-3' : 'pt-6'
    )}
  >
    {title}
    {#if count != null}
      <span class="text-subheadline text-muted-foreground font-normal tabular-nums md:text-xs"
        >{count.toLocaleString()}</span
      >
    {/if}
  </h2>
{/snippet}

{#snippet downloadAction()}
  {#if compact}
    <!-- The page's one prominent action keeps its tint fill in the bar's capsule. -->
    <Button
      size="icon"
      onclick={onTriggerDownload}
      disabled={triggering || downloadingNow}
      aria-label={downloadingNow ? `Downloading ${progress?.downloaded ?? 0}` : 'Download now'}
    >
      {#if triggering || downloadingNow}<Loader2 class="animate-spin" />{:else}<Download />{/if}
    </Button>
  {:else}
    <Button
      variant="outline"
      size="sm"
      class="h-8 gap-1.5 px-2.5"
      onclick={onRetryAllFailed}
      disabled={retryingFailed || downloadingNow}
      title="Put every failed or not-found item back in the queue so the next download retries it"
    >
      {#if retryingFailed}<Loader2 class="size-4 animate-spin" />{:else}<RotateCw
          class="size-4"
        />{/if}
      <span class="text-nav-sm">Retry failed</span>
    </Button>
    <Button
      size="sm"
      class="h-8 gap-1.5 px-2.5"
      onclick={onTriggerDownload}
      disabled={triggering || downloadingNow}
    >
      {#if triggering || downloadingNow}<Loader2 class="size-4 animate-spin" />{:else}<Download
          class="size-4"
        />{/if}
      <span class="text-nav-sm"
        >{downloadingNow ? `Downloading ${progress?.downloaded ?? 0}…` : 'Download now'}</span
      >
    </Button>
  {/if}
{/snippet}

{#snippet moreItems()}
  {#if compact}
    <DropdownMenu.Item disabled={retryingFailed || downloadingNow} onSelect={onRetryAllFailed}>
      <RotateCw /> Retry failed
    </DropdownMenu.Item>
  {/if}
  {#if showCheckAlbums}
    <DropdownMenu.Item disabled={completionRunning} onSelect={onRunCompletionNow}>
      <Disc3 /> Check albums now
    </DropdownMenu.Item>
  {/if}
  {#if compact && showSettingsRow}
    <DropdownMenu.Separator />
    <DropdownMenu.Item onSelect={() => (settingsOpen = true)}>
      <SlidersHorizontal /> Download settings…
    </DropdownMenu.Item>
  {/if}
{/snippet}

<div class="flex min-h-0 flex-1 flex-col">
  <ScrollArea class="min-h-0 flex-1" viewportClass="overscroll-contain">
    <PageToolbarV2
      title="Wishlist"
      {meta}
      metaFrom="lg"
      actions={downloadAction}
      more={compact || showCheckAlbums ? moreItems : undefined}
    >
      {#snippet filterRow()}
        {#each FILTERS as f (f)}
          <FilterChip pressed={statusFilter === f} onclick={() => (statusFilter = f)}>
            {STATUS_LABEL[f]}
          </FilterChip>
        {/each}
      {/snippet}
    </PageToolbarV2>

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

    {#if !compact}
      <!-- Downloads: the two deployment switches as list rows (a switch belongs in a row), each
           saying what it does — a hover title never reaches a phone. -->
      {#if showAutoDownloadControl}
        {@render sectionHeader('Downloads')}
        <div role="group" aria-label="Downloads" class="md:max-w-3xl">
          {@render downloadsRows('md:px-7')}
        </div>
        <div class="text-footnote text-muted-foreground space-y-1 px-4 pt-1.5 md:px-7 md:text-xs">
          {@render downloadsNote()}
        </div>
      {/if}

      <!-- Sources -->
      {#if sources.length > 0}
        {@render sectionHeader('Sources', sources.length)}
        <div role="group" aria-label="Sources" class="md:max-w-3xl">
          {@render sourcesRows('md:px-7')}
        </div>
        <p class="text-footnote text-muted-foreground px-4 pt-1.5 md:px-7 md:text-xs">
          {SOURCES_NOTE}
        </p>
      {/if}
    {/if}

    <!-- Items -->
    {#if error && errorIsPermission}
      <div class="flex flex-col items-center justify-center px-6 py-12 text-center">
        <Lock class="text-muted-foreground mb-3 size-9" aria-hidden="true" />
        <p class="text-body text-foreground font-medium md:text-sm">{error}</p>
        <p class="text-subheadline text-muted-foreground mt-1 max-w-sm md:text-sm">
          This account can't manage the wishlist, so trying again won't change anything.
        </p>
      </div>
    {:else if error}
      <div class="flex flex-col items-center justify-center px-6 py-12 text-center">
        <AlertCircle class="text-destructive-text mb-3 size-10" aria-hidden="true" />
        <p class="text-body text-muted-foreground md:text-sm">{error}</p>
        <Button
          variant="outline"
          class="mt-4 h-11 rounded-full px-5 md:h-8 md:rounded-lg md:px-3"
          onclick={() => loadItems()}>Retry</Button
        >
      </div>
    {:else if loading}
      <div role="status" class="flex items-center justify-center py-12">
        <Loader2 class="text-muted-foreground size-6 animate-spin" aria-hidden="true" />
        <span class="sr-only">Loading the wishlist…</span>
      </div>
    {:else if visibleItems.length === 0 && visibleAlbumFillTotal === 0}
      <div class="flex flex-col items-center justify-center py-12 text-center">
        <Heart class="text-muted-foreground mb-3 size-10" aria-hidden="true" />
        <p class="text-body text-muted-foreground md:text-sm">
          No wishlist items{statusFilter === 'All'
            ? ' yet'
            : ` (${STATUS_LABEL[statusFilter].toLowerCase()})`}.
        </p>
      </div>
    {:else}
      {#if visibleItems.length > 0}
        <!-- No count: the subtitle already says how many. On a phone it is the first thing
             under the chips, so it sits close under them. -->
        {@render sectionHeader('Requested', undefined, compact)}
        <ul aria-label="Requested tracks">
          {#each visibleItems as item (item.id)}
            {@render itemRow(item)}
          {/each}
        </ul>
      {/if}

      <!-- Deliberately outside the "you have items" branch: turning album completion on for the
           first time leaves you with zero requested items and a full fill queue, which is exactly
           when hiding it would read as "it did nothing". -->
      {#if visibleAlbumFillTotal > 0}
        <!-- Album completion's own queue. Separate and collapsed: it's a background drip that can
             dwarf the list you curated, and it pages separately for the same reason. -->
        <div class="mt-4">
          <GroupedList.Row
            class="md:px-7"
            onclick={() => (albumFillOpen = !albumFillOpen)}
            aria-expanded={albumFillOpen}
            label="Album fill"
            value={visibleAlbumFillTotal.toLocaleString()}
            sublabel="Tracks queued to complete albums you already own part of"
          >
            {#snippet leading()}
              <ChevronRight
                class="text-muted-foreground size-5 shrink-0 transition-transform duration-200 ease-[cubic-bezier(0.23,1,0.32,1)] {albumFillOpen
                  ? 'rotate-90'
                  : ''}"
                aria-hidden="true"
              />
            {/snippet}
          </GroupedList.Row>
          {#if albumFillOpen}
            <ul aria-label="Album fill">
              {#each visibleAlbumFill as item (item.id)}
                {@render itemRow(item)}
              {/each}
            </ul>
          {/if}
        </div>
      {/if}
    {/if}
  </ScrollArea>
</div>

<!-- A phone's Downloads and Sources: the same rows as md+'s inline sections, in a sheet. It comes
     before the Remove source alert on purpose: portals keep this order in the body, and the alert
     must land above the sheet it is opened from. -->
{#if compact}
  <BottomSheet.Root bind:open={settingsOpen} title="Download settings">
    {#snippet trailing()}
      <BottomSheet.Action prominent onclick={() => (settingsOpen = false)}>Done</BottomSheet.Action>
    {/snippet}
    <div class="flex flex-col gap-7 pb-2">
      {#if showAutoDownloadControl}
        <GroupedList.Section
          header="Downloads"
          footer={hasDownloadsNote ? downloadsFooter : undefined}
        >
          {@render downloadsRows('')}
          {#if showCheckAlbums}
            <!-- The More menu's "Check albums now", beside the switch it belongs to. -->
            <GroupedList.Row disabled={completionRunning} onclick={onRunCompletionNow}>
              <span class="text-body text-primary flex items-center gap-2">
                {#if completionRunning}<Loader2
                    class="size-4 animate-spin"
                    aria-hidden="true"
                  />{:else}<Disc3 class="size-4" aria-hidden="true" />{/if}
                Check albums now
              </span>
            </GroupedList.Row>
          {/if}
        </GroupedList.Section>
      {/if}
      {#if sources.length > 0}
        <GroupedList.Section header="Sources" footer={SOURCES_NOTE}>
          {@render sourcesRows('')}
        </GroupedList.Section>
      {/if}
    </div>
  </BottomSheet.Root>
{/if}

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
          removeSourceOpen = false;
          if (sourceToRemove) void onRemoveSource(sourceToRemove);
        }}
      >
        Remove source
      </AlertDialog.Action>
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>

{#snippet downloadsRows(rowClass: string)}
  <GroupedList.Row
    class={rowClass}
    label="Auto-download"
    sublabel="Newly liked tracks download in the background"
  >
    {#snippet trailing()}
      <Switch
        bind:checked={
          () => autoDownloadPending ?? autoDownload ?? false, () => void onToggleAutoDownload()
        }
        disabled={autoDownloadBusy}
        aria-label="Auto-download new tracks"
      />
    {/snippet}
  </GroupedList.Row>
  <GroupedList.Row
    class={rowClass}
    label="Complete albums"
    sublabel="Owning one track of an album queues the rest"
  >
    {#snippet trailing()}
      <Switch
        bind:checked={
          () => albumCompletionPending ?? albumCompletion ?? false,
          () => void onToggleAlbumCompletion()
        }
        disabled={albumCompletionBusy}
        aria-label="Complete albums automatically"
      />
    {/snippet}
  </GroupedList.Row>
{/snippet}

{#snippet downloadsNote()}
  {#if autoDownloadOff && !downloadingNow && !errorIsPermission}
    <p>
      Auto-download is off — pending tracks stay queued and won’t download on their own. Use
      Download now for a one-off sweep.
    </p>
  {/if}
  {#if albumCompletion}
    <!-- "Checking now…" is an expression so it keeps its leading space: Svelte trims the
         whitespace a block starts with. -->
    <p>
      Album completion runs every hour, a few albums at a time and always behind anything you asked
      for.{completionRunning ? ' Checking now…' : ''}
    </p>
  {/if}
{/snippet}

{#snippet downloadsFooter()}
  <div class="space-y-1">{@render downloadsNote()}</div>
{/snippet}

{#snippet sourcesRows(rowClass: string)}
  {#each sources as source (source.id)}
    {@const busy = busySources.has(source.id)}
    <GroupedList.Row
      class={rowClass}
      label={source.name}
      sublabel="{playlistProviderLabel(source.provider)}{source.sourceType === 'LikedSongs'
        ? ''
        : ' playlist'} · {source.itemCount.toLocaleString()} tracks"
    >
      {#if source.lastSyncedAtUtc}
        <span class="text-footnote text-muted-foreground md:text-xs">
          Synced {formatRelativeTime(source.lastSyncedAtUtc)}
        </span>
      {/if}
      {#snippet leading()}
        {#if source.imageUrl}
          <img
            src={source.imageUrl}
            alt=""
            class="size-10 shrink-0 rounded-sm object-cover"
            crossorigin="anonymous"
          />
        {:else}
          <span
            class="bg-muted text-muted-foreground flex size-10 items-center justify-center rounded-sm"
            aria-hidden="true"
          >
            {#if source.sourceType === 'LikedSongs'}<Heart
                class="size-5"
              />{:else if source.provider === 'youtube'}<ListVideo class="size-5" />{:else}<Music
                class="size-5"
              />{/if}
          </span>
        {/if}
      {/snippet}
      {#snippet trailing()}
        <span class="flex items-center gap-1">
          <Switch
            bind:checked={
              () => autoSyncPending[source.id] ?? source.autoSync,
              (on) => void onToggleAutoSync(source, on)
            }
            disabled={busy}
            aria-label="Auto-sync {source.name}"
          />
          <Button
            variant="ghost"
            size="icon"
            class="text-muted-foreground size-11 shrink-0 rounded-full md:size-8"
            aria-label="Remove {source.name}"
            title="Remove source"
            disabled={busy}
            onclick={() => askRemoveSource(source)}
          >
            <Trash2 />
          </Button>
        </span>
      {/snippet}
    </GroupedList.Row>
  {/each}
{/snippet}

{#snippet statusInline(item: WishlistItem)}
  {@const failed = item.status === 'Failed' || item.status === 'NotFound'}
  <span
    class="flex shrink-0 items-center gap-1 {failed
      ? 'text-destructive-text'
      : item.status === 'Downloaded'
        ? 'text-foreground'
        : 'text-muted-foreground'}"
  >
    {#if item.status === 'Downloaded' && item.downloadedSongId != null}
      <CircleCheck class="text-primary size-4" aria-hidden="true" />
    {:else if item.status === 'Downloading' || (item.status === 'Downloaded' && item.downloadedSongId == null)}
      <Loader2 class="size-3.5 animate-spin" aria-hidden="true" />
    {:else}
      <span class="size-2 shrink-0 rounded-full {statusDotClass(item.status)}" aria-hidden="true"
      ></span>
    {/if}
    {statusWord(item)}<span aria-hidden="true">&nbsp;·</span>
  </span>
{/snippet}

{#snippet itemRow(item: WishlistItem)}
  {@const failed = item.status === 'Failed' || item.status === 'NotFound'}
  {@const songId = item.downloadedSongId}
  {@const busy = busyItems.has(item.id)}
  <li class="group/item md:hover:bg-accent relative flex items-center gap-3 pl-4 md:px-7">
    <div
      class="bg-muted mt-2.5 size-11 shrink-0 self-start overflow-hidden rounded-sm md:mt-0 md:size-10 md:self-center"
    >
      {#if item.albumArt}
        <img
          src={item.albumArt}
          alt=""
          loading="lazy"
          class="size-full object-cover"
          crossorigin="anonymous"
        />
      {:else}
        <div class="flex size-full items-center justify-center">
          <Music class="text-muted-foreground size-4" aria-hidden="true" />
        </div>
      {/if}
    </div>
    <!-- The hairline hangs off the text column (an inset separator). -->
    <div
      class="after:bg-separator relative flex min-h-16 min-w-0 flex-1 items-center gap-3 self-stretch py-2.5 pr-4 after:absolute after:inset-x-0 after:bottom-0 after:h-(--hairline) group-last/item:after:hidden md:min-h-14 md:pr-0 md:after:hidden"
    >
      <div class="min-w-0 flex-1">
        {#if compact && songId != null}
          <!-- On a phone the row itself opens the song (the desktop's In library button): the
               title is a button stretched over the row; the disclosure and menu sit above it. -->
          <button
            type="button"
            class="text-body active:after:bg-accent block w-full truncate text-left outline-none after:absolute after:inset-0 focus-visible:underline"
            onclick={() => songDetail.open(songId)}
          >
            {item.title}
          </button>
        {:else}
          <div class="text-body truncate md:text-sm md:font-medium">{item.title}</div>
        {/if}
        <div
          class="text-subheadline text-muted-foreground flex min-w-0 items-center gap-1.5 md:block md:truncate md:text-xs"
        >
          {#if compact}
            <!-- A phone's status: glyph + word at the head of the second line (the title keeps the
                 row's width; the ⋯ menu is the only trailing item). -->
            {@render statusInline(item)}
          {/if}
          <span class="min-w-0 truncate"
            >{item.artist}{item.album ? ` · ${item.album}` : ''}{item.downloadProvider &&
            (item.status === 'Downloaded' || songId != null)
              ? ` · via ${item.downloadProvider}`
              : ''}{item.fallbackFromProvider && item.status === 'Downloaded'
              ? ` · ${item.fallbackFromProvider} was unavailable${item.libraryBuildStatus === 'Done' ? ', upgrade pending' : ''}`
              : ''}</span
          >
        </div>
        {#if item.lastError && failed}
          {@const why = describeDownloadError(item.lastError)}
          <p class="text-footnote mt-0.5 md:text-xs">
            <span class="text-destructive-text">{why.summary}</span>
            {#if why.next}<span class="text-muted-foreground"> {why.next}</span>{/if}
          </p>
          <!-- The raw worker output, readable on a phone (a title tooltip never shows there). -->
          <details class="text-footnote relative z-10 md:text-xs">
            <!-- 44pt tall on touch (it was 34): the raw error is one tap away, so that tap must land. -->
            <summary
              class="text-muted-foreground hover:text-foreground w-fit cursor-pointer py-1 select-none pointer-coarse:flex pointer-coarse:min-h-11 pointer-coarse:min-w-11 pointer-coarse:items-center pointer-coarse:py-0"
            >
              Details
            </summary>
            <pre
              class="bg-muted text-muted-foreground mt-0.5 mb-1 max-h-40 overflow-auto rounded-md p-2 font-mono text-[11px] leading-snug break-all whitespace-pre-wrap">{item.lastError}</pre>
          </details>
        {/if}
        {#if item.status === 'Failed'}
          <div class="text-footnote text-muted-foreground mt-0.5 truncate md:text-xs">
            {#if item.nextAttemptAtUtc}
              Retries automatically {formatRelativeFuture(
                item.nextAttemptAtUtc
              )}{item.attemptCount > 0
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

      {#if compact}
        <!-- The row's menu: Open in library · Retry · Remove. Top-aligned, beside the title and
             artist lines, so a failed row's error and Details below don't push it downwards. -->
        <DropdownMenu.Root>
          <DropdownMenu.Trigger>
            {#snippet child({ props })}
              <Button
                {...props}
                variant="ghost"
                size="icon"
                class="text-muted-foreground relative z-10 -ml-1 size-11 shrink-0 self-start rounded-full"
                aria-label="More for {item.title}"
                disabled={busy}
              >
                {#if busy}<Loader2 class="animate-spin" />{:else}<Ellipsis />{/if}
              </Button>
            {/snippet}
          </DropdownMenu.Trigger>
          <DropdownMenu.Content align="end" class="min-w-52">
            {#if songId != null}
              <DropdownMenu.Item onSelect={() => songDetail.open(songId)}>
                <CircleCheck />
                {item.libraryBuildStatus === 'Done'
                  ? 'Open in library'
                  : `Open (${item.libraryEnrichmentStatus ?? 'processing'})`}
              </DropdownMenu.Item>
            {/if}
            {#if failed}
              <DropdownMenu.Item onSelect={() => onRetry(item)}
                ><RotateCw /> Retry</DropdownMenu.Item
              >
            {/if}
            {#if songId != null || failed}<DropdownMenu.Separator />{/if}
            <DropdownMenu.Item variant="destructive" onSelect={() => onRemoveItem(item)}>
              <Trash2 /> Remove
            </DropdownMenu.Item>
          </DropdownMenu.Content>
        </DropdownMenu.Root>
      {:else}
        {#if songId != null}
          <Button
            variant={item.libraryBuildStatus === 'Done' ? 'tinted' : 'outline'}
            size="sm"
            class="h-8 shrink-0 gap-1 px-2.5 text-xs font-medium"
            title="Open this song in your library"
            onclick={() => songDetail.open(songId)}
          >
            {#if item.libraryBuildStatus === 'Done'}
              <CircleCheck class="size-3.5 shrink-0" />
              In library
            {:else}
              {item.libraryEnrichmentStatus ?? 'Processing'}
            {/if}
          </Button>
        {/if}

        <span class="text-muted-foreground w-12 shrink-0 text-right text-xs tabular-nums">
          {fmtDuration(item.durationMs)}
        </span>

        <Badge
          class="{statusBadgeClass(item.status)} gap-1.5"
          title={item.status === 'Downloaded' && songId == null
            ? 'Downloaded — being added to your library'
            : undefined}
        >
          <span class="size-1.5 shrink-0 rounded-full {statusDotClass(item.status)}"></span>
          {statusWord(item)}
        </Badge>

        {#if failed}
          <Button
            variant="ghost"
            size="icon"
            class="shrink-0"
            aria-label="Retry {item.title}"
            title="Retry"
            disabled={busy}
            onclick={() => onRetry(item)}
          >
            <RotateCw class="size-4" />
          </Button>
        {/if}
        <Button
          variant="ghost"
          size="icon"
          class="shrink-0"
          aria-label="Remove {item.title}"
          title="Remove"
          disabled={busy}
          onclick={() => onRemoveItem(item)}
        >
          <Trash2 class="size-4" />
        </Button>
      {/if}
    </div>
  </li>
{/snippet}
