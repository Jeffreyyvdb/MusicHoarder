<script lang="ts">
  import { Users } from '@lucide/svelte';
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
      letter === 'all' ? 'bg-muted text-foreground font-semibold' : 'text-muted-foreground'
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
        letter === L && 'bg-muted text-foreground font-semibold',
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
            ? 'bg-muted text-foreground font-semibold'
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
  <div
    class="grid grid-cols-3 gap-x-4 gap-y-6 sm:grid-cols-4 md:grid-cols-5 lg:grid-cols-6 xl:grid-cols-7"
  >
    {#each filtered as group (group.key)}
      <a
        href={hrefFor(group)}
        class="group focus-visible:ring-ring outline-hidden flex flex-col items-center gap-2 rounded-lg p-1 transition-transform [content-visibility:auto] [contain-intrinsic-size:auto_11rem] hover:-translate-y-0.5 focus-visible:ring-2 focus-visible:ring-offset-2"
        aria-label={`Browse ${group.label}`}
      >
        <Cover
          artist={group.coverArtist}
          title={group.coverTitle}
          coverUrl={group.coverUrl}
          size={160}
          corner={80}
          caption={false}
          interactive
          class="!h-auto !w-full aspect-square !rounded-full shadow-[0_2px_10px_rgba(0,0,0,0.12)] hover:shadow-[0_8px_24px_rgba(0,0,0,0.18)] dark:shadow-[0_4px_14px_rgba(0,0,0,0.5)] dark:hover:shadow-[0_10px_28px_rgba(0,0,0,0.6)]"
        />
        <div class="min-w-0 w-full px-0.5 text-center">
          <p class="truncate text-[12.5px] font-medium">{group.label}</p>
          <p class="text-muted-foreground truncate text-[11.5px] tabular-nums">
            {group.albumCount} album{group.albumCount === 1 ? '' : 's'} · {group.trackCount} track{group.trackCount ===
            1
              ? ''
              : 's'}
          </p>
        </div>
      </a>
    {/each}
  </div>
{/if}
