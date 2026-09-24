<script lang="ts">
  import type { Snippet } from 'svelte';
  import type { QualitySongRow } from '$lib/api-client';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import { Badge } from '$lib/components/ui/badge';
  import { VERDICT_DOT, issueLabel, verdictBadge } from '$lib/quality-ui';
  import { cleanDisplayName } from '$lib/formatters';
  import { coverUrlForSongId } from '$lib/components/v2/inbox/song-cover';
  import { cn } from '$lib/utils';
  import { Check, Loader2, TriangleAlert, EarOff, History } from '@lucide/svelte';

  // The worst-first song list. Two presentations:
  //  - `pane` (the desktop split view): its own scrolling column beside the detail; a row selects.
  //  - `inline` (the phone and tablet drill-down): an inset list inside the page's one scroller;
  //    a row is a link that pushes the detail (`hrefFor`), so Back returns here.
  type Props = {
    songs: QualitySongRow[];
    selectedId: number | null;
    onSelect?: (id: number) => void;
    loading?: boolean;
    /** Bucket total when more rows exist than were loaded (the list is capped, worst-first). */
    total?: number | null;
    layout?: 'pane' | 'inline';
    /** `inline`: the URL a row opens. */
    hrefFor?: (id: number) => string;
    /** `inline`: called just before a row navigates (the page keeps its scroll position). */
    onNavigate?: () => void;
    /** `inline`: the section header (the page's category picker). */
    header?: Snippet;
  };

  const {
    songs,
    selectedId,
    onSelect,
    loading = false,
    total = null,
    layout = 'pane',
    hrefFor,
    onNavigate,
    header
  }: Props = $props();

  const capped = $derived(total != null && total > songs.length);
  const countLine = $derived(
    `${songs.length.toLocaleString()}${capped ? ` of ${total!.toLocaleString()}` : ''} ${songs.length === 1 && !capped ? 'track' : 'tracks'} · worst first`
  );

  const rowTitle = (s: QualitySongRow) => s.title?.trim() || cleanDisplayName(s.fileName);
</script>

<!-- The row's text and status capsules, shared by both presentations. Every capsule pairs its
     colour with a word or a number. -->
{#snippet rowBody(s: QualitySongRow)}
  <span class="text-body truncate md:text-[12.5px] md:font-medium">{rowTitle(s)}</span>
  <span class="text-subheadline text-muted-foreground truncate md:text-[11px]">
    {s.artist ?? 'Unknown artist'}{#if s.album}<span aria-hidden="true" class="mx-1">·</span><span class="sr-only">, </span><em>{s.album}</em>{/if}
  </span>
  <span class="mt-1 flex flex-wrap items-center gap-1.5">
    {#if s.bucket === 'flagged'}
      <Badge variant="warning" class="md:text-[11px]"><TriangleAlert aria-hidden="true" /> Flagged</Badge>
    {:else if s.bucket === 'silent'}
      <Badge variant="destructive" class="md:text-[11px]"><EarOff aria-hidden="true" /> Silent failure</Badge>
    {:else if s.bucket === 'verified'}
      <Badge variant="secondary" class="md:text-[11px]">Algorithm and AI agree</Badge>
    {/if}

    <span
      class={cn(
        'text-caption-1 inline-flex min-h-5 items-center gap-1 rounded-full border px-2 font-semibold md:text-[11px]',
        verdictBadge(s.verdict)
      )}
    >
      <span class={cn('size-1.5 rounded-full', VERDICT_DOT[s.verdict])} aria-hidden="true"></span>
      <span class="tabular-nums">{s.score}</span>
      <span class="sr-only">{s.verdict}</span>
    </span>

    {#if s.issues.length > 0}
      <!-- The words, not the grader's snake_case code (that stays in the hover title). Foreground
           text: 11–12px on a fill is where a muted grey loses too much. -->
      <span
        class="text-caption-1 bg-muted text-foreground rounded-full px-2 py-px md:text-[11px]"
        title={s.issues[0].code}>{issueLabel(s.issues[0].code)}</span
      >
    {/if}

    {#if s.isOutdated}
      <Badge variant="warning" class="md:text-[11px]" title="Graded with an older prompt or model — re-grade to refresh.">
        <History aria-hidden="true" /> Outdated
      </Badge>
    {/if}
  </span>
{/snippet}

{#snippet empty()}
  {#if loading && songs.length === 0}
    <div class="text-muted-foreground text-body flex h-32 items-center justify-center gap-2 md:text-[12px]">
      <Loader2 class="size-4 animate-spin" aria-hidden="true" /> Loading…
    </div>
  {:else if songs.length === 0}
    <div class="text-muted-foreground flex h-40 flex-col items-center justify-center gap-1.5 px-4 text-center">
      <Check class="text-muted-foreground-dim size-6" aria-hidden="true" />
      <div class="text-body text-foreground md:text-[12.5px] md:font-medium">Nothing to show</div>
      <div class="text-subheadline md:text-[11.5px]">No tracks in this bucket.</div>
    </div>
  {/if}
{/snippet}

{#if layout === 'inline'}
  <GroupedList.Section {header} footer={songs.length > 0 ? countLine : undefined}>
    {@render empty()}
    {#each songs as s (s.songId)}
      <GroupedList.Row
        href={hrefFor?.(s.songId)}
        onclick={onNavigate}
        selected={s.songId === selectedId}
        chevron
      >
        {#snippet leading()}
          <Cover
            artist={s.artist ?? 'Unknown'}
            title={rowTitle(s)}
            coverUrl={coverUrlForSongId(s.songId)}
            size={44}
            corner={6}
            caption={false}
            dprCap={3}
          />
        {/snippet}
        {@render rowBody(s)}
      </GroupedList.Row>
    {/each}
  </GroupedList.Section>
{:else}
  <aside class="bg-card flex h-full min-h-0 flex-col overflow-hidden rounded-xl">
    <div class="border-separator text-muted-foreground flex shrink-0 items-center border-b px-3.5 py-2.5 text-[11.5px]">
      {countLine}
    </div>

    <div class="min-h-0 flex-1 space-y-0.5 overflow-y-auto p-1.5">
      {@render empty()}
      {#each songs as s (s.songId)}
        {@const active = s.songId === selectedId}
        <button
          type="button"
          onclick={() => onSelect?.(s.songId)}
          aria-current={active ? 'true' : undefined}
          class={cn(
            'focus-visible:ring-ring/50 grid w-full grid-cols-[36px_1fr] items-start gap-2.5 rounded-lg border-l-[3px] p-2 text-left outline-none transition-colors focus-visible:ring-3',
            active ? 'bg-accent border-l-primary' : 'hover:bg-accent border-l-transparent',
            !active && s.bucket === 'flagged' && 'border-l-warning',
            !active && s.bucket === 'silent' && 'border-l-destructive'
          )}
        >
          <Cover
            artist={s.artist ?? 'Unknown'}
            title={rowTitle(s)}
            coverUrl={coverUrlForSongId(s.songId)}
            size={36}
            corner={4}
            caption={false}
          />
          <span class="flex min-w-0 flex-col">{@render rowBody(s)}</span>
        </button>
      {/each}
    </div>
  </aside>
{/if}
