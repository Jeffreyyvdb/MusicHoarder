<script lang="ts">
  import { Copy, ExternalLink, Link2Off, RefreshCw, Share, TriangleAlert } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import ShareOpensChart from '$lib/components/shares/ShareOpensChart.svelte';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { Button } from '$lib/components/ui/button';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import { SegmentedControl } from '$lib/components/ui/segmented-control';
  import {
    ApiError,
    fetchShareStats,
    getSongCoverUrl,
    revokeSongShare,
    shareUrl,
    type ShareStatsDetail
  } from '$lib/api-client';
  import { formatRelativeTime } from '$lib/formatters';
  import { resetShareLinkCache } from '$lib/share-links';
  import {
    countLabel,
    isRevoked,
    shareKindLine,
    shareTitle,
    sourceLabel,
    sumDaily
  } from '$lib/share-stats';
  import type { NavBack } from '$lib/nav';
  import { cn } from '$lib/utils';

  // One share link, pushed from the Share links list: how many people opened it and played it,
  // when, and from where — then the link itself (copy, share, open) and, last and alone, Revoke.

  type Props = {
    id: number;
    back: NavBack;
    /** The link changed (revoked): the list behind it should reload. */
    onchange?: () => void;
  };
  const { id, back, onchange }: Props = $props();

  type Range = '7' | '30' | '90';
  const RANGES: { value: Range; label: string }[] = [
    { value: '7', label: '7 days' },
    { value: '30', label: '30 days' },
    { value: '90', label: '90 days' }
  ];

  let range = $state<Range>('30');
  let detail = $state<ShareStatsDetail | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let notFound = $state(false);
  let reqSeq = 0;

  async function load() {
    const seq = ++reqSeq;
    loading = true;
    try {
      const result = await fetchShareStats(id, Number(range));
      if (seq !== reqSeq) return;
      detail = result;
      error = null;
      notFound = false;
    } catch (e) {
      if (seq !== reqSeq) return;
      notFound = e instanceof ApiError && e.status === 404;
      error = notFound ? null : e instanceof Error ? e.message : 'Failed to load this link';
    } finally {
      if (seq === reqSeq) loading = false;
    }
  }

  $effect(() => {
    void id;
    void range;
    void load();
  });

  const share = $derived(detail?.share ?? null);
  const title = $derived(share ? shareTitle(share) : 'Share link');
  const revoked = $derived(share ? isRevoked(share) : false);
  const url = $derived(share ? shareUrl(share.token) : '');
  const windowTotals = $derived(detail ? sumDaily(detail.daily) : { views: 0, plays: 0 });
  const sourceMax = $derived(Math.max(1, ...(detail?.sources ?? []).map((s) => s.views)));
  const trackMax = $derived(Math.max(1, ...(detail?.tracks ?? []).map((t) => t.plays)));
  const canShareSheet = $derived(
    typeof navigator !== 'undefined' && typeof navigator.share === 'function'
  );

  let heroTitleEl = $state<HTMLElement | null>(null);

  function fmtDate(iso: string): string {
    return new Date(iso).toLocaleDateString([], {
      day: 'numeric',
      month: 'short',
      year: 'numeric'
    });
  }

  const heroMeta = $derived.by(() => {
    if (!share) return '';
    const made = `Created ${fmtDate(share.createdAtUtc)}`;
    return share.revokedAtUtc ? `${made} · Revoked ${fmtDate(share.revokedAtUtc)}` : made;
  });

  const chartFooter = $derived.by(() => {
    if (!detail || detail.daily.length === 0) return undefined;
    const days = detail.daily.length;
    const span = days < Number(range) ? `since the link was made` : `in the last ${range} days`;
    return `${countLabel(windowTotals.views, 'open', 'opens')} and ${countLabel(windowTotals.plays, 'play', 'plays')} ${span}.`;
  });

  async function copyLink() {
    try {
      await navigator.clipboard.writeText(url);
      toast.success('Link copied');
    } catch {
      toast.info('Copy the link', { description: url, duration: 12000 });
    }
  }

  function openShareSheet() {
    if (!share) return;
    void navigator.share({ url, title }).catch(() => {});
  }

  let confirmRevoke = $state(false);
  let revoking = $state(false);

  async function revoke() {
    if (!share) return;
    revoking = true;
    try {
      await revokeSongShare(share.id);
      // The row menus look an existing link up before offering it; they must not find this one.
      resetShareLinkCache();
      toast.success('Link revoked', {
        description: 'Anyone who opens it now sees that it is gone.'
      });
      onchange?.();
      await load();
    } catch (e) {
      toast.error(e instanceof Error ? e.message : 'Could not revoke the link');
    } finally {
      revoking = false;
    }
  }
</script>

