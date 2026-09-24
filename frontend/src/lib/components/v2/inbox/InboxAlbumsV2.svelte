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
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { Button } from '$lib/components/ui/button';
  import * as AlertDialog from '$lib/components/ui/alert-dialog';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { toast } from 'svelte-sonner';
  import InboxDedupHistoryV2 from './InboxDedupHistoryV2.svelte';
  import InboxQueueStates from './InboxQueueStates.svelte';
  import { snapshotDedupKeys, toastWithUndo } from './dedup-undo';

  type Props = { oncount?: (n: number | null) => void };
  const { oncount }: Props = $props();

  let splits = $state<AlbumSplitGroup[]>([]);
  let pairs = $state<AlbumDuplicatePair[]>([]);
  let loading = $state(true);
  let error = $state<string | null>(null);
  // The card that is acting and the error it got — shown in that card, next to the button.
  let actingKey = $state<string | null>(null);
  let actionError = $state<{ key: string; message: string } | null>(null);
  let healSummary = $state<string | null>(null);
  let historyVersion = $state(0);

  const total = $derived(splits.length + pairs.length);
  const HEAL_KEY = 'heal';

  // untrack the callback identity — see InboxTagReviewV2 for the loop this avoids.
  $effect(() => {
    const n = loading ? null : total;
    untrack(() => oncount?.(n));
  });

  async function load(quiet = false) {
    try {
      if (!quiet) loading = true;
      error = null;
      const [splitRes, pairRes] = await Promise.all([fetchSplitAlbums(), fetchAlbumDuplicates()]);
      splits = splitRes.groups ?? [];
      pairs = pairRes.pairs ?? [];
    } catch (err) {
      if (quiet) toast.error(err instanceof Error ? err.message : 'Failed to reload albums');
      else error = err instanceof Error ? err.message : 'Failed to load album duplicates';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void load();
  });

  function reloadAll() {
    historyVersion += 1;
    void load(true);
  }

  async function run(key: string, action: () => Promise<void>) {
    if (actingKey) return;
    try {
      actingKey = key;
      actionError = null;
      await action();
      reloadAll();
    } catch (err) {
      actionError = { key, message: err instanceof Error ? err.message : 'Action failed' };
    } finally {
      actingKey = null;
    }
  }

  // A heal re-tags every split album library-wide and has no Undo (heals converge on their own,
  // so the log marks them not revertible), so it asks first.
  let confirmHeal = $state(false);
  const splitTrackTotal = $derived(splits.reduce((n, g) => n + g.membersNeedingCorrection, 0));

  function healAll() {
    return run(HEAL_KEY, async () => {
      const res = await healSplitAlbums();
      healSummary = `Healed ${res.groupsHealed} group${res.groupsHealed === 1 ? '' : 's'} — ${res.songsCorrected} tracks corrected, ${res.songsRequeued} re-queued for re-tag.`;
      // Heals converge on their own and are not revertible, so no Undo here.
      toast.success(healSummary);
    });
  }

  function artistOf(p: AlbumDuplicatePair): string {
    return p.artistDisplay || p.artistKey;
  }

  function keepAlbum(p: AlbumDuplicatePair, keep: string, merge: string) {
    return run(pairKey(p), async () => {
      const before = await snapshotDedupKeys();
      await mergeAlbums(artistOf(p), keep, merge);
      await toastWithUndo(`Kept “${keep}”`, 'album-merge', before, reloadAll);
    });
  }

  function notSame(p: AlbumDuplicatePair) {
    return run(pairKey(p), async () => {
      await dismissAlbumDuplicates(artistOf(p), p.albumA, p.albumB);
      toast.success('Marked as different albums');
    });
  }

  function pairKey(p: AlbumDuplicatePair): string {
    return `${p.artistKey}|${p.albumA}|${p.albumB}`;
  }

  const meta = $derived(
    loading
      ? undefined
      : `${splits.length} split album${splits.length === 1 ? '' : 's'} · ${pairs.length} near-duplicate pair${pairs.length === 1 ? '' : 's'}`
  );
</script>

