<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { cn } from '$lib/utils';
  import InboxTagReviewV2 from './inbox/InboxTagReviewV2.svelte';
  import InboxDuplicatesV2 from './inbox/InboxDuplicatesV2.svelte';
  import InboxAiFlaggedV2 from './inbox/InboxAiFlaggedV2.svelte';

  type TabId = 'review' | 'dupes' | 'ai';

  type Tab = { id: TabId; label: string };
  const TABS: Tab[] = [
    { id: 'review', label: 'Tag review' },
    { id: 'dupes', label: 'Duplicates' },
    { id: 'ai', label: 'AI flagged' }
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
    // goto (not raw replaceState) so the `tab` derived re-runs and the body
    // switches — the same reactive update the sidebar's <a> sub-links trigger.
    void goto(url.pathname + url.search, { replaceState: true, noScroll: true, keepFocus: true });
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
<header class="border-border flex shrink-0 items-end justify-between gap-4 border-b px-4 py-4 sm:px-7 sm:py-5">
  <div class="min-w-0">
    <h1 class="text-xl font-semibold tracking-tight sm:text-2xl">Inbox</h1>
    <p class="text-muted-foreground mt-1 max-w-2xl text-[13px]">
      {#if totalAwaiting != null}
        {totalAwaiting.toLocaleString()} item{totalAwaiting === 1 ? '' : 's'} awaiting you ·
      {/if}
      Everything the pipeline couldn't auto-resolve.
    </p>
  </div>
</header>

<!-- Subtabs — Apple-style segmented control, matching the section sub-nav and
     the song-panel tabs (one tab idiom app-wide). -->
<nav class="no-scrollbar border-border flex shrink-0 items-center overflow-x-auto border-b px-4 py-2 sm:px-7" aria-label="Inbox queues">
  <div class="bg-foreground/5 flex shrink-0 items-center gap-1 rounded-full p-1">
    {#each TABS as t (t.id)}
      {@const isActive = t.id === tab}
      {@const count = counts[t.id]}
      <button
        type="button"
        onclick={() => selectTab(t.id)}
        data-active={isActive || undefined}
        class={cn(
          'flex shrink-0 items-baseline gap-1.5 rounded-full px-3 py-1.5 text-xs font-medium whitespace-nowrap transition-colors sm:px-4 sm:text-[13px]',
          'focus-visible:ring-ring/60 outline-none focus-visible:ring-2',
          isActive
            ? 'bg-background text-foreground shadow-sm'
            : 'text-muted-foreground hover:text-foreground'
        )}
      >
        <span>{t.label}</span>
        {#if count != null && count > 0}
          <span class="text-muted-foreground text-xs tabular-nums">{count.toLocaleString()}</span>
        {/if}
      </button>
    {/each}
  </div>
</nav>

<!-- Body: only the active queue is mounted (keyed so switching resets state). -->
{#if tab === 'review'}
  <InboxTagReviewV2 oncount={(n) => reportCount('review', n)} />
{:else if tab === 'dupes'}
  <InboxDuplicatesV2 oncount={(n) => reportCount('dupes', n)} />
{:else}
  <InboxAiFlaggedV2 oncount={(n) => reportCount('ai', n)} />
{/if}
