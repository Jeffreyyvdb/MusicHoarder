<script lang="ts">
  import {
    Disc3,
    Heart,
    Image as ImageIcon,
    Library,
    Mic2,
    RefreshCw,
    CircleCheck,
    TriangleAlert
  } from '@lucide/svelte';
  import type { Component } from 'svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Button } from '$lib/components/ui/button';
  import { Badge } from '$lib/components/ui/badge';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import PageToolbarV2 from '$lib/components/v2/PageToolbarV2.svelte';
  import { fetchInsights, type LibraryInsights } from '$lib/api-client';
  import { cn } from '$lib/utils';

  // Library stats, Health-summary style: a few highlights, then sections of plain rows — a label,
  // a figure and a thin bar in the one accent. Colour means status everywhere else in the app
  // (the warning tone is "needs review", red is "failed"), so the old per-metric rainbow of
  // violet/sky/rose/amber rings is gone; a figure that already appears once is not repeated.

  let data = $state<LibraryInsights | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);

  async function load() {
    loading = true;
    try {
      data = await fetchInsights();
      error = null;
    } catch (e) {
      error = e instanceof Error ? e.message : 'Failed to load stats';
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    void load();
  });

  const empty = $derived(!!data && data.source.indexed === 0);

  // Percentages arrive unrounded (77.63157894736842); every one on screen is a whole number.
  function pctLabel(n: number | null | undefined): string {
    return n == null ? '—' : `${Math.round(n)}%`;
  }

  const headerMeta = $derived(
    data
      ? `${data.source.inLibrary.toLocaleString()} of ${data.source.indexed.toLocaleString()} source files in the library · ${pctLabel(data.source.inLibraryPct)}`
      : undefined
  );

  // Hoisted out of the template because {@const} can't live inside a plain <div>.
  const enrichTotal = $derived(
    data ? Math.max(1, data.quality.enrichment.reduce((s, x) => s + x.count, 0)) : 1
  );
  const confMax = $derived(data ? maxOf(data.quality.confidence) : 1);

  // ── formatters ──────────────────────────────────────────────────────────────
  function fmt(n: number | null | undefined): string {
    return n == null ? '—' : n.toLocaleString();
  }
  function fmtDate(iso: string | null): string {
    if (!iso) return '—';
    return new Date(iso).toLocaleDateString([], { year: 'numeric', month: 'short', day: 'numeric' });
  }
  // Largest count in a list, for scaling horizontal bars (never divide by zero).
  function maxOf(items: { count?: number; tracks?: number }[]): number {
    return Math.max(1, ...items.map((i) => i.count ?? i.tracks ?? 0));
  }

  // The enrichment distribution bar is a status chart, so its colours are the status tokens —
  // the same the pipeline, Inbox and folder bars use — each paired with its word in the legend.
  // Keyed by the status with spaces and case folded away: the API sends the enum name
  // ("NeedsReview"), which the old 'Needs review' key never matched.
  const ENRICH_COLORS: Record<string, string> = {
    matched: 'bg-primary',
    needsreview: 'bg-warning',
    failed: 'bg-destructive',
    pending: 'bg-muted-foreground-dim'
  };
  const statusKey = (status: string) => status.replace(/\s+/g, '').toLowerCase();
  function enrichColor(status: string): string {
    return ENRICH_COLORS[statusKey(status)] ?? 'bg-primary';
  }
  /** "NeedsReview" → "Needs review". */
  function statusLabel(status: string): string {
    const spaced = status.replace(/([a-z])([A-Z])/g, '$1 $2').toLowerCase();
    return spaced.charAt(0).toUpperCase() + spaced.slice(1);
  }

  type Highlight = {
    icon: Component;
    label: string;
    value: string;
    sub: string;
    pct: number | null;
  };

  const highlights = $derived.by<Highlight[]>(() => {
    if (!data) return [];
    const liked = data.wishlist.liked;
    return [
      {
        icon: Library,
        label: 'In your library',
        value: fmt(data.source.inLibrary),
        sub: `of ${fmt(data.source.indexed)} source files · ${pctLabel(data.source.inLibraryPct)}`,
        pct: data.source.inLibraryPct
      },
      {
        icon: ImageIcon,
        label: 'Album covers added',
        value: fmt(data.covers.albumCoversAdded),
        sub: `${pctLabel(data.covers.coveragePct)} of built tracks show art`,
        pct: data.covers.coveragePct
      },
      {
        icon: Mic2,
        label: 'Lyrics added',
        value: fmt(data.lyrics.added),
        sub: `${data.lyrics.builtWithLyrics} of ${data.lyrics.builtTracks} built · ${pctLabel(data.lyrics.coveragePct)}`,
        pct: data.lyrics.coveragePct
      },
      {
        icon: Heart,
        label: 'Liked → library',
        value: fmt(liked.inLibrary),
        sub: `of ${fmt(liked.total)} liked songs wishlisted`,
        pct: liked.total > 0 ? (liked.inLibrary / liked.total) * 100 : 0
      }
    ];
  });
