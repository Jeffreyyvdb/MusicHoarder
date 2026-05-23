<script lang="ts">
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import {
    ChevronLeft,
    ChevronRight,
    Columns,
    Disc3,
    FolderOpen,
    Grid3x3,
    List,
    Loader2,
    PackageCheck,
    ScanLine,
    Search,
    Settings as SettingsIcon
  } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import * as Tooltip from '$lib/components/ui/tooltip';
  import { Input } from '$lib/components/ui/input';
  import { Separator } from '$lib/components/ui/separator';
  import ThemeToggle from '$lib/components/ThemeToggle.svelte';
  import {
    fetchLibraryAvailability,
    fetchOverview,
    triggerBuild,
    triggerEnrichmentScan,
    triggerFingerprint,
    type ApiOverview,
    type EnrichmentTriggerResult,
    type LibraryAvailability
  } from '$lib/api-client';
  import { breadcrumbStore } from '$lib/stores/breadcrumbs.svelte';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { cn } from '$lib/utils';

  type LibraryLayout = 'grid' | 'list' | 'col';

  const pathname = $derived(page.url.pathname);
  const onApp = $derived(pathname === '/library' || pathname.startsWith('/library/'));
  const onAlbumsRoot = $derived(pathname === '/library');
  const albumKey = $derived(page.url.searchParams.get('album'));
  const section = $derived(page.url.searchParams.get('section'));
  const onRuns = $derived(pathname.startsWith('/runs'));

  const SECTION_LABEL: Record<string, string> = {
    lib: 'Library',
    recent: 'Recently Added',
    dupes: 'Duplicates',
    missing: 'Missing metadata',
    queue: 'Import Queue'
  };

  const crumbs = $derived.by(() => {
    if (onApp) {
      const album = breadcrumbStore.currentAlbum;
      if (albumKey && album) {
        return [
          { label: 'Library', href: '/library' },
          { label: album.artist, href: '/library' },
          { label: album.title, href: null }
        ];
      }
      if (albumKey) {
        const [artistL, titleL] = decodeURIComponent(albumKey).split('::');
        return [
          { label: 'Library', href: '/library' },
          { label: artistL ?? '', href: '/library' },
          { label: titleL ?? '', href: null }
        ];
      }
      if (section && SECTION_LABEL[section]) {
        return [
          { label: 'Library', href: '/library' },
          { label: SECTION_LABEL[section], href: null }
        ];
      }
      return [
        { label: 'Library', href: '/library' },
        { label: 'All albums', href: null }
      ];
    }
    if (onRuns) return [{ label: 'MusicHoarder', href: '/library' }, { label: 'Runs', href: null }];
    if (pathname.startsWith('/artists')) return [{ label: 'MusicHoarder', href: '/library' }, { label: 'Artists', href: null }];
    if (pathname.startsWith('/spotify')) return [{ label: 'MusicHoarder', href: '/library' }, { label: 'Spotify', href: null }];
    if (pathname.startsWith('/review')) return [{ label: 'MusicHoarder', href: '/library' }, { label: 'Review', href: null }];
    if (pathname.startsWith('/settings')) return [{ label: 'MusicHoarder', href: '/library' }, { label: 'Settings', href: null }];
    return [{ label: 'MusicHoarder', href: '/library' }];
  });

  let searchValue = $state(page.url.searchParams.get('q') ?? '');
  let searchTimer: ReturnType<typeof setTimeout> | null = null;

  $effect(() => {
    // Re-sync the input when the URL changes from elsewhere.
    const q = page.url.searchParams.get('q') ?? '';
    if (q !== searchValue && document.activeElement?.tagName !== 'INPUT') {
      searchValue = q;
    }
  });

  function commitSearch(value: string) {
    if (!onAlbumsRoot) return;
    const url = new URL(page.url);
    if (value.trim()) url.searchParams.set('q', value);
    else url.searchParams.delete('q');
    void goto(url.pathname + url.search, { replaceState: true, keepFocus: true, noScroll: true });
  }

  function onSearchInput(e: Event) {
    const v = (e.currentTarget as HTMLInputElement).value;
    searchValue = v;
    if (searchTimer) clearTimeout(searchTimer);
    searchTimer = setTimeout(() => commitSearch(v), 180);
  }

  function getStoredLayout(): LibraryLayout {
    if (typeof window === 'undefined') return 'grid';
    const v = localStorage.getItem('musichoarder-library-view');
    if (v === 'list' || v === 'col' || v === 'grid') return v;
    return 'grid';
  }

  let layout = $state<LibraryLayout>('grid');
  let hydrated = $state(false);

  $effect(() => {
    layout = getStoredLayout();
    hydrated = true;
  });

  function setLayout(next: LibraryLayout) {
    if (next === 'col') return; // Coming soon
    layout = next;
    if (hydrated && typeof window !== 'undefined') {
      localStorage.setItem('musichoarder-library-view', next);
    }
    window.dispatchEvent(new CustomEvent('mh:layout-change', { detail: next }));
  }

  function handleBack() {
    if (typeof window !== 'undefined' && window.history.length > 1) {
      history.back();
    } else {
      void goto('/library');
    }
  }

  let overview = $state<ApiOverview | null>(null);
  $effect(() => {
    let cancelled = false;
    void fetchOverview().then((v) => {
      if (!cancelled) overview = v;
    });
    return () => {
      cancelled = true;
    };
  });
  const queueRemaining = $derived(
    overview?.job
      ? Math.max(0, (overview.job.tracksDiscovered ?? 0) - (overview.job.tracksProcessed ?? 0))
      : 0
  );
  // Prefer SSE-driven liveness from the pipeline-overlay store (per-second),
  // falling back to the polled overview when the SSE hasn't reported yet.
  const indexing = $derived(pipelineOverlay.isAnyRunning || overview?.job?.status === 'running');
  const pipelineDrawerOpen = $derived(pipelineOverlay.isOpen);

  const showViewToggle = $derived(onAlbumsRoot && !albumKey);

  // Manual pipeline controls. With MusicEnricher__AutoStartPipeline=false
  // (e.g. preview environments) stages no longer cascade, so the manual flow
  // is Scan → Fingerprint → Build (enrichment rides off the fingerprint feed).
  // One job runs at a time, so a single in-flight marker is enough.
  type PipelineStage = 'scan' | 'fingerprint' | 'build';
  type StageDef = {
    id: PipelineStage;
    label: string;
    icon: typeof ScanLine;
    trigger: () => Promise<EnrichmentTriggerResult>;
    // Which directories must be online for the backend to accept this stage.
    needsSource: boolean;
    needsDestination: boolean;
  };
  const STAGES: StageDef[] = [
    { id: 'scan', label: 'Scan', icon: ScanLine, trigger: triggerEnrichmentScan, needsSource: true, needsDestination: false },
    { id: 'fingerprint', label: 'Fingerprint', icon: Disc3, trigger: triggerFingerprint, needsSource: true, needsDestination: false },
    { id: 'build', label: 'Build', icon: PackageCheck, trigger: triggerBuild, needsSource: true, needsDestination: true }
  ];

  // Polled directory availability (mirrors LibraryOfflineBanner). null = unknown
  // (e.g. API unreachable) — treated as available so we never block on a failed probe.
  let availability = $state<LibraryAvailability | null>(null);
  $effect(() => {
    let cancelled = false;
    const refresh = async () => {
      try {
        const a = await fetchLibraryAvailability();
        if (!cancelled) availability = a;
      } catch {
        if (!cancelled) availability = null;
      }
    };
    void refresh();
    const handle = setInterval(() => void refresh(), 15_000);
    return () => {
      cancelled = true;
      clearInterval(handle);
    };
  });

  function unavailableReason(stage: StageDef): string | null {
    if (!availability) return null;
    const missing: string[] = [];
    if (stage.needsSource && !availability.sourceAvailable) missing.push('source');
    if (stage.needsDestination && !availability.destinationAvailable) missing.push('destination');
    if (missing.length === 0) return null;
    return `${missing.join(' and ')} directory offline`;
  }

  let runningStage = $state<PipelineStage | null>(null);
  async function runStage(stage: (typeof STAGES)[number]) {
    if (runningStage) return;
    runningStage = stage.id;
    try {
      const result = await stage.trigger();
      if (result.ok) {
        toast.success(`${stage.label} started`);
        // Surface live progress as the pipeline picks up the new rows.
        pipelineOverlay.setOpen(true);
      } else {
        toast.error(`Could not start ${stage.label.toLowerCase()}`, { description: result.message });
      }
    } catch {
      toast.error(`Could not start ${stage.label.toLowerCase()}`, {
        description: 'The API may be unavailable.'
      });
    } finally {
      runningStage = null;
    }
  }
