<script lang="ts">
  import { untrack } from 'svelte';
  import {
    Check,
    ChevronUp,
    ChevronDown,
    CircleCheck,
    Loader2,
    RefreshCw,
    Pin,
    Unlink
  } from '@lucide/svelte';
  import {
    fetchDuplicates,
    resolveDuplicates,
    dismissDuplicates,
    type DuplicateGroup,
    type DuplicateMember
  } from '$lib/api-client';
  import { formatFileSize, formatDuration, formatBitrate } from '$lib/formatters';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { Badge } from '$lib/components/ui/badge';
  import { Button } from '$lib/components/ui/button';
  import * as DropdownMenu from '$lib/components/ui/dropdown-menu';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { toast } from 'svelte-sonner';
  import { cn } from '$lib/utils';
  import InboxDecisionBar from './InboxDecisionBar.svelte';
  import InboxQueueRow from './InboxQueueRow.svelte';
  import { coverUrlForSongId } from './song-cover';
  import InboxQueueStates from './InboxQueueStates.svelte';
  import { QueueSelection } from './queue-selection.svelte';
  import { nextAfterDecision } from './queue-selection';

  type Props = { oncount?: (n: number | null) => void };
  const { oncount }: Props = $props();

  const isMobile = new IsMobile();
  const compact = $derived(isMobile.current);

  let groups = $state<DuplicateGroup[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let acting = $state(false);
  let actionError = $state<string | null>(null);

  // Invoke via untrack() so this effect tracks `loading`/`groups` only, not the
  // `oncount` prop identity — see the note in InboxTagReviewV2 for why tracking
  // it loops (effect_update_depth_exceeded).
  $effect(() => {
    const n = loading ? null : groups.length;
    untrack(() => oncount?.(n));
  });

  // A quiet reload (after a decision) keeps the page in place instead of flashing the skeleton.
  async function load(quiet = false) {
    try {
      if (!quiet) loading = true;
      error = null;
      const res = await fetchDuplicates();
      groups = res.duplicateGroups ?? [];
    } catch (err) {
      if (quiet) toast.error(err instanceof Error ? err.message : 'Failed to reload duplicates');
      else error = err instanceof Error ? err.message : 'Failed to load duplicates';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void load();
  });

  // Selected by ?group=<groupId>: the cluster's lowest song id, which survives a reload — a list
  // index would point at a different cluster after every decision.
  const groupIds = $derived(groups.map((g) => g.groupId));
  const selection = new QueueSelection({
    tab: 'dupes',
    param: 'group',
    ids: () => groupIds,
    compact: () => compact,
    ready: () => !loading
  });
  const position = $derived(selection.position);
  const selectedGroup = $derived(groups.find((g) => g.groupId === selection.selectedId) ?? null);

  // A decision advances to the next cluster (a replace — Back still returns to the list), or back
  // to the list on a phone when it was the last one.
  async function decide(group: DuplicateGroup, action: () => Promise<void>, done: string) {
    if (acting) return;
    try {
      acting = true;
      actionError = null;
      await action();
      selection.select(nextAfterDecision(group.groupId, groupIds));
      toast.success(done);
      await load(true);
    } catch (err) {
      actionError = err instanceof Error ? err.message : 'Failed to resolve the duplicate group';
    } finally {
      acting = false;
    }
  }

  function keep(group: DuplicateGroup, keeperId: number) {
    const keeper = group.members.find((m) => m.id === keeperId);
    const losers = group.members.filter((m) => m.id !== keeperId);
    return decide(
      group,
      () =>
        resolveDuplicates(
          keeperId,
          losers.map((m) => m.id)
        ),
      `Kept “${keeper?.title || keeper?.fileName || 'the copy'}” — ${losers.length} other ${
        losers.length === 1 ? 'copy is' : 'copies are'
      } left out of the library`
    );
  }

  function dismiss(group: DuplicateGroup) {
    return decide(
      group,
      () => dismissDuplicates(group.members.map((m) => m.id)),
      'Marked as not duplicates'
    );
  }

  function reasonChips(m: DuplicateMember): string[] {
    const chips: string[] = [];
    for (const r of m.reasons ?? []) {
      if (r === 'exact-fingerprint') chips.push('Same fingerprint');
      else if (r === 'fingerprint-similarity')
        chips.push(
          m.similarity != null
            ? `${Math.round(m.similarity * 100)}% acoustic match`
            : 'Acoustic match'
        );
      else if (r === 'acoustid') chips.push('Same AcoustID');
      else if (r === 'isrc') chips.push('Same ISRC');
      else if (r === 'metadata') chips.push('Metadata match');
    }
    return chips;
  }

  function subtitleOf(m: DuplicateMember): string {
    return [m.albumArtist ?? m.artist, m.album].filter(Boolean).join(' · ');
  }

  function groupLabel(g: DuplicateGroup): { title: string; artist: string; url: string | null } {
    const head = g.keeper ?? g.members[0];
    return {
      title: (head?.title || head?.fileName) ?? 'Unknown',
      artist: head?.albumArtist ?? head?.artist ?? 'Unknown',
      url: head ? coverUrlForSongId(head.id) : null
    };
  }

  function statsOf(m: DuplicateMember): { l: string; v: string; mono?: boolean }[] {
    return [
      { l: 'Bitrate', v: formatBitrate(m.bitrate, m.extension) },
      { l: 'Size', v: formatFileSize(m.fileSizeBytes) },
      { l: 'Duration', v: formatDuration(m.durationSeconds) },
      {
        l: 'Fingerprint',
        v: m.fingerprint ? m.fingerprint.slice(0, 12) + '…' : '—',
        mono: true
      }
    ];
  }

  // Why the copy was paired, and what keeping another one does to it — the phone's section
  // footer (desktop shows the reasons as chips on the card).
  function memberFooter(m: DuplicateMember): string | undefined {
    const parts = [reasonChips(m).join(' · ')];
    if (!m.isKeeper && m.isBuilt)
      parts.push('Already built — its destination file is left in place (nothing is deleted).');
    return parts.filter(Boolean).join('. ') || undefined;
  }

  const FOOTNOTE =
    'Keeping a copy excludes the others from the library build — source files are never touched.';

  // The phone's list unmounts while a cluster is pushed; put it back where it was on return.
  let listScroller = $state<HTMLElement | null>(null);
  let listScrollTop = 0;
  $effect(() => {
    const el = listScroller;
    if (el && listScrollTop > 0) requestAnimationFrame(() => (el.scrollTop = listScrollTop));
  });
  let detailScroller = $state<HTMLElement | null>(null);
  $effect(() => {
    void selection.selectedId;
    untrack(() => detailScroller?.scrollTo({ top: 0 }));
  });
  function openRow(event: MouseEvent, id: number) {
    if (compact && listScroller) listScrollTop = listScroller.scrollTop;
    selection.onRowClick(event, id);
  }
</script>

<!-- A label, not a control: green text is kept for things you can tap, so a confirmed match is a
     grey badge with the success glyph in the tint — the app's one way of saying "done". -->
{#snippet confidenceBadge(g: DuplicateGroup)}
  {#if g.confidence === 'confirmed'}
    <Badge variant="secondary"
      ><CircleCheck class="text-primary" aria-hidden="true" />Confirmed</Badge
    >
  {:else}
    <Badge variant="warning">Suspected</Badge>
  {/if}
{/snippet}

{#snippet queueList()}
  {#each groups as g (g.groupId)}
    {@const meta = groupLabel(g)}
    <InboxQueueRow
      href={selection.href(g.groupId)}
      onclick={(e) => openRow(e, g.groupId)}
      selected={selection.selectedId === g.groupId}
      {compact}
      cover={meta}
      title={meta.title}
    >
      {#snippet detail()}
        <span class="min-w-0 truncate">{meta.artist}</span>
      {/snippet}
      {#snippet trailing()}
        <div class="flex shrink-0 flex-col items-end gap-1 md:gap-0.5">
          <span class="text-footnote text-muted-foreground tabular-nums md:text-[11px]">
            {g.members.length} copies
          </span>
          {#if g.confidence === 'suspected'}
            <Badge variant="warning">Suspected</Badge>
          {/if}
        </div>
      {/snippet}
    </InboxQueueRow>
  {/each}
{/snippet}

{#snippet refreshItem()}
  <DropdownMenu.Item onSelect={() => void load()}><RefreshCw /> Refresh</DropdownMenu.Item>
{/snippet}

{#snippet refreshAction()}
  <Button
    variant="ghost"
    size="icon"
    aria-label="Refresh duplicates"
    title="Refresh"
    onclick={() => void load()}
  >
    <RefreshCw />
  </Button>
{/snippet}

{#snippet chevrons()}
  <Button
    variant="ghost"
    size="icon"
    aria-label="Previous group"
    disabled={position.prev == null}
    onclick={() => selection.select(position.prev)}
  >
    <ChevronUp />
  </Button>
  <Button
    variant="ghost"
    size="icon"
    aria-label="Next group"
    disabled={position.next == null}
    onclick={() => selection.select(position.next)}
  >
    <ChevronDown />
  </Button>
{/snippet}

{#snippet queueStates()}
  {#if loading}
    <InboxQueueStates state="loading" label="Loading duplicates…" {compact} />
  {:else if error}
    <InboxQueueStates state="error" message={error} onretry={load} />
  {:else}
    <InboxQueueStates state="empty" icon={Check} title="No duplicates detected">
      Detection runs after every fingerprint pass and pairs files by acoustic fingerprint, shared
      identifiers and matching metadata.
    </InboxQueueStates>
  {/if}
{/snippet}

{#if compact}
  {#if selectedGroup}
    {@const head = groupLabel(selectedGroup)}
    <!-- ── Phone: the pushed cluster, one section per copy ───────────────────────── -->
    <div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
      <div
        bind:this={detailScroller}
        class="min-h-0 flex-1 overflow-y-auto overscroll-contain pb-(--mh-content-pad)"
      >
        <PageToolbarV2
          title="{position.position} of {position.total}"
          titleLabel="Duplicate tracks"
          meta="Duplicate tracks"
          largeTitle={false}
          grouped
          actions={chevrons}
        />
        <div class="flex flex-col gap-7 pt-3 pb-6">
          <GroupedList.Section
            footer="{selectedGroup.members.length} copies of the same recording."
          >
            <div class="flex items-center gap-3 px-4 py-3">
              <Cover
                artist={head.artist}
                title={head.title}
                coverUrl={head.url}
                size={60}
                corner={6}
                caption={false}
                dprCap={3}
              />
              <div class="min-w-0 flex-1">
                <h2 class="text-headline line-clamp-2 break-words">{head.title}</h2>
                <p class="text-subheadline text-muted-foreground truncate">{head.artist}</p>
                <div class="mt-1">{@render confidenceBadge(selectedGroup)}</div>
              </div>
            </div>
          </GroupedList.Section>

          {#each selectedGroup.members as m, i (m.id)}
            <GroupedList.Section
              header={m.isKeeper ? 'Recommended keep' : `Copy ${i + 1}`}
              footer={memberFooter(m)}
            >
              <div
                class="after:bg-separator relative px-4 py-2.5 after:absolute after:right-0 after:bottom-0 after:left-4 after:h-(--hairline)"
              >
                <div class="text-body flex items-center gap-2">
                  <span class="min-w-0 truncate">{m.title || m.fileName}</span>
                  {#if m.isPinned}
                    <span
                      class="text-footnote text-muted-foreground inline-flex items-center gap-1"
                    >
                      <Pin class="size-3.5" aria-hidden="true" /> Pinned
                    </span>
                  {/if}
                </div>
                <div class="text-subheadline text-muted-foreground truncate">
                  {subtitleOf(m) || '—'}
                </div>
              </div>
              {#each statsOf(m) as stat (stat.l)}
                {#if stat.mono}
                  <GroupedList.Row label={stat.l}>
                    {#snippet trailing()}
                      <span class="text-subheadline text-muted-foreground font-mono">{stat.v}</span>
                    {/snippet}
                  </GroupedList.Row>
                {:else}
                  <GroupedList.Row label={stat.l} value={stat.v} />
                {/if}
              {/each}
              <div
                class="text-footnote text-muted-foreground after:bg-separator relative px-4 py-2.5 font-mono break-all after:absolute after:right-0 after:bottom-0 after:left-4 after:h-(--hairline) last:after:hidden"
              >
                {m.sourcePath}
              </div>
              {#if !m.isKeeper}
                <GroupedList.Row disabled={acting} onclick={() => keep(selectedGroup, m.id)}>
                  <span class="text-body text-primary">Keep this one</span>
                </GroupedList.Row>
              {/if}
            </GroupedList.Section>
          {/each}

          <div class="text-footnote px-8">
            {#if actionError}
              <p role="alert" class="text-destructive-text">{actionError}</p>
            {:else}
              <p class="text-muted-foreground">{FOOTNOTE}</p>
            {/if}
          </div>
        </div>
      </div>

      <!-- At larger text sizes "Not duplicates" falls back to its glyph (the words stay for
           VoiceOver) and "Keep recommended" truncates — see InboxDecisionBar. -->
      <InboxDecisionBar label="Duplicate decision">
        <Button
          variant="ghost"
          class="text-body h-11 rounded-full px-3"
          disabled={acting}
          onclick={() => dismiss(selectedGroup)}
        >
          <Unlink class="hidden size-5 in-data-tight:block" aria-hidden="true" />
          <span class="in-data-tight:sr-only">Not duplicates</span>
        </Button>
        <Button
          class="text-headline ml-auto h-11 gap-1.5 rounded-full px-3.5 in-data-tight:min-w-0 in-data-tight:shrink"
          disabled={acting}
          onclick={() => keep(selectedGroup, selectedGroup.keeper.id)}
        >
          {#if acting}<Loader2 class="size-5 animate-spin" />{/if}
          <span class="truncate">Keep recommended</span>
        </Button>
      </InboxDecisionBar>
    </div>
  {:else}
    <!-- ── Phone: the list of clusters ───────────────────────────────────────────── -->
    <div class="flex min-h-0 flex-1 flex-col">
      <div
        bind:this={listScroller}
        class="min-h-0 flex-1 overflow-y-auto overscroll-contain pb-(--mh-content-pad)"
      >
        <PageToolbarV2
          title="Duplicate tracks"
          meta={loading
            ? undefined
            : `${groups.length} duplicate group${groups.length === 1 ? '' : 's'}`}
          more={refreshItem}
        />
        {#if loading || error || groups.length === 0}
          {@render queueStates()}
        {:else}
          {@render queueList()}
        {/if}
      </div>
    </div>
  {/if}
{:else}
  <!-- ── Desktop: list pane + detail pane ──────────────────────────────────────────── -->
  <div class="flex min-h-0 flex-1 flex-col">
    <PageToolbarV2
      title="Duplicate tracks"
      meta={loading
        ? undefined
        : `${groups.length} duplicate group${groups.length === 1 ? '' : 's'}`}
      actions={refreshAction}
    />
    {#if !loading && (error || groups.length === 0)}
      <div class="min-h-0 flex-1 overflow-y-auto pb-(--mh-content-pad)">
        {@render queueStates()}
      </div>
    {:else}
      <!-- The list pane gives up width first (down to 240px); see InboxTagReviewV2. -->
      <div
        class="grid min-h-0 flex-1 grid-cols-[clamp(240px,30%,320px)_minmax(0,1fr)] overflow-hidden"
      >
        <aside
          aria-label="Duplicate groups"
          class="border-separator bg-surface-sunken flex min-h-0 flex-col border-r"
        >
          <div
            class="min-h-0 flex-1 overflow-y-auto p-1.5 pb-[calc(0.375rem_+_var(--mh-content-pad))]"
          >
            {#if loading}
              {@render queueStates()}
            {:else}
              {@render queueList()}
            {/if}
          </div>
        </aside>

        {#if selectedGroup}
          <!-- Laid out by the pane's own width (a container): a narrow pane stacks the stats two
               by two instead of squeezing four columns into it. -->
          <div class="@container flex min-h-0 min-w-0 flex-col overflow-hidden">
            <div
              class="border-separator flex items-center gap-3 border-b px-4 py-3 @min-[36rem]:px-6"
            >
              <div class="min-w-0 flex-1">
                <div class="flex items-center gap-2">
                  <h2 class="text-[14px] font-semibold">Duplicate group</h2>
                  {@render confidenceBadge(selectedGroup)}
                </div>
                <div class="text-muted-foreground truncate text-[12px]">
                  {selectedGroup.members.length} copies of the same recording
                </div>
              </div>
            </div>

            <div class="min-h-0 flex-1 space-y-3 overflow-y-auto px-4 py-4 @min-[36rem]:px-6">
              {#each selectedGroup.members as m (m.id)}
                {@const keeper = m.isKeeper}
                <div
                  class={cn(
                    'rounded-lg border p-4',
                    keeper ? 'border-primary bg-primary/5' : 'border-border bg-card'
                  )}
                >
                  <div class="mb-2 flex flex-wrap items-center justify-between gap-2">
                    <div
                      class={cn(
                        'flex items-center gap-1.5 text-[11px] font-medium',
                        keeper ? 'text-primary' : 'text-muted-foreground'
                      )}
                    >
                      <span
                        class={cn(
                          'size-1.5 rounded-full',
                          keeper ? 'bg-primary' : 'bg-muted-foreground-dim'
                        )}
                        aria-hidden="true"
                      ></span>
                      {keeper ? 'Recommended keep' : 'Duplicate copy'}
                      {#if m.isPinned}
                        <span class="text-primary inline-flex items-center gap-0.5"
                          ><Pin class="size-3" aria-hidden="true" /> pinned</span
                        >
                      {/if}
                    </div>
                    {#if !keeper}
                      <Button
                        variant="outline"
                        size="sm"
                        disabled={acting}
                        onclick={() => keep(selectedGroup, m.id)}
                      >
                        Keep this one
                      </Button>
                    {/if}
                  </div>
                  <div class="truncate text-[15px] font-medium">{m.title || m.fileName}</div>
                  <div class="text-muted-foreground truncate text-[12px]">
                    {subtitleOf(m) || '—'}
                  </div>
                  {#if reasonChips(m).length > 0}
                    <div class="mt-2 flex flex-wrap gap-1">
                      {#each reasonChips(m) as chip, chipIdx (chipIdx)}
                        <span class="bg-muted text-foreground rounded-sm px-1.5 py-px text-[11px]"
                          >{chip}</span
                        >
                      {/each}
                    </div>
                  {/if}
                  <div class="mt-3 grid grid-cols-2 gap-3 @min-[30rem]:grid-cols-4">
                    {#each statsOf(m) as stat (stat.l)}
                      <div>
                        <div class="text-muted-foreground text-[11px]">{stat.l}</div>
                        <div
                          class={cn(
                            'text-[12.5px] tabular-nums',
                            stat.mono && 'font-mono text-[11.5px]'
                          )}
                        >
                          {stat.v}
                        </div>
                      </div>
                    {/each}
                  </div>
                  <div
                    class="text-muted-foreground mt-3 font-mono text-[11px] leading-relaxed break-all"
                  >
                    {m.sourcePath}
                  </div>
                  {#if !keeper && m.isBuilt}
                    <p class="text-muted-foreground mt-2 text-[11px]">
                      Already built — its destination file is left in place (nothing is deleted).
                    </p>
                  {/if}
                </div>
              {/each}
            </div>

            <!-- Last item in a full-height column: carries the mini player's clearance itself. -->
            <div
              class="border-separator bg-background flex flex-wrap items-center gap-2 border-t px-4 pt-3 pb-[calc(0.75rem_+_var(--mh-content-pad))] @min-[36rem]:px-6"
            >
              <Button
                size="sm"
                disabled={acting}
                onclick={() => keep(selectedGroup, selectedGroup.keeper.id)}
              >
                {#if acting}<Loader2 class="mr-1 size-3.5 animate-spin" />{/if}
                Keep recommended
              </Button>
              <Button
                variant="outline"
                size="sm"
                disabled={acting}
                onclick={() => dismiss(selectedGroup)}
              >
                Not duplicates
              </Button>
              {#if actionError}
                <span role="alert" class="text-destructive-text text-[12px]">{actionError}</span>
              {:else}
                <span class="text-muted-foreground text-[11.5px]">{FOOTNOTE}</span>
              {/if}
            </div>
          </div>
        {/if}
      </div>
    {/if}
  </div>
{/if}
