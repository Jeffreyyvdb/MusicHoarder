<script lang="ts">
  import { Search, Disc3, Music } from '@lucide/svelte';
  import { Input } from '$lib/components/ui/input';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import type { GroupSummary } from '$lib/api-client';

  type Props = {
    title: string;
    /** Builds the drill-down href for a group (e.g. `/app?artist=...`). */
    hrefFor: (group: GroupSummary) => string;
    groups: GroupSummary[];
    isLoading: boolean;
    searchPlaceholder?: string;
    /** Singular noun for the count summary, e.g. "artist" / "year". */
    noun: string;
  };
  const { title, hrefFor, groups, isLoading, searchPlaceholder, noun }: Props = $props();

  let searchQuery = $state('');

  const filtered = $derived.by(() => {
    const q = searchQuery.trim().toLowerCase();
    if (!q) return groups;
    return groups.filter((g) => g.label.toLowerCase().includes(q));
  });
</script>

<div class="flex min-h-0 flex-1 flex-col overflow-hidden">
  <div class="border-border bg-card/30 border-b px-4 py-5 md:px-6">
    <h1 class="text-2xl font-bold tracking-[-0.02em]">{title}</h1>
    <p class="text-muted-foreground mt-1 text-sm">
      {groups.length.toLocaleString()}
      {noun}{groups.length === 1 ? '' : 's'} in your library
    </p>
  </div>

  <div class="border-border border-b px-4 py-3 md:px-6">
    <div class="relative max-w-md">
      <Search class="text-muted-foreground absolute top-1/2 left-3 size-4 -translate-y-1/2" />
      <Input
        placeholder={searchPlaceholder ?? `Search ${noun}s...`}
        bind:value={searchQuery}
        class="bg-secondary border-0 pl-9"
      />
    </div>
  </div>

  {#if isLoading && groups.length === 0}
    <div class="text-muted-foreground flex flex-1 items-center justify-center p-8 text-sm">
      Loading…
    </div>
  {:else}
    <ScrollArea class="min-h-0 flex-1">
      <div class="p-4 pb-20 md:p-6">
        {#if filtered.length === 0}
          <div
            class="text-muted-foreground flex flex-col items-center justify-center gap-3 py-16 text-center"
          >
            <Disc3 class="size-10 opacity-40" />
            <p class="text-sm">
              {searchQuery.trim() ? `No ${noun}s match your search.` : `No ${noun}s yet.`}
            </p>
          </div>
        {:else}
          <div
            class="grid grid-cols-2 gap-x-5 gap-y-6 sm:grid-cols-3 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
          >
            {#each filtered as group (group.key)}
              <a
                href={hrefFor(group)}
                class="group focus-visible:ring-ring outline-hidden flex flex-col gap-2 rounded-lg p-1 transition-transform hover:-translate-y-0.5 focus-visible:ring-2 focus-visible:ring-offset-2"
                aria-label={`Browse ${group.label}`}
              >
                <Cover
                  artist={group.coverArtist}
                  title={group.coverTitle}
                  coverUrl={group.coverUrl}
                  size={176}
                  interactive
                  caption={false}
                  class="!h-auto !w-full aspect-square"
                />
                <div class="min-w-0 px-0.5">
                  <p class="truncate text-[12.5px] font-medium">{group.label}</p>
                  <p class="text-muted-foreground truncate text-[11.5px]">
                    <Music class="-mt-0.5 mr-0.5 inline size-3" />{group.albumCount} album{group.albumCount ===
                    1
                      ? ''
                      : 's'} · {group.trackCount} track{group.trackCount === 1 ? '' : 's'}
                  </p>
                </div>
              </a>
            {/each}
          </div>

          <div class="text-muted-foreground mt-6 text-center text-[11px]">
            {filtered.length.toLocaleString()}
            {noun}{filtered.length === 1 ? '' : 's'}
          </div>
        {/if}
      </div>
    </ScrollArea>
  {/if}
</div>
