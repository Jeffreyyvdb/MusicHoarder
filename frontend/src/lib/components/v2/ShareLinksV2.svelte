<script lang="ts">
  import { Link2, RefreshCw, TriangleAlert } from '@lucide/svelte';
  import { page } from '$app/state';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import ShareLinkDetail from '$lib/components/shares/ShareLinkDetail.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { Button } from '$lib/components/ui/button';
  import { EmptyState } from '$lib/components/ui/empty-state';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import {
    ApiError,
    fetchShareStatsList,
    getSongCoverUrl,
    type ShareStatsRow
  } from '$lib/api-client';
  import { formatRelativeTime } from '$lib/formatters';
  import { replaceUrl } from '$lib/navigation/replace-url';
  import {
    countLabel,
    isRevoked,
    shareDetailId,
    shareKindLine,
    shareListHref,
    shareListView,
    shareTitle,
    type ShareListView
  } from '$lib/share-stats';
  import { cn } from '$lib/utils';

  // Every public link made with "Share link…", with how often each was opened — the page that
  // answers "did anyone click the link I posted?", and the one place a link can be revoked.
  //
  // A drill-down at every width, like a Settings list: the rows push /shares?link=<id>, whose nav
  // bar goes Back to the list it came from. Active and Revoked are two lists behind one segmented
  // control (?view=revoked), so a revoked link's numbers stay findable after it stops working.

  const detailId = $derived(shareDetailId(page.url));
  const view = $derived(shareListView(page.url));

  let rows = $state<ShareStatsRow[]>([]);
  let loading = $state(true);
  let loaded = $state(false);
  let error = $state<string | null>(null);
  /** The endpoint is admin-only while the nav deliberately shows this page to the demo account. */
  let forbidden = $state(false);

  async function load() {
    loading = true;
    try {
      rows = await fetchShareStatsList();
      error = null;
      forbidden = false;
    } catch (e) {
      if (e instanceof ApiError && e.status === 403) {
        forbidden = true;
        rows = [];
        error = null;
      } else {
        error = e instanceof Error ? e.message : 'Failed to load share links';
      }
    } finally {
      loading = false;
      loaded = true;
    }
  }

  $effect(() => {
    void load();
  });

  const active = $derived(rows.filter((r) => !isRevoked(r)));
  const revoked = $derived(rows.filter(isRevoked));
  const shown = $derived(view === 'revoked' ? revoked : active);
  const activeOpens = $derived(active.reduce((sum, r) => sum + r.views, 0));

  const headerMeta = $derived.by(() => {
    if (!loaded || forbidden || error) return undefined;
    if (view === 'revoked') return countLabel(revoked.length, 'revoked link', 'revoked links');
    return `${countLabel(active.length, 'active link', 'active links')} · ${countLabel(activeOpens, 'open', 'opens')}`;
  });

  const tabs = $derived([
    { id: 'active', label: 'Active', count: loaded ? active.length : null },
    { id: 'revoked', label: 'Revoked', count: loaded ? revoked.length : null }
  ]);

  function selectView(id: string) {
    void replaceUrl(shareListHref(id as ShareListView));
  }

  function rowHref(row: ShareStatsRow): string {
    const params = new URLSearchParams({ link: String(row.id) });
    if (view === 'revoked') params.set('view', 'revoked');
    return `/shares?${params}`;
  }

  function rowSublabel(row: ShareStatsRow): string {
    const kind = shareKindLine(row);
    if (row.revokedAtUtc) return `${kind} · revoked ${formatRelativeTime(row.revokedAtUtc)}`;
    if (row.lastViewedAtUtc) return `${kind} · opened ${formatRelativeTime(row.lastViewedAtUtc)}`;
    return `${kind} · shared ${formatRelativeTime(row.createdAtUtc)}`;
  }
</script>

{#snippet message(title: string, body?: string)}
  <div class="mx-auto max-w-md px-6 py-14 text-center">
    <p class="text-headline md:text-sm md:font-medium">{title}</p>
    {#if body}
      <p class="text-callout text-muted-foreground mt-1 md:text-sm">{body}</p>
    {/if}
  </div>
{/snippet}

<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  {#if detailId != null}
    {#key detailId}
      <ScrollArea class="min-h-0 flex-1">
        <ShareLinkDetail
          id={detailId}
          back={{ label: 'Share links', href: shareListHref(view) }}
          onchange={load}
        />
      </ScrollArea>
    {/key}
  {:else}
    <ScrollArea class="min-h-0 flex-1">
      <PageToolbarV2
        title="Share links"
        meta={headerMeta}
        tabs={forbidden ? undefined : tabs}
        activeTab={view}
        onselectTab={selectView}
        grouped
      >
        {#snippet actions()}
          <Button variant="gray" class="rounded-full" onclick={load} disabled={loading}>
            <RefreshCw class={cn(loading && 'animate-spin')} aria-hidden="true" />
            <span class="max-md:sr-only">Refresh</span>
          </Button>
        {/snippet}
      </PageToolbarV2>

      <div class="mx-auto flex w-full max-w-3xl flex-col gap-7 pt-2 pb-8 md:gap-6 md:px-7 md:pt-6">
        {#if forbidden}
          {@render message(
            'Share links are for administrators',
            'This page lists the public links an administrator has made to songs and albums, and how often each was opened.'
          )}
        {:else if error}
          <GroupedList.Section>
            <GroupedList.Row
              icon={TriangleAlert}
              iconClass="bg-destructive/12 text-destructive-text"
              label="Couldn't load share links"
              sublabel={error}
            >
              {#snippet trailing()}
                <Button
                  variant="ghost"
                  class="text-primary hover:text-primary h-11 md:h-8"
                  onclick={load}
                >
                  Retry
                </Button>
              {/snippet}
            </GroupedList.Row>
          </GroupedList.Section>
        {:else if !loaded}
          <GroupedList.Section>
            {#each Array(4) as _, i (i)}
              <div class="flex items-center gap-3 px-4 py-2.5">
                <Skeleton class="size-11 shrink-0 rounded-md" />
                <div class="min-w-0 flex-1 space-y-1.5">
                  <Skeleton class="h-4 w-1/2" />
                  <Skeleton class="h-3 w-2/3" />
                </div>
                <Skeleton class="h-4 w-14 shrink-0" />
              </div>
            {/each}
          </GroupedList.Section>
        {:else if shown.length === 0}
          {#if view === 'revoked'}
            <EmptyState
              icon={Link2}
              title="No revoked links"
              hint="A link you revoke stops working, but its numbers stay listed here."
            />
          {:else}
            <EmptyState
              icon={Link2}
              title="No share links yet"
              hint="Pick Share link… on a song or an album to make one. This page counts how often each is opened and played."
            />
          {/if}
        {:else}
          <GroupedList.Section
            footer={view === 'active'
              ? 'An open is one visit to the link’s page: link previews, bots and your own visits aren’t counted, and a reload within 30 minutes counts once.'
              : undefined}
          >
            {#each shown as row (row.id)}
              <GroupedList.Row
                href={rowHref(row)}
                label={shareTitle(row)}
                sublabel={rowSublabel(row)}
                value={countLabel(row.views, 'open', 'opens')}
                chevron
              >
                {#snippet leading()}
                  <Cover
                    artist={row.artist ?? ''}
                    title={shareTitle(row)}
                    coverUrl={getSongCoverUrl(row.songId)}
                    size={44}
                    corner={6}
                    dprCap={3}
                    caption={false}
                  />
                {/snippet}
              </GroupedList.Row>
            {/each}
          </GroupedList.Section>
        {/if}
      </div>
    </ScrollArea>
  {/if}
</div>