{#snippet bar(pct: number)}
  <span class="bg-muted mt-1.5 block h-1 overflow-hidden rounded-full" aria-hidden="true">
    <span
      class="bg-primary block h-full rounded-full"
      style="width: {Math.min(100, Math.max(0, pct))}%"
    ></span>
  </span>
{/snippet}

{#snippet barRow(label: string, value: string, pct: number)}
  <GroupedList.Row>
    <span class="flex items-baseline justify-between gap-3">
      <span class="text-body min-w-0 truncate md:text-sm">{label}</span>
      <span class="text-body text-muted-foreground shrink-0 tabular-nums md:text-sm">{value}</span>
    </span>
    {@render bar(pct)}
  </GroupedList.Row>
{/snippet}

<PageToolbarV2 {title} {back} largeTitle={false} collapseAfter={heroTitleEl} grouped>
  {#snippet actions()}
    <Button variant="ghost" size="icon" aria-label="Refresh" onclick={load} disabled={loading}>
      <RefreshCw class={cn(loading && 'animate-spin')} aria-hidden="true" />
    </Button>
  {/snippet}
</PageToolbarV2>

<div class="mx-auto flex w-full max-w-3xl flex-col gap-7 pt-2 pb-8 md:gap-6 md:px-7 md:pt-6">
  {#if notFound}
    <div class="mx-auto max-w-md px-6 py-14 text-center">
      <p class="text-headline md:text-sm md:font-medium">This link isn't here</p>
      <p class="text-callout text-muted-foreground mt-1 md:text-sm">
        It may belong to another account. Go back to see the links you made.
      </p>
    </div>
  {:else if error && !detail}
    <GroupedList.Section>
      <GroupedList.Row
        icon={TriangleAlert}
        iconClass="bg-destructive/12 text-destructive-text"
        label="Couldn't load this link"
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
  {:else if !detail || !share}
    <div class="flex items-center gap-4 px-4 md:px-0">
      <Skeleton class="size-20 shrink-0 rounded-[10px]" />
      <div class="min-w-0 flex-1 space-y-2">
        <Skeleton class="h-5 w-2/3" />
        <Skeleton class="h-4 w-1/3" />
      </div>
    </div>
    <GroupedList.Section>
      <div class="p-4"><Skeleton class="h-32 w-full" /></div>
    </GroupedList.Section>
  {:else}
    <!-- Hero: what the link plays, and when it was made (and killed). -->
    <div class="flex items-center gap-4 px-4 md:px-0">
      <Cover
        artist={share.artist ?? ''}
        {title}
        coverUrl={getSongCoverUrl(share.songId)}
        size={80}
        corner={10}
        dprCap={3}
        caption={false}
        class="shrink-0"
      />
      <div class="min-w-0 flex-1">
        <h2 bind:this={heroTitleEl} class="text-title-3 text-balance md:text-xl md:font-semibold">
          {title}
        </h2>
        <p class="text-subheadline text-muted-foreground md:text-sm">{shareKindLine(share)}</p>
        <p class="text-footnote text-muted-foreground mt-0.5 md:text-xs">{heroMeta}</p>
      </div>
    </div>

    <!-- The three figures, all time. -->
    <GroupedList.Section
      headingLevel={2}
      header="All time"
      footer={share.lastViewedAtUtc
        ? `Last opened ${formatRelativeTime(share.lastViewedAtUtc)}.`
        : 'Nobody has opened it yet.'}
    >
      <dl class="divide-separator grid grid-cols-3 divide-x py-3">
        {#each [{ label: 'Opens', value: share.views }, { label: 'Visitors', value: share.visitors }, { label: 'Plays', value: share.plays }] as fig (fig.label)}
          <div class="flex flex-col items-center gap-0.5 px-2">
            <dt class="text-footnote text-muted-foreground md:text-xs">{fig.label}</dt>
            <dd class="text-title-2 font-semibold md:text-2xl">{fig.value.toLocaleString()}</dd>
          </div>
        {/each}
      </dl>
    </GroupedList.Section>

    <GroupedList.Section headingLevel={2} header="Opens per day" footer={chartFooter}>
      <div class="flex flex-col gap-4 px-4 pt-3 pb-3">
        <SegmentedControl
          items={RANGES}
          value={range}
          label="Days shown"
          onValueChange={(v) => (range = v)}
          class="self-start"
        />
        <ShareOpensChart daily={detail.daily} />
      </div>
    </GroupedList.Section>

    {#if detail.sources.length > 0}
      <GroupedList.Section
        headingLevel={2}
        header="Where visitors came from"
        footer="From the page they came from, or the app whose browser opened the link."
      >
        {#each detail.sources as s (s.source ?? '')}
          {@render barRow(
            sourceLabel(s.source),
            countLabel(s.views, 'open', 'opens'),
            (s.views / sourceMax) * 100
          )}
        {/each}
      </GroupedList.Section>
    {/if}

    {#if share.scope === 'Album' && detail.tracks.length > 0}
      <GroupedList.Section headingLevel={2} header="Plays by track">
        {#each detail.tracks as t (t.songId)}
          {@render barRow(
            t.title,
            countLabel(t.plays, 'play', 'plays'),
            (t.plays / trackMax) * 100
          )}
        {/each}
      </GroupedList.Section>
    {/if}

    {#if revoked}
      <GroupedList.Section
        headingLevel={2}
        header="Link"
        footer="Revoked links stop working for everyone. Sharing the song again makes a new link."
      >
        <GroupedList.Row icon={Link2Off} label="Revoked" sublabel={url} />
      </GroupedList.Section>
    {:else}
      <GroupedList.Section
        headingLevel={2}
        header="Link"
        footer="Anyone with the link can play it and read its lyrics."
      >
        <GroupedList.Row icon={Copy} label="Copy link" sublabel={url} onclick={copyLink} />
        {#if canShareSheet}
          <GroupedList.Row icon={Share} label="Share…" onclick={openShareSheet} />
        {/if}
        <GroupedList.Row
          icon={ExternalLink}
          label="Open link"
          href={url}
          target="_blank"
          rel="noopener"
        />
      </GroupedList.Section>

      <GroupedList.Section>
        <GroupedList.Row
          label="Revoke link"
          destructive
          disabled={revoking}
          onclick={() => (confirmRevoke = true)}
        />
      </GroupedList.Section>
    {/if}
  {/if}
</div>

<AlertDialog.Root bind:open={confirmRevoke}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title>Revoke this link?</AlertDialog.Title>
      <AlertDialog.Description>
        It stops working for everyone, including people who already have it. Its numbers stay here.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action variant="destructive" onclick={() => void revoke()}
        >Revoke</AlertDialog.Action
      >
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