</script>

<!-- A thin determinate bar in the one accent. -->
{#snippet bar(pct: number, cls = 'bg-primary')}
  <span class="bg-muted mt-1.5 mb-0.5 block h-1 w-full overflow-hidden rounded-full" aria-hidden="true">
    <span
      class="{cls} block h-full w-full origin-left rounded-full transition-transform duration-700 ease-out"
      style="transform: scaleX({Math.min(100, Math.max(0, pct)) / 100})"
    ></span>
  </span>
{/snippet}

<!-- A labelled figure with a bar under both: funnels, coverage, top lists, formats. The figure
     sits on the label's line (not centred on the two-line cell) and the bar spans the cell. -->
{#snippet barRow(label: string, value: string, pct: number, cls?: string)}
  <GroupedList.Row>
    <span class="flex items-baseline justify-between gap-3">
      <span class="text-body min-w-0 truncate md:text-sm">{label}</span>
      <span class="text-body text-muted-foreground shrink-0 tabular-nums md:text-sm">{value}</span>
    </span>
    {@render bar(pct, cls)}
  </GroupedList.Row>
{/snippet}

<div class="bg-background-grouped flex min-h-0 flex-1 flex-col">
  <!-- The nav bar is the scroller's first child, so its large title scrolls away under the
       sticky bar (PageToolbarV2's placement rule). -->
  <ScrollArea class="min-h-0 flex-1">
    <PageToolbarV2 title="Stats" meta={headerMeta} grouped>
      {#snippet actions()}
        <Button variant="gray" class="rounded-full" onclick={load} disabled={loading}>
          <RefreshCw class={cn(loading && 'animate-spin')} aria-hidden="true" />
          <!-- The phone bar shows the glyph alone; the word stays its accessible name. -->
          <span class="max-md:sr-only">Refresh</span>
        </Button>
      {/snippet}
    </PageToolbarV2>

    <div class="mx-auto flex w-full max-w-6xl flex-col gap-7 pt-2 pb-8 md:gap-6 md:px-7 md:pt-6">
      {#if error}
        <GroupedList.Section>
          <GroupedList.Row
            icon={TriangleAlert}
            iconClass="bg-destructive/12 text-destructive-text"
            label="Couldn't load stats"
            sublabel={error}
          >
            {#snippet trailing()}
              <Button variant="ghost" class="text-primary hover:text-primary h-11 md:h-8" onclick={load}>
                Retry
              </Button>
            {/snippet}
          </GroupedList.Row>
        </GroupedList.Section>
      {:else if loading && !data}
        <GroupedList.Section>
          {#each Array(4) as _, i (i)}
            <div class="flex items-center gap-3 px-4 py-3">
              <Skeleton class="size-[29px] shrink-0 rounded-[7px]" />
              <div class="min-w-0 flex-1 space-y-1.5">
                <Skeleton class="h-4 w-1/2" />
                <Skeleton class="h-3 w-2/3" />
              </div>
              <Skeleton class="h-6 w-12 shrink-0" />
            </div>
          {/each}
        </GroupedList.Section>
      {:else if empty}
        <div class="mx-auto max-w-md px-6 py-14 text-center">
          <p class="text-headline md:text-sm md:font-medium">Nothing indexed yet</p>
          <p class="text-callout text-muted-foreground mt-1 md:text-sm">
            Run a scan and let the pipeline enrich and build your library — this page fills in as songs
            flow through.
          </p>
        </div>
      {:else if data}
        <!-- ── Highlights ── A phone reads them as rows with the figure trailing; a desktop lays
             the same four out as tiles. -->
        <GroupedList.Section headingLevel={2} header="Highlights" class="md:hidden">
          {#each highlights as h (h.label)}
            <GroupedList.Row icon={h.icon} label={h.label} sublabel={h.sub}>
              {#if h.pct != null}{@render bar(h.pct)}{/if}
              {#snippet trailing()}
                <span class="text-title-3 tabular-nums">{h.value}</span>
              {/snippet}
            </GroupedList.Row>
          {/each}
        </GroupedList.Section>
        <section aria-label="Highlights" class="hidden grid-cols-2 gap-4 md:grid lg:grid-cols-4">
          {#each highlights as h (h.label)}
            {@const Icon = h.icon}
            <div class="bg-card flex flex-col gap-2 rounded-xl p-4">
              <div class="flex items-center gap-2.5">
                <span class="bg-muted text-foreground grid size-[29px] place-items-center rounded-[7px]">
                  <Icon class="size-[18px]" aria-hidden="true" />
                </span>
                <span class="text-[13px] font-medium">{h.label}</span>
              </div>
              <div class="text-[30px] leading-none font-semibold tabular-nums">{h.value}</div>
              <div class="text-muted-foreground text-[12px]">{h.sub}</div>
              {#if h.pct != null}{@render bar(h.pct)}{/if}
            </div>
          {/each}
        </section>

        <!-- The remaining sections flow in balanced columns on a desktop (CSS columns, so a short
             section never leaves a hole beside a tall one). -->
        <div class="flex flex-col gap-7 md:block md:columns-2 md:gap-6 md:*:mb-6 md:*:break-inside-avoid xl:columns-3">
          <GroupedList.Section headingLevel={2}
            header="Pipeline funnel"
            footer="How far your source files travel: indexed → fingerprinted → matched → written to the destination library."
          >
            {#each data.funnel as s (s.stage)}
              {@render barRow(s.stage, `${fmt(s.count)} · ${pctLabel(s.pct)}`, s.pct)}
            {/each}
          </GroupedList.Section>

          <GroupedList.Section headingLevel={2}
            header="Spotify wishlist journey"
            footer={`${fmt(data.wishlist.all.total)} tracks wishlisted across ${data.wishlist.sources} source${data.wishlist.sources === 1 ? '' : 's'}.`}
          >
            {#each data.wishlist.funnel as s (s.stage)}
              {@render barRow(s.stage, `${fmt(s.count)} · ${pctLabel(s.pct)}`, s.pct)}
            {/each}
            {#if data.wishlist.statusBreakdown.some((s) => s.count > 0)}
              <div class="flex flex-wrap gap-1.5 px-4 py-3">
                {#each data.wishlist.statusBreakdown.filter((s) => s.count > 0) as s (s.status)}
                  <Badge variant="secondary" class="text-footnote md:text-[11px]">
                    {s.status} <span class="font-semibold tabular-nums">{fmt(s.count)}</span>
                  </Badge>
                {/each}
              </div>
            {/if}
          </GroupedList.Section>

          <GroupedList.Section headingLevel={2} header="Metadata coverage">
            {@render barRow(
              'Cover art',
              `${fmt(data.covers.builtWithCover)} of ${fmt(data.covers.builtTracks)} · ${pctLabel(data.covers.coveragePct)}`,
              data.covers.coveragePct
            )}
            {@render barRow(
              'Lyrics',
              `${fmt(data.lyrics.builtWithLyrics)} of ${fmt(data.lyrics.builtTracks)} · ${pctLabel(data.lyrics.coveragePct)}`,
              data.lyrics.coveragePct
            )}
            {@render barRow(
              'Fingerprint',
              `${fmt(data.quality.coverage.fingerprint.count)} · ${pctLabel(data.quality.coverage.fingerprint.pct)}`,
              data.quality.coverage.fingerprint.pct
            )}
            {@render barRow(
              'MusicBrainz',
              `${fmt(data.quality.coverage.musicBrainz.count)} · ${pctLabel(data.quality.coverage.musicBrainz.pct)}`,
              data.quality.coverage.musicBrainz.pct
            )}
            {@render barRow(
              'Spotify ID',
              `${fmt(data.quality.coverage.spotify.count)} · ${pctLabel(data.quality.coverage.spotify.pct)}`,
              data.quality.coverage.spotify.pct
            )}
            {@render barRow(
              'ISRC',
              `${fmt(data.quality.coverage.isrc.count)} · ${pctLabel(data.quality.coverage.isrc.pct)}`,
              data.quality.coverage.isrc.pct
            )}
          </GroupedList.Section>

          <GroupedList.Section headingLevel={2} header="Top artists">
            {#if data.top.artists.length === 0}
              <p class="text-body text-muted-foreground px-4 py-4 md:text-sm">No built tracks yet.</p>
            {:else}
              {@const max = maxOf(data.top.artists)}
              {#each data.top.artists as a (a.name)}
                {@render barRow(a.name, fmt(a.tracks), (a.tracks / max) * 100)}
              {/each}
            {/if}
          </GroupedList.Section>

          <GroupedList.Section headingLevel={2} header="Biggest albums">
            {#if data.top.albums.length === 0}
              <p class="text-body text-muted-foreground px-4 py-4 md:text-sm">No built tracks yet.</p>
            {:else}
              {@const max = maxOf(data.top.albums)}
              {#each data.top.albums as al (al.artist + '—' + al.album)}
                {@render barRow(al.album, fmt(al.tracks), (al.tracks / max) * 100)}
              {/each}
            {/if}
          </GroupedList.Section>

          <GroupedList.Section headingLevel={2}
            header="Library totals"
            footer={`Indexed between ${fmtDate(data.totals.oldestIndexedUtc)} and ${fmtDate(data.totals.newestIndexedUtc)}.`}
          >
            <GroupedList.Row label="Tracks" value={fmt(data.totals.builtTracks)} />
            <GroupedList.Row label="Artists" value={fmt(data.totals.distinctArtists)} />
            <GroupedList.Row label="Albums" value={fmt(data.totals.distinctAlbums)} />
            <GroupedList.Row label="Hours of music" value={fmt(Math.round(data.totals.totalHours))} />
            <GroupedList.Row label="On disk" value={`${data.totals.totalGiB} GiB`} />
            <GroupedList.Row label="Duplicates" value={fmt(data.totals.duplicates)} />
          </GroupedList.Section>

          <GroupedList.Section headingLevel={2} header="By format">
            {#if data.totals.byFormat.length === 0}
              <p class="text-body text-muted-foreground px-4 py-4 md:text-sm">No files indexed.</p>
            {:else}
              {@const max = maxOf(data.totals.byFormat)}
              {#each data.totals.byFormat.slice(0, 6) as f (f.format)}
                {@render barRow(f.format.toUpperCase(), fmt(f.count), (f.count / max) * 100)}
              {/each}
            {/if}
          </GroupedList.Section>

          <!-- Enrichment quality: a status chart, so its bar and legend use the status tokens and
               every colour sits beside its word. -->
          <GroupedList.Section headingLevel={2} header="Match status">
            <div class="px-4 pt-3 pb-1">
              <div class="bg-muted flex h-2 gap-px overflow-hidden rounded-full" aria-hidden="true">
                {#each data.quality.enrichment.filter((s) => s.count > 0) as s (s.status)}
                  <div class={enrichColor(s.status)} style="width: {(s.count / enrichTotal) * 100}%"></div>
                {/each}
              </div>
            </div>
            {#each data.quality.enrichment as s (s.status)}
              <GroupedList.Row label={statusLabel(s.status)} value={fmt(s.count)}>
                {#snippet leading()}
                  <span class="flex w-3 justify-center" aria-hidden="true">
                    <span class="size-2 rounded-full {enrichColor(s.status)}"></span>
                  </span>
                {/snippet}
              </GroupedList.Row>
            {/each}
          </GroupedList.Section>

          <GroupedList.Section headingLevel={2} header="Match confidence">
            {#each data.quality.confidence as c (c.bucket)}
              {@render barRow(c.bucket, fmt(c.count), (c.count / confMax) * 100)}
            {/each}
            <GroupedList.Row label="Manually approved" value={fmt(data.quality.manualApprovals)}>
              {#snippet leading()}
                <CircleCheck class="text-primary size-5" aria-hidden="true" />
              {/snippet}
            </GroupedList.Row>
          </GroupedList.Section>

          <GroupedList.Section headingLevel={2} header="Matches by provider">
            {#if data.quality.byProvider.length === 0}
              <p class="text-body text-muted-foreground px-4 py-4 md:text-sm">No provider attempts yet.</p>
            {:else}
              {@const pmax = Math.max(1, ...data.quality.byProvider.map((p) => p.matched))}
              {#each data.quality.byProvider as p (p.provider)}
                {@render barRow(p.provider, fmt(p.matched), (p.matched / pmax) * 100)}
              {/each}
            {/if}
          </GroupedList.Section>
        </div>

        <p class="text-footnote text-muted-foreground flex items-center gap-1.5 px-8 md:px-4 md:text-xs">
          <Disc3 class="size-3.5 shrink-0" aria-hidden="true" />
          Cover and lyrics counts reflect what MusicHoarder wrote to your destination library.
        </p>
      {/if}
    </div>
  </ScrollArea>
</div>
