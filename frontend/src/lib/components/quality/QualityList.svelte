<script lang="ts">
  import type { QualitySongRow } from '$lib/api-client';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { VERDICT_DOT, verdictBadge } from '$lib/quality-ui';
  import { cleanDisplayName } from '$lib/formatters';
  import { cn } from '$lib/utils';
  import { Check, Loader2, TriangleAlert, EarOff, History } from '@lucide/svelte';

  type Props = {
    songs: QualitySongRow[];
    selectedId: number | null;
    onSelect: (id: number) => void;
    loading?: boolean;
    /** Bucket total when more rows exist than were loaded (the list is capped, worst-first). */
    total?: number | null;
  };

  const { songs, selectedId, onSelect, loading = false, total = null }: Props = $props();

  const capped = $derived(total != null && total > songs.length);

  const rowTitle = (s: QualitySongRow) => s.title?.trim() || cleanDisplayName(s.fileName);
</script>

<aside class="bg-surface-sunken border-border flex h-full min-h-0 flex-col rounded-lg border">
  <div class="border-border text-muted-foreground flex shrink-0 items-center justify-between border-b px-3.5 py-2.5 font-mono text-[11px]">
    <span>
      {songs.length.toLocaleString()}{#if capped}<span class="text-muted-foreground/60"> of {total!.toLocaleString()}</span>{/if}
      {songs.length === 1 && !capped ? 'track' : 'tracks'}
    </span>
    <span>worst first</span>
  </div>

  <div class="min-h-0 flex-1 space-y-0.5 overflow-y-auto p-1.5">
    {#if loading && songs.length === 0}
      <div class="text-muted-foreground flex h-32 items-center justify-center gap-2 text-[12px]">
        <Loader2 class="size-3.5 animate-spin" /> Loading…
      </div>
    {:else if songs.length === 0}
      <div class="text-muted-foreground flex h-40 flex-col items-center justify-center gap-2 px-4 text-center">
        <Check class="text-muted-foreground/50 size-6" />
        <div class="text-[12.5px] font-medium">Nothing to show</div>
        <div class="text-[11.5px]">No tracks in this bucket.</div>
      </div>
    {/if}

    {#each songs as s (s.songId)}
      {@const active = s.songId === selectedId}
      <button
        type="button"
        onclick={() => onSelect(s.songId)}
        class={cn(
          'grid w-full grid-cols-[36px_1fr] items-start gap-2.5 rounded-md border border-l-[3px] border-transparent p-2 text-left transition-colors',
          active ? 'bg-accent border-l-primary' : 'hover:bg-muted/60',
          !active && s.bucket === 'flagged' && 'border-l-amber-500/70',
          !active && s.bucket === 'silent' && 'border-l-red-500/70'
        )}
      >
        <Cover artist={s.artist ?? 'Unknown'} title={rowTitle(s)} size={36} corner={4} caption={false} />
        <div class="min-w-0">
          <div class="truncate text-[12.5px] font-medium">{rowTitle(s)}</div>
          <div class="text-muted-foreground truncate text-[11px]">
            {s.artist ?? 'Unknown artist'}{#if s.album}<span class="text-muted-foreground/60"> · </span><em>{s.album}</em>{/if}
          </div>
          <div class="mt-1 flex flex-wrap items-center gap-1.5">
            {#if s.bucket === 'flagged'}
              <span class="inline-flex items-center gap-1 rounded bg-amber-500/15 px-1.5 py-px text-[9.5px] font-semibold tracking-wide text-amber-600 uppercase dark:text-amber-400">
                <TriangleAlert class="size-2.5" /> flagged
              </span>
            {:else if s.bucket === 'silent'}
              <span class="inline-flex items-center gap-1 rounded bg-red-500/15 px-1.5 py-px text-[9.5px] font-semibold tracking-wide text-red-600 uppercase dark:text-red-400">
                <EarOff class="size-2.5" /> silent failure
              </span>
            {:else if s.bucket === 'verified'}
              <span class="border-border text-muted-foreground inline-flex items-center rounded border px-1.5 py-px text-[9.5px] font-semibold tracking-wide uppercase">
                algo + AI agree
              </span>
            {/if}

            <span class={cn('inline-flex items-center gap-1 rounded-full border px-1.5 py-px text-[10.5px] font-semibold', verdictBadge(s.verdict))}>
              <span class={cn('size-[5px] rounded-full', VERDICT_DOT[s.verdict])}></span>
              <span class="font-mono tabular-nums">{s.score}</span>
            </span>

            {#if s.issues.length > 0}
              <span class="bg-background text-muted-foreground border-border rounded border px-1 py-px font-mono text-[9.5px]">{s.issues[0].code}</span>
            {/if}

            {#if s.isOutdated}
              <span
                title="Graded with an older prompt or model — re-grade to refresh."
                class="inline-flex items-center gap-1 rounded bg-amber-500/15 px-1.5 py-px text-[9.5px] font-semibold tracking-wide text-amber-600 uppercase dark:text-amber-400"
              >
                <History class="size-2.5" /> outdated
              </span>
            {/if}
          </div>
        </div>
      </button>
    {/each}
  </div>
</aside>
