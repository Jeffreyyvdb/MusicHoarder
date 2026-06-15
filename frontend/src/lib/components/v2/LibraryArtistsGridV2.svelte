<script lang="ts">
  import { ChevronRight, Users } from '@lucide/svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { cn } from '$lib/utils';
  import type { GroupSummary } from '$lib/api-client';

  type Props = {
    groups: GroupSummary[];
    /** href builder for an artist card (links into `/library?artist=…`). */
    hrefFor: (group: GroupSummary) => string;
    isLoading?: boolean;
    /** `primary` shows lead/album artists only; `all` shows every credited artist. */
    mode?: 'primary' | 'all';
  };
  let { groups, hrefFor, isLoading = false, mode = $bindable('primary') }: Props = $props();

  const ALL_LETTERS = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ'.split('');

  let letter = $state<'all' | string>('all');

  function firstLetter(label: string): string {
    const c = label.trim()[0]?.toUpperCase() ?? '';
    return /[A-Z]/.test(c) ? c : '#';
  }

  const presentLetters = $derived.by(() => {
    const set = new Set<string>();
    for (const g of groups) set.add(firstLetter(g.label));
    return set;
  });

  const filtered = $derived.by(() => {
    if (letter === 'all') return groups;
    return groups.filter((g) => firstLetter(g.label) === letter);
  });
</script>

<div class="border-border mb-4 flex flex-wrap gap-0 border-b pb-3.5 font-mono text-[11px]">
  <button
    type="button"
    onclick={() => (letter = 'all')}
    class={cn(
      'hover:bg-muted hover:text-foreground min-w-[22px] rounded px-2 py-[3px] transition-colors',
      letter === 'all' ? 'bg-primary/10 text-primary font-semibold' : 'text-muted-foreground'
    )}
  >
    All
  </button>
  {#each ALL_LETTERS as L (L)}
    {@const present = presentLetters.has(L)}
    <button
      type="button"
      disabled={!present}
      onclick={() => present && (letter = L)}
      class={cn(
        'min-w-[22px] rounded px-2 py-[3px] transition-colors',
        letter === L && 'bg-primary/10 text-primary font-semibold',
        present
          ? 'text-muted-foreground hover:bg-muted hover:text-foreground'
          : 'text-muted-foreground/40 cursor-default'
      )}
    >
      {L}
    </button>
  {/each}

  <div
    class="border-border ml-auto flex items-center gap-0 self-center rounded border p-[2px]"
    title="Primary shows lead/album artists only; All shows every credited artist (incl. features)"
  >
    {#each [{ value: 'primary', text: 'Primary' }, { value: 'all', text: 'All' }] as opt (opt.value)}
      <button
        type="button"
        onclick={() => (mode = opt.value as 'primary' | 'all')}
        aria-pressed={mode === opt.value}
        class={cn(
          'rounded-[3px] px-2 py-[2px] transition-colors',
          mode === opt.value
            ? 'bg-primary/10 text-primary font-semibold'
            : 'text-muted-foreground hover:bg-muted hover:text-foreground'
        )}
      >
        {opt.text}
      </button>
    {/each}
  </div>
</div>

{#if filtered.length === 0}
  <div class="text-muted-foreground flex flex-col items-center justify-center gap-3 py-16 text-center">
    <Users class="size-10 opacity-40" />
    <p class="text-sm">{isLoading ? 'Loading artists…' : 'No artists in this range.'}</p>
  </div>
{:else}
  <div class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3 2xl:grid-cols-4">
    {#each filtered as group (group.key)}
      <a
        href={hrefFor(group)}
        class="border-border bg-card hover:border-border/80 group grid grid-cols-[56px_1fr_auto] items-center gap-3 rounded-[10px] border px-3.5 py-3 transition-all [content-visibility:auto] [contain-intrinsic-size:auto_5rem] hover:-translate-y-px hover:shadow-sm"
        aria-label={`Browse ${group.label}`}
      >
        <Cover
          artist={group.coverArtist}
          title={group.coverTitle}
          coverUrl={group.coverUrl}
          size={56}
          corner={28}
          caption={false}
          class="!size-14 shrink-0 !rounded-full"
        />
        <span class="min-w-0">
          <span class="block truncate text-[13.5px] font-semibold">{group.label}</span>
          <span class="text-muted-foreground mt-0.5 block font-mono text-[11px] tabular-nums">
            <b class="text-foreground/80 font-medium">{group.albumCount}</b> album{group.albumCount ===
            1
              ? ''
              : 's'} ·
            <b class="text-foreground/80 font-medium">{group.trackCount}</b> track{group.trackCount ===
            1
              ? ''
              : 's'}
          </span>
        </span>
        <ChevronRight class="text-muted-foreground/50 size-4 shrink-0" />
      </a>
    {/each}
  </div>
{/if}
