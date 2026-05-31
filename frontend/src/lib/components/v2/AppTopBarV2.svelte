<script lang="ts">
  import { page } from '$app/state';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import * as Breadcrumb from '$lib/components/ui/breadcrumb';
  import { Separator } from '$lib/components/ui/separator';
  import ThemeToggle from '$lib/components/ThemeToggle.svelte';
  import DesignVersionToggle from '$lib/components/v2/DesignVersionToggle.svelte';

  // Resolve the current v1 route into the v2 "Section › Page" breadcrumb. Phase 0
  // still routes through the existing v1 pages, so the mapping is keyed on the
  // real pathname; later phases (/pipeline, /inbox, /track/[id]) extend this.
  type Crumb = { section: string; page: string };

  const pathname = $derived(page.url.pathname);
  const isSourceView = $derived(page.url.searchParams.get('view') === 'source');
  const inboxTab = $derived(page.url.searchParams.get('tab') ?? 'review');

  const crumb = $derived.by<Crumb>(() => {
    if (pathname === '/pipeline') return { section: 'Pipeline', page: 'Conveyor' };
    if (pathname === '/runs' || pathname.startsWith('/runs/')) {
      return { section: 'Pipeline', page: 'Ingest history' };
    }
    if (pathname.startsWith('/directories')) return { section: 'Pipeline', page: 'By folder' };
    if (pathname.startsWith('/quality')) return { section: 'Pipeline', page: 'AI quality' };
    if (pathname.startsWith('/inbox')) {
      const label =
        inboxTab === 'dupes' ? 'Duplicates' : inboxTab === 'ai' ? 'AI flagged' : 'Tag review';
      return { section: 'Inbox', page: label };
    }
    if (pathname.startsWith('/review')) return { section: 'Inbox', page: 'Tag review' };
    if (pathname.startsWith('/track/')) return { section: 'Track', page: 'Provenance' };
    if (pathname.startsWith('/artists')) return { section: 'Library', page: 'Artists' };
    if (pathname === '/tracks') return { section: 'Library', page: 'All tracks' };
    if (pathname === '/library' || pathname.startsWith('/library/')) {
      return { section: 'Library', page: isSourceView ? 'Source folder' : 'Albums' };
    }
    if (pathname.startsWith('/years')) return { section: 'Library', page: 'By year' };
    if (pathname.startsWith('/spotify')) return { section: 'Pipeline', page: 'Spotify' };
    if (pathname.startsWith('/settings')) return { section: 'Settings', page: 'Account' };
    if (pathname.startsWith('/overview')) return { section: 'Pipeline', page: 'Overview' };
    return { section: 'MusicHoarder', page: '' };
  });
</script>

<header
  class="border-border bg-background flex h-12 shrink-0 items-center gap-2.5 border-b px-3.5"
>
  <Sidebar.Trigger class="-ml-1 size-7" />
  <Separator orientation="vertical" class="h-4" />
  <Breadcrumb.Root class="min-w-0">
    <Breadcrumb.List class="gap-1.5 sm:gap-2">
      <Breadcrumb.Item>
        <span class="text-muted-foreground text-[13px] whitespace-nowrap">{crumb.section}</span>
      </Breadcrumb.Item>
      {#if crumb.page}
        <Breadcrumb.Separator />
        <Breadcrumb.Item>
          <Breadcrumb.Page class="text-foreground text-[13px] whitespace-nowrap">
            {crumb.page}
          </Breadcrumb.Page>
        </Breadcrumb.Item>
      {/if}
    </Breadcrumb.List>
  </Breadcrumb.Root>

  <div class="ml-auto flex items-center gap-1.5">
    <DesignVersionToggle />
    <ThemeToggle />
  </div>
</header>
