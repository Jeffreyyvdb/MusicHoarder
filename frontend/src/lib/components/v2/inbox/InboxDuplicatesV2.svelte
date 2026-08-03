<script lang="ts">
  import { untrack } from 'svelte';
  import { Check, ChevronLeft, Loader2, RefreshCw, Copy, Pin } from '@lucide/svelte';
  import {
    fetchDuplicates,
    resolveDuplicates,
    dismissDuplicates,
    type DuplicateGroup,
    type DuplicateMember
  } from '$lib/api-client';
  import { formatFileSize, formatDuration, formatBitrate } from '$lib/formatters';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { Button } from '$lib/components/ui/button';
  import { cn } from '$lib/utils';

  type Props = { oncount?: (n: number | null) => void };
  const { oncount }: Props = $props();

  let groups = $state<DuplicateGroup[]>([]);
  // null == "showing the list" (the mobile master pane). Defaults to the first
  // group after a load so desktop's two-pane layout opens populated.
  let selectedIdx = $state<number | null>(0);
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

  async function load() {
    try {
      loading = true;
      error = null;
      const res = await fetchDuplicates();
      groups = res.duplicateGroups ?? [];
      selectedIdx = 0;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to load duplicates';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void load();
  });

  const selectedGroup = $derived(selectedIdx != null ? (groups[selectedIdx] ?? null) : null);

  async function keep(group: DuplicateGroup, keeperId: number) {
    if (acting) return;
    try {
      acting = true;
      actionError = null;
      const loserIds = group.members.filter((m) => m.id !== keeperId).map((m) => m.id);
      await resolveDuplicates(keeperId, loserIds);
      await load();
    } catch (err) {
      actionError = err instanceof Error ? err.message : 'Failed to resolve the duplicate group';
    } finally {
      acting = false;
    }
  }

  async function dismiss(group: DuplicateGroup) {
    if (acting) return;
    try {
      acting = true;
      actionError = null;
      await dismissDuplicates(group.members.map((m) => m.id));
      await load();
    } catch (err) {
      actionError = err instanceof Error ? err.message : 'Failed to dismiss the duplicate group';
    } finally {
      acting = false;
    }
  }

  function reasonChips(m: DuplicateMember): string[] {
    const chips: string[] = [];
    for (const r of m.reasons ?? []) {
      if (r === 'exact-fingerprint') chips.push('Same fingerprint');
      else if (r === 'fingerprint-similarity')
        chips.push(m.similarity != null ? `${Math.round(m.similarity * 100)}% acoustic match` : 'Acoustic match');
      else if (r === 'acoustid') chips.push('Same AcoustID');
      else if (r === 'isrc') chips.push('Same ISRC');
      else if (r === 'metadata') chips.push('Metadata match');
    }
    return chips;
  }

  function subtitleOf(m: DuplicateMember): string {
    return [m.albumArtist ?? m.artist, m.album].filter(Boolean).join(' · ');
  }

  function groupLabel(g: DuplicateGroup): { title: string; artist: string } {
    const head = g.keeper ?? g.members[0];
    return {
      title: (head?.title || head?.fileName) ?? 'Unknown',
      artist: (head?.albumArtist ?? head?.artist) ?? 'Unknown'
    };
  }
</script>