{#snippet refreshAction()}
  <Button
    variant="ghost"
    size="icon"
    aria-label="Refresh album issues"
    title="Refresh"
    onclick={() => void load()}
  >
    <RefreshCw />
  </Button>
{/snippet}

{#snippet cardError(key: string)}
  {#if actionError?.key === key}
    <span role="alert" class="text-destructive-text mt-1 block">{actionError.message}</span>
  {/if}
{/snippet}

{#snippet actionLabel(key: string, text: string, primary = false)}
  <span
    class="text-body flex min-w-0 items-center gap-2 md:text-sm {primary
      ? 'text-primary'
      : 'text-foreground'}"
  >
    {#if actingKey === key}<Loader2 class="size-4 shrink-0 animate-spin" />{/if}
    <span class="min-w-0 break-words">{text}</span>
  </span>
{/snippet}

<!-- A single-level list of decision cards at every width (no push), like Artist names. -->
<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <div class="min-h-0 flex-1 overflow-y-auto overscroll-contain pb-(--mh-content-pad)">
    <PageToolbarV2 title="Album names" {meta} grouped actions={refreshAction} />

    <div class="mx-auto flex w-full max-w-3xl flex-col gap-7 pt-2 pb-8 md:px-6 md:pt-6">
      {#if loading}
        <div
          role="status"
          class="text-body text-muted-foreground flex items-center justify-center gap-2 py-16 md:text-sm"
        >
          <Loader2 class="size-5 animate-spin" /> Scanning albums…
        </div>
      {:else if error}
        <InboxQueueStates state="error" message={error} onretry={load} />
      {:else if total === 0}
        <InboxQueueStates state="empty" icon={Check} title="No album issues found">
          Split albums (tracks disagreeing on identity) and near-duplicate titles ("The Blueprint 3"
          vs "Blueprint 3") show up here with one-step fixes.
          {#if healSummary}<span class="mt-2 block">{healSummary}</span>{/if}
        </InboxQueueStates>
      {:else}
        {#if splits.length > 0}
          <GroupedList.Section
            header="{splits.length} album{splits.length === 1
              ? ' is'
              : 's are'} split across identities"
          >
            {#each splits as g (`${g.artistKey}|${g.albumKey}`)}
              <GroupedList.Row
                icon={Disc3}
                label={g.electedIdentity.album ?? g.albumKey}
                sublabel="{g.electedIdentity.albumArtist ??
                  g.artistKey} · {g.memberCount} tracks, {g.membersNeedingCorrection} need correction{g
                  .distinctFolders.length > 1
                  ? ` · ${g.distinctFolders.length} destination folders`
                  : ''}"
              />
            {/each}
            <!-- An action row: the tint is on the label, the way a UITableView action row is
                 drawn; a filled tile would read as a status. -->
            <GroupedList.Row disabled={actingKey != null} onclick={() => (confirmHeal = true)}>
              <span class="text-body text-primary flex min-w-0 items-center gap-2 md:text-sm">
                {#if actingKey === HEAL_KEY}<Loader2
                    class="size-4 shrink-0 animate-spin"
                  />{:else}<Wand2 class="size-4 shrink-0" aria-hidden="true" />{/if}
                Heal all
              </span>
            </GroupedList.Row>
            {#snippet footer()}
              Tracks of one album carrying different release/year/artist tags. One heal pass elects
              a single identity per album and re-tags built files in place.
              {#if healSummary}<span class="mt-1 block">{healSummary}</span>{/if}
              {@render cardError(HEAL_KEY)}
            {/snippet}
          </GroupedList.Section>
        {/if}

        {#each pairs as p (pairKey(p))}
          {@const key = pairKey(p)}
          <GroupedList.Section
            header="{artistOf(p)} · {p.evidence}{p.fuzzyRatio != null
              ? ` · ${Math.round(p.fuzzyRatio)}%`
              : ''}"
          >
            <GroupedList.Row
              disabled={actingKey != null}
              value="{p.songCountA} track{p.songCountA === 1 ? '' : 's'}"
              onclick={() => keepAlbum(p, p.albumA, p.albumB)}
            >
              {@render actionLabel(key, `Keep “${p.albumA}”`, true)}
            </GroupedList.Row>
            <GroupedList.Row
              disabled={actingKey != null}
              value="{p.songCountB} track{p.songCountB === 1 ? '' : 's'}"
              onclick={() => keepAlbum(p, p.albumB, p.albumA)}
            >
              {@render actionLabel(key, `Keep “${p.albumB}”`, true)}
            </GroupedList.Row>
            <GroupedList.Row
              label="Not the same album"
              disabled={actingKey != null}
              onclick={() => notSame(p)}
            />
            {#snippet footer()}
              Keeping one title moves the other album’s tracks onto it and re-tags built files in
              place.
              {@render cardError(key)}
            {/snippet}
          </GroupedList.Section>
        {/each}
      {/if}

      {#if !loading}
        <InboxDedupHistoryV2 refresh={historyVersion} onreverted={() => void load(true)} />
      {/if}
    </div>
  </div>
</div>

<AlertDialog.Root bind:open={confirmHeal}>
  <AlertDialog.Content>
    <AlertDialog.Header>
      <AlertDialog.Title
        >Heal {splits.length} split album{splits.length === 1 ? '' : 's'}?</AlertDialog.Title
      >
      <AlertDialog.Description>
        Each album gets one identity, and {splitTrackTotal.toLocaleString()}
        {splitTrackTotal === 1 ? 'track is' : 'tracks are'} re-tagged to match it, built files in place.
        A heal can’t be reverted from Recent dedup actions.
      </AlertDialog.Description>
    </AlertDialog.Header>
    <AlertDialog.Footer>
      <AlertDialog.Cancel>Cancel</AlertDialog.Cancel>
      <AlertDialog.Action
        onclick={() => {
          confirmHeal = false;
          void healAll();
        }}>Heal all</AlertDialog.Action
      >
    </AlertDialog.Footer>
  </AlertDialog.Content>
</AlertDialog.Root>