</script>

<header
  class="border-border bg-background/80 sticky top-0 z-30 flex h-12 shrink-0 items-center gap-2 border-b px-3 backdrop-blur md:px-4"
>
  <Sidebar.Trigger class="-ml-1" />
  <Separator orientation="vertical" class="mx-1 h-5" />

  <div class="flex items-center gap-1">
    <button
      type="button"
      onclick={handleBack}
      title="Back"
      class="text-foreground/70 hover:bg-accent hover:text-foreground grid size-7 place-items-center rounded-md transition-colors"
    >
      <ChevronLeft class="size-3.5" />
    </button>
    <button
      type="button"
      title="Forward"
      disabled
      class="text-muted-foreground/40 grid size-7 place-items-center rounded-md"
    >
      <ChevronRight class="size-3.5" />
    </button>
  </div>

  <nav class="ml-1 hidden min-w-0 items-center gap-1 sm:flex" aria-label="Breadcrumb">
    {#each crumbs as crumb, i (i + '-' + crumb.label)}
      {#if i > 0}
        <ChevronRight class="text-muted-foreground/60 size-2.5 shrink-0" />
      {/if}
      {#if crumb.href}
        <a
          href={crumb.href}
          class="text-muted-foreground hover:bg-accent hover:text-foreground flex shrink-0 items-center gap-1 truncate rounded px-1.5 py-0.5 text-xs transition-colors"
        >
          {#if i === 0}<FolderOpen class="size-3" />{/if}
          <span class="truncate">{crumb.label}</span>
        </a>
      {:else}
        <span
          class="text-foreground flex shrink-0 items-center gap-1 truncate rounded px-1.5 py-0.5 text-xs font-medium"
        >
          <span class="max-w-[200px] truncate">{crumb.label}</span>
        </span>
      {/if}
    {/each}
  </nav>

  <div class="mx-auto flex w-full max-w-[460px] min-w-0 flex-1 items-center px-2">
    <div
      class="bg-surface-sunken border-border focus-within:border-primary focus-within:ring-primary/20 flex w-full items-center gap-2 rounded-md border px-2.5 py-1.5 transition-shadow focus-within:ring-2"
    >
      <Search class="text-muted-foreground size-3.5 shrink-0" />
      <Input
        type="search"
        placeholder={onAlbumsRoot ? 'Search albums, artists…' : 'Search'}
        value={searchValue}
        oninput={onSearchInput}
        disabled={!onAlbumsRoot}
        class="h-auto flex-1 border-0 bg-transparent p-0 text-xs shadow-none focus-visible:ring-0"
        aria-label="Search library"
      />
      <span class="text-muted-foreground bg-background hidden rounded border px-1.5 py-0.5 font-mono text-[10px] sm:inline">
        ⌘K
      </span>
    </div>
  </div>

  <div class="flex items-center gap-1.5">
    {#if showViewToggle}
      <div class="bg-surface-sunken border-border flex items-center rounded-md border p-0.5">
        {#each [{ id: 'grid' as const, label: 'Gallery', icon: Grid3x3 }, { id: 'list' as const, label: 'List', icon: List }, { id: 'col' as const, label: 'Column (coming soon)', icon: Columns }] as opt (opt.id)}
          {@const disabled = opt.id === 'col'}
          {@const isActive = layout === opt.id}
          <Tooltip.Provider delayDuration={300}>
            <Tooltip.Root>
              <Tooltip.Trigger>
                {#snippet child({ props })}
                  <button
                    {...props}
                    type="button"
                    onclick={() => setLayout(opt.id)}
                    {disabled}
                    class={cn(
                      'grid size-7 place-items-center rounded text-xs transition-colors',
                      isActive
                        ? 'bg-background text-foreground shadow-sm'
                        : 'text-muted-foreground hover:text-foreground',
                      disabled && 'cursor-not-allowed opacity-50'
                    )}
                    aria-label={opt.label}
                  >
                    <opt.icon class="size-3.5" />
                  </button>
                {/snippet}
              </Tooltip.Trigger>
              <Tooltip.Content>{opt.label}</Tooltip.Content>
            </Tooltip.Root>
          </Tooltip.Provider>
        {/each}
      </div>
    {/if}

    <div class="bg-surface-sunken border-border flex items-center rounded-md border p-0.5">
      {#each STAGES as stage (stage.id)}
        {@const busy = runningStage === stage.id}
        {@const reason = unavailableReason(stage)}
        {@const disabled = runningStage !== null || reason !== null}
        <Tooltip.Provider delayDuration={300}>
          <Tooltip.Root>
            <Tooltip.Trigger>
              {#snippet child({ props })}
                <button
                  {...props}
                  type="button"
                  onclick={() => runStage(stage)}
                  {disabled}
                  class={cn(
                    'text-muted-foreground hover:text-foreground flex items-center gap-1.5 rounded px-2 py-1 text-[11px] transition-colors',
                    disabled && !busy && 'cursor-not-allowed opacity-50'
                  )}
                  aria-label={stage.label}
                >
                  {#if busy}
                    <Loader2 class="size-3.5 animate-spin" />
                  {:else}
                    <stage.icon class="size-3.5" />
                  {/if}
                  <span class="hidden lg:inline">{stage.label}</span>
                </button>
              {/snippet}
            </Tooltip.Trigger>
            <Tooltip.Content>{reason ? `${stage.label} — ${reason}` : stage.label}</Tooltip.Content>
          </Tooltip.Root>
        </Tooltip.Provider>
      {/each}
    </div>

    <button
      type="button"
      onclick={() => pipelineOverlay.toggle()}
      aria-pressed={pipelineDrawerOpen}
      title={pipelineDrawerOpen ? 'Hide pipeline' : 'Show pipeline'}
      class={cn(
        'flex items-center gap-1.5 rounded-full border px-2.5 py-1 text-[11px] transition-colors',
        pipelineDrawerOpen
          ? 'bg-primary/15 text-primary border-transparent'
          : 'bg-surface-sunken border-border text-muted-foreground hover:text-foreground'
      )}
    >
      <span class={cn('size-1.5 rounded-full', indexing ? 'bg-primary mh-toolbar-pulse' : 'bg-muted-foreground/50')}></span>
      <span class="hidden sm:inline">Pipeline</span>
      <span class="text-muted-foreground/80 font-mono text-[10px]">{queueRemaining.toLocaleString()}</span>
    </button>

    <a
      href="/settings"
      class="text-muted-foreground hover:bg-accent hover:text-foreground grid size-8 place-items-center rounded-md transition-colors"
      aria-label="Settings"
      title="Settings"
    >
      <SettingsIcon class="size-4" />
    </a>

    <ThemeToggle />
  </div>
</header>

<style>
  :global(.mh-toolbar-pulse) {
    box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0.5);
    animation: mh-toolbar-pulse 2s infinite;
  }
  @keyframes mh-toolbar-pulse {
    0% {
      box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0.5);
    }
    70% {
      box-shadow: 0 0 0 6px oklch(0.5 0.17 145 / 0);
    }
    100% {
      box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0);
    }
  }
</style>
