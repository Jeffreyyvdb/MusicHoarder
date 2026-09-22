<script lang="ts">
  import { Users } from '@lucide/svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import { cn } from '$lib/utils';
  import { getArtistImageUrl, type GroupSummary } from '$lib/api-client';

  /** Below this many artists, the A–Z index is more chrome than it's worth on a phone — everything
      fits on one screen without it, so it's hidden there (still shown at sm+, where it always was). */
  const PHONE_INDEX_THRESHOLD = 20;

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

<div class="border-border mb-4 flex flex-wrap items-center gap-0.5 border-b pb-3.5 font-mono text-[11px]">
  <!-- Below the phone threshold this whole index is hidden on narrow viewports (still `contents` —
       i.e. unwrapped into the flex row — at sm+, where it always showed regardless of count). -->
  <div class={groups.length < PHONE_INDEX_THRESHOLD ? 'hidden sm:contents' : 'contents'}>
    <button
      type="button"
      onclick={() => (letter = 'all')}
      class={cn(
        'hover:bg-muted hover:text-foreground inline-flex min-h-7 min-w-7 items-center justify-center rounded px-2 transition-colors',
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
          'inline-flex min-h-7 min-w-7 items-center justify-center rounded px-2 transition-colors',
          letter === L && 'bg-muted text-foreground font-semibold',
          present
            ? 'text-muted-foreground hover:bg-muted hover:text-foreground'
            : 'text-muted-foreground-dim cursor-default'
        )}
      >
        {L}
      </button>
    {/each}
  </div>

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
          'inline-flex min-h-7 min-w-7 items-center justify-center rounded-[3px] px-2 transition-colors',
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

{#if isLoading && groups.length === 0}
  <!-- Skeleton tiles in the real grid, not a spinner or a sentence (F26). -->
  <div class="grid grid-cols-3 gap-x-4 gap-y-6 sm:grid-cols-4 md:grid-cols-5 lg:grid-cols-6 xl:grid-cols-7">
    {#each Array(14) as _, i (i)}
      <div class="flex flex-col items-center gap-2 p-1">
        <Skeleton class="aspect-square w-full rounded-full" />
        <div class="w-full space-y-1.5 px-0.5">
          <Skeleton class="mx-auto h-3 w-3/4" />
          <Skeleton class="mx-auto h-3 w-1/2" />
        </div>
      </div>
    {/each}
  </div>
{:else if filtered.length === 0}
  <div class="text-muted-foreground flex flex-col items-center justify-center gap-3 py-16 text-center">
    <Users class="size-10 opacity-40" />
    <p class="text-sm">No artists in this range.</p>
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
          coverUrl={getArtistImageUrl(group.label)}
          fallbackUrl={group.coverUrl}
          size={160}
          corner={80}
          caption={false}
          interactive
          class="!h-auto !w-full aspect-square !rounded-full shadow-[0_2px_10px_rgba(0,0,0,0.12)] hover:shadow-[0_8px_24px_rgba(0,0,0,0.18)] dark:shadow-[0_4px_14px_rgba(0,0,0,0.5)] dark:hover:shadow-[0_10px_28px_rgba(0,0,0,0.6)]"
        />
        <div class="min-w-0 w-full px-0.5 text-center">
          <p class="truncate text-[12.5px] font-medium">{group.label}</p>
          <!-- No `truncate` here on purpose (vis-10): the album count alone reads as the whole
               story when the track count clips off, so this wraps to a second line instead. -->
          <p class="text-muted-foreground text-[11.5px] tabular-nums">
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
