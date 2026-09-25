<script lang="ts">
  import { AlertTriangle, Loader2 } from '@lucide/svelte';
  import * as BottomSheet from '$lib/components/ui/bottom-sheet';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import type { SongProvenance } from '$lib/api-client';
  import { formatDate } from '$lib/formatters';
  import { PROVENANCE_ICON, seedSublabel, visibleProvenanceGroups } from '$lib/provenance';

  /**
   * "How this album got here": the tracks grouped by why they are in the library. An album fill
   * leads with the owned tracks it started from, because that — not "album completion did it" — is
   * the answer to "I don't recognise any of these".
   */
  type Props = {
    open?: boolean;
    provenance: SongProvenance | null;
    loading: boolean;
    error: string | null;
    /** Offer the way to album completion's switch (an admin surface). */
    canManage: boolean;
  };
  let { open = $bindable(false), provenance, loading, error, canManage }: Props = $props();

  const groups = $derived(visibleProvenanceGroups(provenance?.groups ?? []));
  const hasFill = $derived(groups.some((g) => g.reason === 'AlbumFill'));
</script>

<!-- No body padding: grouped sections bring their own 16px margins inside a sheet. -->
<BottomSheet.Root bind:open title="How it got here" class="md:max-w-xl">
  {#snippet trailing()}
    <BottomSheet.Action prominent onclick={() => (open = false)}>Done</BottomSheet.Action>
  {/snippet}

  {#if loading && !provenance}
    <div
      class="text-muted-foreground text-subheadline mx-4 flex items-center justify-center gap-2 py-10 md:text-sm"
    >
      <Loader2 class="size-4 animate-spin" aria-hidden="true" />
      Tracing how it got here…
    </div>
  {:else if error}
    <div
      class="bg-card text-muted-foreground text-subheadline mx-4 flex items-center justify-center gap-2 rounded-xl px-4 py-8 md:text-sm"
    >
      <AlertTriangle class="text-warning-text size-4" aria-hidden="true" />
      {error}
    </div>
  {:else if groups.length === 0}
    <div
      class="bg-card text-muted-foreground text-subheadline mx-4 rounded-xl px-4 py-8 text-center md:text-sm"
    >
      Nothing recorded about how these tracks arrived.
    </div>
  {:else}
    <div class="flex flex-col gap-6 pt-2 pb-2">
      {#each groups as group, gi (gi)}
        {#if group.fill && group.fill.seeds.length > 0}
          {@const fill = group.fill}
          <GroupedList.Section header="Started from" footer={group.explanation}>
            {#each fill.seeds as seed (seed.songId)}
              <GroupedList.Row
                icon={PROVENANCE_ICON[seed.reason]}
                label={seed.title}
                sublabel={seedSublabel(seed, fill.album)}
                value={seed.atUtc ? formatDate(seed.atUtc) : undefined}
                title={seed.atUtc ? `${seed.atLabel} ${formatDate(seed.atUtc)}` : undefined}
              />
            {/each}
            {#if fill.seedCount > fill.seeds.length}
              <GroupedList.Row
                label="and {fill.seedCount - fill.seeds.length} more you already had"
                class="text-muted-foreground"
              />
            {/if}
          </GroupedList.Section>
        {/if}
        <GroupedList.Section
          header="{group.label} · {group.tracks.length}"
          footer={group.fill && group.fill.seeds.length > 0 ? undefined : group.explanation}
        >
          {#each group.tracks as track (track.songId)}
            <GroupedList.Row
              label={track.title}
              value={track.atUtc ? formatDate(track.atUtc) : undefined}
              title={track.atUtc ? `${track.atLabel} ${formatDate(track.atUtc)}` : undefined}
            />
          {/each}
        </GroupedList.Section>
      {/each}

      {#if hasFill && canManage}
        <GroupedList.Section
          footer="Album completion fetches the rest of any album you own a track from. It is switched in the Wishlist’s download settings."
        >
          <GroupedList.Row
            href="/wishlist"
            onclick={() => (open = false)}
            icon={PROVENANCE_ICON.AlbumFill}
            label="Album completion settings"
            chevron
          />
        </GroupedList.Section>
      {/if}
    </div>
  {/if}
</BottomSheet.Root>
