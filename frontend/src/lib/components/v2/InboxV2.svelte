<script lang="ts">
  import { Tag, Copy, Sparkles } from '@lucide/svelte';
  import type { Component } from 'svelte';
  import { page } from '$app/state';
  import { replaceState } from '$app/navigation';
  import { cn } from '$lib/utils';
  import InboxTagReviewV2 from './inbox/InboxTagReviewV2.svelte';
  import InboxDuplicatesV2 from './inbox/InboxDuplicatesV2.svelte';
  import InboxAiFlaggedV2 from './inbox/InboxAiFlaggedV2.svelte';

  type TabId = 'review' | 'dupes' | 'ai';

  type Tab = { id: TabId; label: string; icon: Component };
  const TABS: Tab[] = [
    { id: 'review', label: 'Tag review', icon: Tag },
    { id: 'dupes', label: 'Duplicates', icon: Copy },
    { id: 'ai', label: 'AI flagged', icon: Sparkles }
  ];

  // The active subtab is driven by ?tab= so the sidebar subitems, breadcrumb, and
  // browser back/forward all stay in sync. Falls back to Tag review.
  const tab = $derived.by<TabId>(() => {
    const t = page.url.searchParams.get('tab');
    return t === 'dupes' || t === 'ai' ? t : 'review';
  });

  function selectTab(next: TabId) {
    if (next === tab) return;
    const url = new URL(page.url);
    if (next === 'review') url.searchParams.delete('tab');
    else url.searchParams.set('tab', next);
    // Dropping any ?song deep-link once the user navigates between tabs.
    url.searchParams.delete('song');
    replaceState(url, {});
  }

  // Live per-tab counts reported up by each subtab (null while loading).
  let counts = $state<Record<TabId, number | null>>({ review: null, dupes: null, ai: null });

  // Only reassign when the value actually changes — reallocating `counts` on
  // every report would re-render this component (and recreate the inline
  // oncount arrows) for no reason. The children also untrack the callback, so
  // this is belt-and-suspenders against a reactive feedback loop.
  function reportCount(tab: TabId, n: number | null) {
    if (counts[tab] === n) return;
    counts = { ...counts, [tab]: n };
  }
  const totalAwaiting = $derived.by(() => {
    const vals = [counts.review, counts.dupes, counts.ai];
    if (vals.every((v) => v == null)) return null;
    return vals.reduce<number>((sum, v) => sum + (v ?? 0), 0);
  });
</script>

<!-- Header -->
<header class="border-border flex shrink-0 items-end justify-between gap-4 border-b px-7 py-5">
  <div class="min-w-0">
    <div class="text-muted-foreground font-mono text-[10px] tracking-[0.12em] uppercase">
      {totalAwaiting == null ? 'Loading…' : `${totalAwaiting.toLocaleString()} item${totalAwaiting === 1 ? '' : 's'} awaiting you`}
    </div>
    <h1 class="mt-1 text-2xl font-semibold tracking-tight">Inbox</h1>
    <p class="text-muted-foreground mt-1 max-w-2xl text-xs">
      Everything the pipeline couldn't auto-resolve. Pick the right tag candidate, compare ambiguous
      duplicates, or inspect what the AI grader flagged as wrong.
    </p>
  </div>
</header>

<!-- Subtabs -->
<nav class="border-border flex shrink-0 items-center gap-1 border-b px-7" aria-label="Inbox queues">
  {#each TABS as t (t.id)}
    {@const isActive = t.id === tab}
    {@const Icon = t.icon}
    {@const count = counts[t.id]}
    <button
      type="button"
      onclick={() => selectTab(t.id)}
      data-active={isActive || undefined}
      class={cn(
        'relative flex items-center gap-1.5 px-2.5 py-2.5 text-[12.5px] transition-colors',
        'after:absolute after:inset-x-2.5 after:bottom-0 after:h-[2px] after:rounded-full after:bg-transparent',
        isActive ? 'text-foreground font-medium after:bg-primary' : 'text-muted-foreground hover:text-foreground'
      )}
    >
      <Icon class="size-3.5" />
      <span>{t.label}</span>
      {#if count != null && count > 0}
        <span
          class={cn(
            'rounded-full px-1.5 py-px font-mono text-[10px] tabular-nums',
            isActive ? 'bg-primary/15 text-primary' : 'bg-muted text-muted-foreground'
          )}
        >{count.toLocaleString()}</span>
      {/if}
    </button>
  {/each}
</nav>

<!-- Body: only the active queue is mounted (keyed so switching resets state). -->
{#if tab === 'review'}
  <InboxTagReviewV2 oncount={(n) => reportCount('review', n)} />
{:else if tab === 'dupes'}
  <InboxDuplicatesV2 oncount={(n) => reportCount('dupes', n)} />
{:else}
  <InboxAiFlaggedV2 oncount={(n) => reportCount('ai', n)} />
{/if}
