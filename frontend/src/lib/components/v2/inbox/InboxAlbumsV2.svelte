<script lang="ts">
  import { untrack } from 'svelte';
  import { Check, Loader2, RefreshCw, Disc3, Wand2 } from '@lucide/svelte';
  import {
    fetchSplitAlbums,
    healSplitAlbums,
    fetchAlbumDuplicates,
    mergeAlbums,
    dismissAlbumDuplicates,
    type AlbumSplitGroup,
    type AlbumDuplicatePair
  } from '$lib/api-client';
  import { Button } from '$lib/components/ui/button';

  type Props = { oncount?: (n: number | null) => void };
  const { oncount }: Props = $props();

  let splits = $state<AlbumSplitGroup[]>([]);
  let pairs = $state<AlbumDuplicatePair[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let acting = $state(false);
  let actionError = $state<string | null>(null);
  let healSummary = $state<string | null>(null);

  const total = $derived(splits.length + pairs.length);

  // untrack the callback identity — see InboxTagReviewV2 for the loop this avoids.
  $effect(() => {
    const n = loading ? null : total;
    untrack(() => oncount?.(n));
  });

  async function load() {
    try {
      loading = true;
      error = null;
      const [splitRes, pairRes] = await Promise.all([fetchSplitAlbums(), fetchAlbumDuplicates()]);
      splits = splitRes.groups ?? [];
      pairs = pairRes.pairs ?? [];
    } catch (err) {
      error = err instanceof Error ? err.message : 'Failed to load album duplicates';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void load();
  });

  async function run(action: () => Promise<unknown>) {
    if (acting) return;
    try {
      acting = true;
      actionError = null;
      await action();
      await load();
    } catch (err) {
      actionError = err instanceof Error ? err.message : 'Action failed';
    } finally {
      acting = false;
    }
  }

  function healAll() {
    return run(async () => {
      const res = await healSplitAlbums();
      healSummary = `Healed ${res.groupsHealed} group${res.groupsHealed === 1 ? '' : 's'} — ${res.songsCorrected} tracks corrected, ${res.songsRequeued} re-queued for re-tag.`;
    });
  }

  function pairKey(p: AlbumDuplicatePair): string {
    return `${p.artistKey}|${p.albumA}|${p.albumB}`;
  }
</script>

{#if loading}
  <div class="flex flex-1 items-center justify-center p-8">
    <div class="text-muted-foreground flex items-center gap-2 text-sm">
      <Loader2 class="size-5 animate-spin" /> Scanning albums…
    </div>
  </div>
{:else if error}
  <div class="flex flex-1 items-center justify-center p-8">
    <div class="max-w-md text-center">
      <p class="text-destructive mb-3 text-sm">{error}</p>
      <Button onclick={load}>Retry</Button>
    </div>
  </div>
{:else if total === 0}
  <div class="flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center">
    <span class="bg-primary/10 text-primary grid size-12 place-items-center rounded-full">
      <Check class="size-6" />
    </span>
    <div class="text-[15px] font-semibold">No album issues found</div>
    <p class="text-muted-foreground max-w-sm text-[12.5px]">
      Split albums (tracks disagreeing on identity) and near-duplicate titles
      ("The Blueprint 3" vs "Blueprint 3") show up here with one-click fixes.
    </p>
    {#if healSummary}
      <p class="text-muted-foreground text-[12px]">{healSummary}</p>
    {/if}
  </div>
{:else}
  <div class="min-h-0 flex-1 overflow-y-auto px-4 py-4 pb-[calc(1rem_+_var(--mh-content-pad))] sm:px-6">
    <div class="mb-3 flex items-center justify-between gap-2">
      <span class="text-muted-foreground text-[11px]">
        {splits.length} split album{splits.length === 1 ? '' : 's'} · {pairs.length} near-duplicate pair{pairs.length === 1 ? '' : 's'}
      </span>
      <button
        type="button"
        onclick={load}
        title="Refresh"
        class="text-muted-foreground hover:bg-accent hover:text-foreground grid size-7 place-items-center rounded-md transition-colors"
      >
        <RefreshCw class="size-3.5" />
      </button>
    </div>

    {#if actionError}
      <p class="text-destructive mb-3 text-[12px]">{actionError}</p>
    {/if}
    {#if healSummary}
      <p class="text-muted-foreground mb-3 text-[12px]">{healSummary}</p>
    {/if}

    {#if splits.length > 0}
      <div class="border-border bg-card mb-4 rounded-lg border p-4">
        <div class="flex flex-wrap items-center justify-between gap-2">
          <div class="min-w-0">
            <div class="text-[13.5px] font-semibold">
              {splits.length} album{splits.length === 1 ? ' is' : 's are'} split across identities
            </div>
            <p class="text-muted-foreground text-[11.5px]">
              Tracks of one album carrying different release/year/artist tags. One heal pass elects a
              single identity per album and re-tags built files in place.
            </p>
          </div>
          <Button size="sm" class="h-7 shrink-0 px-2.5 text-[12px]" disabled={acting} onclick={healAll}>
            {#if acting}<Loader2 class="mr-1 size-3.5 animate-spin" />{:else}<Wand2 class="mr-1 size-3.5" />{/if}
            Heal all
          </Button>
        </div>
        <div class="divide-border mt-2 divide-y">
          {#each splits as g (`${g.artistKey}|${g.albumKey}`)}
            <div class="flex items-center gap-3 py-2">
              <Disc3 class="text-muted-foreground size-4 shrink-0" />
              <div class="min-w-0 flex-1">
                <div class="truncate text-[12.5px] font-medium">
                  {g.electedIdentity.album ?? g.albumKey}
                </div>
                <div class="text-muted-foreground truncate text-[11px]">
                  {g.electedIdentity.albumArtist ?? g.artistKey}
                  · {g.memberCount} tracks, {g.membersNeedingCorrection} need correction
                  {#if g.distinctFolders.length > 1}
                    · {g.distinctFolders.length} destination folders
                  {/if}
                </div>
              </div>
            </div>
          {/each}
        </div>
      </div>
    {/if}

    {#if pairs.length > 0}
      <div class="text-muted-foreground mb-1.5 text-[12px] font-medium">Near-duplicate album titles</div>
      <div class="space-y-3">
        {#each pairs as p (pairKey(p))}
          <div class="border-border bg-card rounded-lg border p-4">
            <div class="mb-1 flex flex-wrap items-center gap-2">
              <span class="truncate text-[13.5px] font-medium">{p.artistDisplay || p.artistKey}</span>
              <span class="bg-accent text-muted-foreground rounded-sm px-1.5 py-px text-[10px]">
                {p.evidence}{p.fuzzyRatio != null ? ` · ${Math.round(p.fuzzyRatio)}%` : ''}
              </span>
            </div>
            <div class="text-muted-foreground text-[12px]">
              “{p.albumA}” ({p.songCountA}) vs “{p.albumB}” ({p.songCountB})
            </div>
            <div class="mt-2 flex flex-wrap items-center gap-2">
              <Button
                variant="outline"
                size="sm"
                class="h-7 px-2.5 text-[12px]"
                disabled={acting}
                onclick={() => run(() => mergeAlbums(p.artistDisplay || p.artistKey, p.albumA, p.albumB))}
              >
                Keep “{p.albumA}”
              </Button>
              <Button
                variant="outline"
                size="sm"
                class="h-7 px-2.5 text-[12px]"
                disabled={acting}
                onclick={() => run(() => mergeAlbums(p.artistDisplay || p.artistKey, p.albumB, p.albumA))}
              >
                Keep “{p.albumB}”
              </Button>
              <Button
                variant="ghost"
                size="sm"
                class="h-7 px-2.5 text-[12px]"
                disabled={acting}
                onclick={() => run(() => dismissAlbumDuplicates(p.artistDisplay || p.artistKey, p.albumA, p.albumB))}
              >
                Not the same album
              </Button>
            </div>
          </div>
        {/each}
      </div>
    {/if}
  </div>
{/if}
