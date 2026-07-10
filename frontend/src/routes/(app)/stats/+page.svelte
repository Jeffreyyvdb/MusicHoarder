<script lang="ts">
  import {
    Disc3,
    Heart,
    Image as ImageIcon,
    Library,
    Mic2,
    RefreshCw,
    Clock,
    Users,
    Music,
    Copy,
    CheckCircle2,
    BadgeCheck
  } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Button } from '$lib/components/ui/button';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import { fetchInsights, type LibraryInsights } from '$lib/api-client';

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

  // Distinct accent per segment in the enrichment distribution bar.
  const ENRICH_COLORS: Record<string, string> = {
    Matched: 'bg-emerald-500',
    'Needs review': 'bg-amber-500',
    Failed: 'bg-rose-500',
    Pending: 'bg-muted-foreground/40'
  };
  function enrichColor(status: string): string {
    return ENRICH_COLORS[status] ?? 'bg-primary';
  }
</script>

{#snippet ring(pct: number, label: string, value: string, color: string)}
  <div class="flex flex-col items-center gap-2">
    <div class="relative size-[88px]">
      <svg viewBox="0 0 36 36" class="size-[88px] -rotate-90">
        <circle cx="18" cy="18" r="15.915" fill="none" class="text-muted-foreground/15 stroke-current" stroke-width="3.2" />
        <circle
          cx="18"
          cy="18"
          r="15.915"
          fill="none"
          class="{color} stroke-current transition-[stroke-dasharray] duration-700"
          stroke-width="3.2"
          stroke-linecap="round"
          stroke-dasharray="{Math.min(100, Math.max(0, pct))} {100 - Math.min(100, Math.max(0, pct))}"
        />
      </svg>
      <div class="absolute inset-0 grid place-items-center">
        <span class="text-[15px] font-semibold tabular-nums">{Math.round(pct)}%</span>
      </div>
    </div>
    <div class="text-center">
      <div class="text-[12px] font-medium">{label}</div>
      <div class="text-muted-foreground text-[11px] tabular-nums">{value}</div>
    </div>
  </div>
{/snippet}

{#snippet funnel(stages: { stage: string; count: number; pct: number }[])}
  <div class="space-y-3">
    {#each stages as s, i (s.stage)}
      <div>
        <div class="mb-1 flex items-baseline justify-between text-[12.5px]">
          <span class="font-medium">{s.stage}</span>
          <span class="text-muted-foreground tabular-nums">{fmt(s.count)} · {s.pct}%</span>
        </div>
        <div class="bg-muted h-2.5 overflow-hidden rounded-full">
          <div
            class="bg-primary h-full rounded-full transition-[width] duration-700"
            style="width: {Math.min(100, s.pct)}%; opacity: {1 - i * 0.16}"
          ></div>
        </div>
      </div>
    {/each}
  </div>
{/snippet}

{#snippet statCard(
  icon: typeof Library,
  iconWrap: string,
  label: string,
  value: string,
  sub: string,
  pct: number | null
)}
  {@const Icon = icon}
  <div class="bg-card flex flex-col gap-2.5 rounded-xl border p-5">
    <div class="flex items-center gap-2.5">
      <span class="grid size-8 place-items-center rounded-lg {iconWrap}">
        <Icon class="size-4" />
      </span>
      <span class="text-[13px] font-medium">{label}</span>
    </div>
    <div class="text-[34px] leading-none font-semibold tabular-nums">{value}</div>
    <div class="text-muted-foreground text-[12px]">{sub}</div>
    {#if pct != null}
      <div class="bg-muted mt-0.5 h-1.5 overflow-hidden rounded-full">
        <div class="bg-primary h-full rounded-full transition-[width] duration-700" style="width: {Math.min(100, pct)}%"></div>
      </div>
    {/if}
  </div>
{/snippet}

{#snippet barRow(label: string, sub: string, value: number, max: number, color: string)}
  <div class="flex items-center gap-3">
    <div class="w-32 shrink-0 truncate text-[12.5px] font-medium" title={label}>{label}</div>
    <div class="bg-muted relative h-5 flex-1 overflow-hidden rounded-md">
      <div class="{color} h-full rounded-md transition-[width] duration-700" style="width: {(value / max) * 100}%"></div>
    </div>
    <div class="text-muted-foreground w-14 shrink-0 text-right text-[12px] tabular-nums">{sub}</div>
  </div>
{/snippet}

<div class="flex min-h-0 flex-1 flex-col">
  <header class="border-border flex items-center justify-between gap-4 border-b px-6 py-4">
    <div>
      <h1 class="text-lg font-semibold">Stats</h1>
      <p class="text-muted-foreground text-sm">
        Your hoard at a glance — what the pipeline pulled in, fixed up, and filed away.
      </p>
    </div>
    <Button onclick={load} disabled={loading} variant="outline" size="sm">
      <RefreshCw class="mr-2 size-4 {loading ? 'animate-spin' : ''}" />
      Refresh
    </Button>
  </header>

  <ScrollArea class="min-h-0 flex-1">
    <div class="space-y-8 px-6 py-6">
      {#if error}
        <div class="rounded-md border border-red-500/40 bg-red-500/10 px-4 py-3 text-sm text-red-400">
          {error}
        </div>
      {:else if loading && !data}
        <section class="grid grid-cols-2 gap-4 lg:grid-cols-3 xl:grid-cols-5">
          {#each Array(5) as _, i (i)}
            <div class="bg-card flex flex-col gap-2.5 rounded-xl border p-5">
              <div class="flex items-center gap-2.5">
                <Skeleton class="size-8 rounded-lg" />
                <Skeleton class="h-3.5 w-20" />
              </div>
              <Skeleton class="h-[34px] w-16" />
              <Skeleton class="h-3 w-28" />
            </div>
          {/each}
        </section>
        <section class="mt-8 grid grid-cols-1 gap-4 lg:grid-cols-2">
          {#each Array(2) as _, i (i)}
            <div class="bg-card rounded-xl border p-5">
              <Skeleton class="mb-4 h-4 w-32" />
              <div class="space-y-3">
                {#each Array(3) as _, j (j)}
                  <Skeleton class="h-2.5 w-full rounded-full" />
                {/each}
              </div>
            </div>
          {/each}
        </section>
      {:else if empty}
        <div class="border-border rounded-lg border border-dashed px-6 py-12 text-center">
          <p class="text-sm font-medium">Nothing indexed yet</p>
          <p class="text-muted-foreground mx-auto mt-1 max-w-md text-sm">
            Run a scan and let the pipeline enrich and build your library — this page fills in as songs
            flow through.
          </p>
        </div>
      {:else if data}
        <!-- ── Hero: the five-stat story ── -->
        <section class="grid grid-cols-2 gap-4 lg:grid-cols-3 xl:grid-cols-5">
          {@render statCard(
            Library,
            'bg-primary/12 text-primary',
            'In your library',
            fmt(data.source.inLibrary),
            `of ${fmt(data.source.indexed)} source files · ${data.source.inLibraryPct}%`,
            data.source.inLibraryPct
          )}
          {@render statCard(
            ImageIcon,
            'bg-violet-500/12 text-violet-500',
            'Album covers added',
            fmt(data.covers.albumCoversAdded),
            `${data.covers.coveragePct}% of built tracks show art`,
            data.covers.coveragePct
          )}
          {@render statCard(
            Mic2,
            'bg-sky-500/12 text-sky-500',
            'Lyrics added',
            fmt(data.lyrics.added),
            `${data.lyrics.builtWithLyrics} of ${data.lyrics.builtTracks} built · ${data.lyrics.coveragePct}%`,
            data.lyrics.coveragePct
          )}
          {@render statCard(
            Heart,
            'bg-rose-500/12 text-rose-500',
            'Liked → library',
            fmt(data.wishlist.liked.inLibrary),
            `of ${fmt(data.wishlist.liked.total)} liked songs wishlisted`,
            data.wishlist.liked.total > 0
              ? (data.wishlist.liked.inLibrary / data.wishlist.liked.total) * 100
              : 0
          )}
          {@render statCard(
            Clock,
            'bg-amber-500/12 text-amber-500',
            'Hours of music',
            fmt(Math.round(data.totals.totalHours)),
            `${fmt(data.totals.builtTracks)} tracks · ${data.totals.totalGiB} GiB`,
            null
          )}
        </section>

        <!-- ── Two funnels side by side ── -->
        <section class="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <div class="bg-card rounded-xl border p-5">
            <h2 class="mb-4 flex items-center gap-2 text-sm font-semibold">
              <Disc3 class="text-muted-foreground size-4" /> Pipeline funnel
            </h2>
            {@render funnel(data.funnel)}
            <p class="text-muted-foreground mt-4 text-[11.5px]">
              How far your source files travel: indexed → fingerprinted → matched → written to the
              destination library.
            </p>
          </div>

          <div class="bg-card rounded-xl border p-5">
            <h2 class="mb-4 flex items-center gap-2 text-sm font-semibold">
              <Heart class="size-4 text-rose-500" /> Spotify wishlist journey
            </h2>
            {@render funnel(data.wishlist.funnel)}
            <div class="mt-4 flex flex-wrap gap-1.5">
              {#each data.wishlist.statusBreakdown.filter((s) => s.count > 0) as s (s.status)}
                <span class="bg-muted text-muted-foreground rounded-full px-2.5 py-1 text-[11px]">
                  {s.status} <span class="text-foreground tabular-nums">{fmt(s.count)}</span>
                </span>
              {/each}
            </div>
            <p class="text-muted-foreground mt-3 text-[11.5px]">
              {fmt(data.wishlist.all.total)} tracks wishlisted across {data.wishlist.sources} source{data
                .wishlist.sources === 1
                ? ''
                : 's'}.
            </p>
          </div>
        </section>

        <!-- ── Coverage rings ── -->
        <section class="bg-card rounded-xl border p-5">
          <h2 class="mb-4 flex items-center gap-2 text-sm font-semibold">
            <CheckCircle2 class="text-muted-foreground size-4" /> Metadata coverage
          </h2>
          <div class="grid grid-cols-3 gap-4 sm:grid-cols-6">
            {@render ring(
              data.covers.coveragePct,
              'Cover art',
              `${fmt(data.covers.builtWithCover)}/${fmt(data.covers.builtTracks)}`,
              'text-violet-500'
            )}
            {@render ring(
              data.lyrics.coveragePct,
              'Lyrics',
              `${fmt(data.lyrics.builtWithLyrics)}/${fmt(data.lyrics.builtTracks)}`,
              'text-sky-500'
            )}
            {@render ring(
              data.quality.coverage.fingerprint.pct,
              'Fingerprint',
              fmt(data.quality.coverage.fingerprint.count),
              'text-primary'
            )}
            {@render ring(
              data.quality.coverage.musicBrainz.pct,
              'MusicBrainz',
              fmt(data.quality.coverage.musicBrainz.count),
              'text-amber-500'
            )}
            {@render ring(
              data.quality.coverage.spotify.pct,
              'Spotify ID',
              fmt(data.quality.coverage.spotify.count),
              'text-emerald-500'
            )}
            {@render ring(
              data.quality.coverage.isrc.pct,
              'ISRC',
              fmt(data.quality.coverage.isrc.count),
              'text-rose-500'
            )}
          </div>
        </section>

        <!-- ── Top artists / albums ── -->
        <section class="grid grid-cols-1 gap-4 lg:grid-cols-2">
          <div class="bg-card rounded-xl border p-5">
            <h2 class="mb-4 flex items-center gap-2 text-sm font-semibold">
              <Users class="text-muted-foreground size-4" /> Top artists
            </h2>
            {#if data.top.artists.length === 0}
              <p class="text-muted-foreground text-sm">No built tracks yet.</p>
            {:else}
              {@const max = maxOf(data.top.artists)}
              <div class="space-y-2">
                {#each data.top.artists as a (a.name)}
                  {@render barRow(a.name, `${fmt(a.tracks)}`, a.tracks, max, 'bg-primary/70')}
                {/each}
              </div>
            {/if}
          </div>

          <div class="bg-card rounded-xl border p-5">
            <h2 class="mb-4 flex items-center gap-2 text-sm font-semibold">
              <Disc3 class="text-muted-foreground size-4" /> Biggest albums
            </h2>
            {#if data.top.albums.length === 0}
              <p class="text-muted-foreground text-sm">No built tracks yet.</p>
            {:else}
              {@const max = maxOf(data.top.albums)}
              <div class="space-y-2">
                {#each data.top.albums as al (al.artist + '—' + al.album)}
                  {@render barRow(al.album, `${fmt(al.tracks)}`, al.tracks, max, 'bg-violet-500/70')}
                {/each}
              </div>
            {/if}
          </div>
        </section>

        <!-- ── Library totals + formats ── -->
        <section class="grid grid-cols-1 gap-4 lg:grid-cols-3">
          <div class="bg-card rounded-xl border p-5 lg:col-span-2">
            <h2 class="mb-4 flex items-center gap-2 text-sm font-semibold">
              <Music class="text-muted-foreground size-4" /> Library totals
            </h2>
            <div class="grid grid-cols-2 gap-4 sm:grid-cols-3">
              <div>
                <div class="text-[22px] font-semibold tabular-nums">{fmt(data.totals.builtTracks)}</div>
                <div class="text-muted-foreground text-[12px]">Tracks</div>
              </div>
              <div>
                <div class="text-[22px] font-semibold tabular-nums">{fmt(data.totals.distinctArtists)}</div>
                <div class="text-muted-foreground text-[12px]">Artists</div>
              </div>
              <div>
                <div class="text-[22px] font-semibold tabular-nums">{fmt(data.totals.distinctAlbums)}</div>
                <div class="text-muted-foreground text-[12px]">Albums</div>
              </div>
              <div>
                <div class="text-[22px] font-semibold tabular-nums">{data.totals.totalHours}</div>
                <div class="text-muted-foreground text-[12px]">Hours</div>
              </div>
              <div>
                <div class="text-[22px] font-semibold tabular-nums">{data.totals.totalGiB}</div>
                <div class="text-muted-foreground text-[12px]">GiB on disk</div>
              </div>
              <div>
                <div class="text-[22px] font-semibold tabular-nums">{fmt(data.totals.duplicates)}</div>
                <div class="text-muted-foreground text-[12px]">Duplicates</div>
              </div>
            </div>
            <p class="text-muted-foreground mt-4 text-[11.5px]">
              Indexed between {fmtDate(data.totals.oldestIndexedUtc)} and {fmtDate(
                data.totals.newestIndexedUtc
              )}.
            </p>
          </div>

          <div class="bg-card rounded-xl border p-5">
            <h2 class="mb-4 text-sm font-semibold">By format</h2>
            {#if data.totals.byFormat.length === 0}
              <p class="text-muted-foreground text-sm">No files indexed.</p>
            {:else}
              {@const max = maxOf(data.totals.byFormat)}
              <div class="space-y-2">
                {#each data.totals.byFormat.slice(0, 6) as f (f.format)}
                  {@render barRow(f.format.toUpperCase(), `${fmt(f.count)}`, f.count, max, 'bg-sky-500/70')}
                {/each}
              </div>
            {/if}
          </div>
        </section>

        <!-- ── Enrichment quality ── -->
        <section class="bg-card rounded-xl border p-5">
          <h2 class="mb-4 flex items-center gap-2 text-sm font-semibold">
            <BadgeCheck class="text-muted-foreground size-4" /> Enrichment quality
          </h2>

          <div class="grid grid-cols-1 gap-6 lg:grid-cols-3">
            <!-- Status distribution -->
            <div>
              <div class="text-muted-foreground mb-2 text-[12px] font-medium">Match status</div>
              <div class="bg-muted mb-2 flex h-3 overflow-hidden rounded-full">
                {#each data.quality.enrichment.filter((s) => s.count > 0) as s (s.status)}
                  <div class={enrichColor(s.status)} style="width: {(s.count / enrichTotal) * 100}%" title="{s.status}: {s.count}"></div>
                {/each}
              </div>
              <div class="space-y-1">
                {#each data.quality.enrichment as s (s.status)}
                  <div class="flex items-center gap-2 text-[12px]">
                    <span class="size-2 rounded-full {enrichColor(s.status)}"></span>
                    <span class="flex-1">{s.status}</span>
                    <span class="text-muted-foreground tabular-nums">{fmt(s.count)}</span>
                  </div>
                {/each}
              </div>
            </div>

            <!-- Confidence buckets -->
            <div>
              <div class="text-muted-foreground mb-2 text-[12px] font-medium">Match confidence</div>
              <div class="space-y-2">
                {#each data.quality.confidence as c (c.bucket)}
                  {@render barRow(c.bucket, `${fmt(c.count)}`, c.count, confMax, 'bg-emerald-500/70')}
                {/each}
              </div>
              <div class="mt-3 flex items-center gap-2 text-[12px]">
                <CheckCircle2 class="size-3.5 text-emerald-500" />
                <span class="flex-1">Manually approved</span>
                <span class="text-muted-foreground tabular-nums">{fmt(data.quality.manualApprovals)}</span>
              </div>
            </div>

            <!-- Provider matches -->
            <div>
              <div class="text-muted-foreground mb-2 text-[12px] font-medium">Matches by provider</div>
              {#if data.quality.byProvider.length === 0}
                <p class="text-muted-foreground text-sm">No provider attempts yet.</p>
              {:else}
                {@const pmax = Math.max(1, ...data.quality.byProvider.map((p) => p.matched))}
                <div class="space-y-2">
                  {#each data.quality.byProvider as p (p.provider)}
                    {@render barRow(p.provider, `${fmt(p.matched)}`, p.matched, pmax, 'bg-primary/70')}
                  {/each}
                </div>
              {/if}
            </div>
          </div>
        </section>

        <p class="text-muted-foreground/70 flex items-center gap-1.5 text-[11px]">
          <Copy class="size-3" />
          Cover & lyrics counts reflect what MusicHoarder wrote to your destination library.
        </p>
      {/if}
    </div>
  </ScrollArea>
</div>
