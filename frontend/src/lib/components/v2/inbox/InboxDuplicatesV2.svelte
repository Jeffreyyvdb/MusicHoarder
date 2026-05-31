<script lang="ts">
  import { untrack } from 'svelte';
  import { Check, Loader2, RefreshCw, Copy, Trash2 } from '@lucide/svelte';
  import {
    fetchDuplicates,
    type DuplicateGroup,
    type DuplicateBest,
    type DuplicateMember
  } from '$lib/api-client';
  import { formatFileSize, formatDuration, formatBitrate } from '$lib/formatters';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { Button } from '$lib/components/ui/button';
  import { cn } from '$lib/utils';

  type Props = { oncount?: (n: number | null) => void };
  const { oncount }: Props = $props();

  let groups = $state<DuplicateGroup[]>([]);
  let selectedIdx = $state(0);
  let loading = $state(true);
  let error = $state<string | null>(null);

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

  const selectedGroup = $derived(groups[selectedIdx] ?? null);

  // The A side is the kept "best" (when the server recorded one); the B side is
  // the first flagged duplicate in the cluster. Extra members are listed below.
  type Side = {
    title: string;
    subtitle: string;
    artist: string;
    bitrate: string;
    size: string;
    duration: string;
    fingerprint: string | null;
    sourcePath: string;
  };

  function bestToSide(b: DuplicateBest): Side {
    return {
      title: b.title || b.fileName,
      subtitle: [b.artist, b.album].filter(Boolean).join(' · '),
      artist: b.artist || 'Unknown',
      bitrate: formatBitrate(b.bitrate, b.extension),
      size: formatFileSize(b.fileSizeBytes),
      duration: '—',
      fingerprint: b.fingerprint ?? null,
      sourcePath: b.sourcePath
    };
  }
  function memberToSide(m: DuplicateMember): Side {
    return {
      title: m.title || m.fileName,
      subtitle: [m.albumArtist ?? m.artist, m.album].filter(Boolean).join(' · '),
      artist: (m.albumArtist ?? m.artist) || 'Unknown',
      bitrate: formatBitrate(m.bitrate, m.extension),
      size: formatFileSize(m.fileSizeBytes),
      duration: formatDuration(m.durationSeconds),
      fingerprint: m.fingerprint ?? null,
      sourcePath: m.sourcePath
    };
  }

  // Pair the kept copy (A) against the first duplicate (B). When the server
  // recorded no "best" we fall back to comparing the two highest-ranked members.
  const sideA = $derived.by<Side | null>(() => {
    if (!selectedGroup) return null;
    if (selectedGroup.best) return bestToSide(selectedGroup.best);
    return selectedGroup.duplicates[0] ? memberToSide(selectedGroup.duplicates[0]) : null;
  });
  const sideB = $derived.by<Side | null>(() => {
    if (!selectedGroup) return null;
    const offset = selectedGroup.best ? 0 : 1;
    const m = selectedGroup.duplicates[offset];
    return m ? memberToSide(m) : null;
  });
  // Remaining cluster members beyond the A/B pair, for a compact list.
  const extras = $derived.by<DuplicateMember[]>(() => {
    if (!selectedGroup) return [];
    const offset = selectedGroup.best ? 1 : 2;
    return selectedGroup.duplicates.slice(offset);
  });

  function groupLabel(g: DuplicateGroup): { title: string; subtitle: string; artist: string } {
    const head = g.best ?? g.duplicates[0];
    const title = (g.best?.title || g.duplicates[0]?.title || head?.fileName) ?? 'Unknown';
    const artist =
      (g.best?.artist || (g.duplicates[0]?.albumArtist ?? g.duplicates[0]?.artist)) ?? 'Unknown';
    return { title, subtitle: artist, artist };
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
    <div class="text-[15px] font-semibold">No duplicates flagged</div>
    <p class="text-muted-foreground max-w-sm text-[12.5px]">
      The fingerprint deduper hasn't flagged any clusters. Files sharing a fingerprint show up here for an A/B comparison.
    </p>
  </div>
{:else}
  <div class="grid min-h-0 flex-1 grid-cols-[320px_1fr] overflow-hidden">
    <!-- List -->
    <aside class="border-border bg-surface-sunken flex min-h-0 flex-col border-r">
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
      <div class="min-h-0 flex-1 overflow-y-auto p-1.5">
        {#each groups as g, i (g.fingerprint ?? i)}
          {@const meta = groupLabel(g)}
          <button
            type="button"
            onclick={() => (selectedIdx = i)}
            class={cn(
              'mb-0.5 flex w-full items-center gap-2.5 rounded-md border-l-2 border-transparent py-2 pr-2.5 pl-2 text-left transition-colors',
              selectedIdx === i ? 'border-l-primary bg-card' : 'hover:bg-accent'
            )}
          >
            <Cover artist={meta.artist} title={meta.title} size={40} corner={6} caption={false} />
            <div class="min-w-0 flex-1">
              <div class="truncate text-[13px] font-medium">{meta.title}</div>
              <div class="text-muted-foreground truncate text-[11.5px]">{meta.subtitle}</div>
            </div>
            <span class="shrink-0 rounded bg-sky-500/15 px-1.5 py-0.5 text-[9px] font-bold tracking-wide text-sky-600 uppercase dark:text-sky-400">
              {g.duplicates.length + (g.best ? 1 : 0)} copies
            </span>
          </button>
        {/each}
      </div>
    </aside>

    <!-- Detail: A/B comparison -->
    {#if selectedGroup}
      <div class="flex min-w-0 flex-col overflow-hidden">
        <div class="border-border flex items-center gap-3 border-b bg-sky-500/5 px-6 py-3">
          <Copy class="size-5 shrink-0 text-sky-600 dark:text-sky-400" />
          <div class="min-w-0 flex-1">
            <div class="text-[14px] font-semibold">Ambiguous duplicate</div>
            <div class="text-muted-foreground truncate font-mono text-[11px]">
              fingerprint {selectedGroup.fingerprint ? selectedGroup.fingerprint.slice(0, 18) + '…' : '(none)'} ·
              {selectedGroup.duplicates.length + (selectedGroup.best ? 1 : 0)} files matched
            </div>
          </div>
        </div>

        <div class="min-h-0 flex-1 space-y-4 overflow-y-auto px-6 py-4">
          <div class="grid gap-3 md:grid-cols-2">
            {#each [{ side: sideA, kept: true }, { side: sideB, kept: false }] as col, ci (ci)}
              {#if col.side}
                {@const s = col.side}
                <div class={cn('rounded-lg border p-4', col.kept ? 'border-primary bg-primary/5' : 'border-border bg-card')}>
                  <div class="mb-2 flex items-center gap-2">
                    <span
                      class={cn(
                        'rounded px-1.5 py-0.5 text-[9px] font-bold tracking-wide uppercase',
                        col.kept ? 'bg-primary/15 text-primary' : 'bg-muted text-muted-foreground'
                      )}
                    >{col.kept ? 'Recommend · keep' : 'Duplicate'}</span>
                  </div>
                  <div class="truncate text-[15px] font-medium">{s.title}</div>
                  <div class="text-muted-foreground truncate text-[12px]">{s.subtitle || '—'}</div>
                  <div class="mt-3 grid grid-cols-2 gap-3">
                    {#each [{ l: 'Bitrate', v: s.bitrate }, { l: 'Size', v: s.size }, { l: 'Duration', v: s.duration }, { l: 'Fingerprint', v: s.fingerprint ? s.fingerprint.slice(0, 12) + '…' : '—' }] as stat (stat.l)}
                      <div>
                        <div class="text-muted-foreground text-[10px] font-semibold tracking-wide uppercase">{stat.l}</div>
                        <div class="font-mono text-[12px]">{stat.v}</div>
                      </div>
                    {/each}
                  </div>
                  <div class="text-muted-foreground mt-3 font-mono text-[10.5px] leading-relaxed break-all">{s.sourcePath}</div>
                </div>
              {:else}
                <div class="border-border text-muted-foreground/70 grid place-items-center rounded-lg border border-dashed p-6 text-[12px]">
                  No second copy recorded for this cluster.
                </div>
              {/if}
            {/each}
          </div>

          {#if extras.length > 0}
            <div>
              <div class="text-muted-foreground mb-1.5 text-[11px] font-semibold tracking-wide uppercase">
                {extras.length} more cop{extras.length === 1 ? 'y' : 'ies'} in this cluster
              </div>
              <div class="border-border divide-border divide-y overflow-hidden rounded-lg border">
                {#each extras as m (m.id)}
                  <div class="flex items-center gap-3 px-3 py-2">
                    <div class="min-w-0 flex-1">
                      <div class="truncate text-[12.5px]">{m.title || m.fileName}</div>
                      <div class="text-muted-foreground truncate font-mono text-[10.5px]">{m.sourcePath}</div>
                    </div>
                    <span class="text-muted-foreground shrink-0 font-mono text-[11px]">{formatBitrate(m.bitrate, m.extension)}</span>
                    <span class="text-muted-foreground shrink-0 font-mono text-[11px]">{formatFileSize(m.fileSizeBytes)}</span>
                  </div>
                {/each}
              </div>
            </div>
          {/if}
        </div>

        <!-- Action bar — resolve isn't a real endpoint yet -->
        <div class="border-border bg-background flex items-center gap-3 border-t px-6 py-3">
          <div class="text-muted-foreground flex-1 text-[11px]">
            The comparison is live, but mutable keep / delete resolution
            <span class="bg-muted ml-1 rounded px-1.5 py-px font-mono text-[9px] tracking-wide uppercase">coming soon</span>
          </div>
          <Button variant="outline" disabled class="gap-1.5" title="No resolve endpoint yet — coming soon">
            <Trash2 class="size-3.5" /> Delete B
          </Button>
          <Button disabled class="gap-1.5" title="No resolve endpoint yet — coming soon">
            <Check class="size-3.5" strokeWidth={2} /> Keep A
          </Button>
        </div>
      </div>
    {/if}
  </div>
{/if}