{#if loading}
  <div class="flex flex-1 items-center justify-center p-8">
    <div class="text-muted-foreground flex items-center gap-2 text-sm">
      <Loader2 class="size-5 animate-spin" /> Loading duplicates…
    </div>
  </div>
{:else if error}
  <div class="flex flex-1 items-center justify-center p-8">
    <div class="max-w-md text-center">
      <p class="text-destructive mb-3 text-sm">{error}</p>
      <Button onclick={load}>Retry</Button>
    </div>
  </div>
{:else if groups.length === 0}
  <div class="flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
    <span class="bg-primary/10 text-primary grid size-12 place-items-center rounded-full">
      <Check class="size-6" />
    </span>
    <div class="text-[15px] font-semibold">No duplicates detected</div>
    <p class="text-muted-foreground max-w-sm text-[12.5px]">
      Detection runs after every fingerprint pass and pairs files by acoustic fingerprint, shared identifiers and matching metadata.
    </p>
  </div>
{:else}
  <div class="grid min-h-0 flex-1 grid-cols-1 overflow-hidden md:grid-cols-[320px_1fr]">
    <!-- List — single-pane on mobile: hidden once a group is selected. -->
    <aside
      class="border-border bg-surface-sunken flex min-h-0 flex-col border-r md:flex"
      class:hidden={selectedIdx != null}
    >
      <div class="border-border flex items-center justify-between gap-2 border-b px-4 py-2.5">
        <span class="text-muted-foreground text-[11px]">{groups.length} duplicate group{groups.length === 1 ? '' : 's'}</span>
        <button
          type="button"
          onclick={load}
          title="Refresh"
          class="text-muted-foreground hover:bg-accent hover:text-foreground grid size-7 place-items-center rounded-md transition-colors"
        >
          <RefreshCw class="size-3.5" />
        </button>
      </div>
      <div class="min-h-0 flex-1 overflow-y-auto p-1.5 pb-[calc(0.375rem_+_var(--mh-content-pad))]">
        {#each groups as g, i (g.groupId)}
          {@const meta = groupLabel(g)}
          <button
            type="button"
            onclick={() => (selectedIdx = i)}
            class={cn(
              'mb-0.5 flex w-full items-center gap-2.5 rounded-md border-l-2 border-transparent py-2 pr-2.5 pl-2 text-left transition-[background-color,transform] duration-100 ease-out active:scale-[0.99]',
              selectedIdx === i ? 'border-l-primary bg-card' : 'hover:bg-accent'
            )}
          >
            <Cover artist={meta.artist} title={meta.title} size={40} corner={6} caption={false} />
            <div class="min-w-0 flex-1">
              <div class="truncate text-[13px] font-medium">{meta.title}</div>
              <div class="text-muted-foreground truncate text-[11.5px]">{meta.artist}</div>
            </div>
            <div class="flex shrink-0 flex-col items-end gap-0.5">
              <span class="text-muted-foreground text-[11px] tabular-nums">{g.members.length} copies</span>
              {#if g.confidence === 'suspected'}
                <span class="rounded-sm bg-amber-500/15 px-1 py-px text-[9.5px] font-medium text-amber-600 dark:text-amber-400">suspected</span>
              {/if}
            </div>
          </button>
        {/each}
      </div>
    </aside>

    <!-- Detail: member cards + resolution actions — single-pane on mobile. -->
    {#if selectedGroup}
      <div
        class="flex min-h-0 min-w-0 flex-col overflow-hidden md:flex"
        class:hidden={selectedIdx == null}
      >
        <div class="border-border flex items-center gap-3 border-b px-4 py-3 sm:px-6">
          <button
            type="button"
            onclick={() => (selectedIdx = null)}
            class="text-muted-foreground hover:bg-accent hover:text-foreground -ml-1 grid size-8 shrink-0 place-items-center rounded-md transition-colors md:hidden"
            title="Back to list"
            aria-label="Back to list"
          >
            <ChevronLeft class="size-5" />
          </button>
          <Copy class="text-muted-foreground size-5 shrink-0" />
          <div class="min-w-0 flex-1">
            <div class="flex items-center gap-2">
              <span class="text-[14px] font-semibold">Duplicate group</span>
              <span
                class={cn(
                  'rounded-sm px-1.5 py-px text-[10px] font-medium',
                  selectedGroup.confidence === 'confirmed'
                    ? 'bg-primary/10 text-primary'
                    : 'bg-amber-500/15 text-amber-600 dark:text-amber-400'
                )}
              >
                {selectedGroup.confidence === 'confirmed' ? 'Confirmed' : 'Suspected'}
              </span>
            </div>
            <div class="text-muted-foreground truncate text-[12px]">
              {selectedGroup.members.length} copies of the same recording
            </div>
          </div>
        </div>

        <div class="min-h-0 flex-1 space-y-3 overflow-y-auto px-4 py-4 pb-[calc(1rem_+_var(--mh-content-pad))] sm:px-6">
          {#each selectedGroup.members as m (m.id)}
            {@const keeper = m.isKeeper}
            <div class={cn('rounded-lg border p-4', keeper ? 'border-primary bg-primary/5' : 'border-border bg-card')}>
              <div class="mb-2 flex items-center justify-between gap-2">
                <div class={cn('flex items-center gap-1.5 text-[11px] font-medium', keeper ? 'text-primary' : 'text-muted-foreground')}>
                  <span class={cn('size-1.5 rounded-full', keeper ? 'bg-primary' : 'bg-muted-foreground/50')}></span>
                  {keeper ? 'Recommended keep' : 'Duplicate copy'}
                  {#if m.isPinned}
                    <span class="text-primary inline-flex items-center gap-0.5"><Pin class="size-3" /> pinned</span>
                  {/if}
                </div>
                {#if !keeper}
                  <Button
                    variant="outline"
                    size="sm"
                    class="h-6 px-2 text-[11px]"
                    disabled={acting}
                    onclick={() => keep(selectedGroup, m.id)}
                  >
                    Keep this one
                  </Button>
                {/if}
              </div>
              <div class="truncate text-[15px] font-medium">{m.title || m.fileName}</div>
              <div class="text-muted-foreground truncate text-[12px]">{subtitleOf(m) || '—'}</div>
              {#if reasonChips(m).length > 0}
                <div class="mt-2 flex flex-wrap gap-1">
                  {#each reasonChips(m) as chip, chipIdx (chipIdx)}
                    <span class="bg-accent text-muted-foreground rounded-sm px-1.5 py-px text-[10px]">{chip}</span>
                  {/each}
                </div>
              {/if}
              <div class="mt-3 grid grid-cols-2 gap-3 sm:grid-cols-4">
                {#each [{ l: 'Bitrate', v: formatBitrate(m.bitrate, m.extension) }, { l: 'Size', v: formatFileSize(m.fileSizeBytes) }, { l: 'Duration', v: formatDuration(m.durationSeconds) }, { l: 'Fingerprint', v: m.fingerprint ? m.fingerprint.slice(0, 12) + '…' : '—', mono: true }] as stat (stat.l)}
                  <div>
                    <div class="text-muted-foreground text-[11px]">{stat.l}</div>
                    <div class={cn('text-[12.5px] tabular-nums', stat.mono && 'font-mono text-[11.5px]')}>{stat.v}</div>
                  </div>
                {/each}
              </div>
              <div class="text-muted-foreground mt-3 font-mono text-[10.5px] leading-relaxed break-all">{m.sourcePath}</div>
              {#if !keeper && m.isBuilt}
                <p class="text-muted-foreground mt-2 text-[11px]">
                  Already built — its destination file is left in place (nothing is deleted).
                </p>
              {/if}
            </div>
          {/each}
        </div>

        <div class="border-border bg-background flex flex-wrap items-center gap-2 border-t px-4 py-3 sm:px-6">
          <Button size="sm" disabled={acting} onclick={() => keep(selectedGroup, selectedGroup.keeper.id)}>
            {#if acting}<Loader2 class="mr-1 size-3.5 animate-spin" />{/if}
            Keep recommended
          </Button>
          <Button variant="outline" size="sm" disabled={acting} onclick={() => dismiss(selectedGroup)}>
            Not duplicates
          </Button>
          {#if actionError}
            <span class="text-destructive text-[12px]">{actionError}</span>
          {:else}
            <span class="text-muted-foreground text-[11.5px]">
              Keeping a copy excludes the others from the library build — source files are never touched.
            </span>
          {/if}
        </div>
      </div>
    {/if}
  </div>
{/if}
