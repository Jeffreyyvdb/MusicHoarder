<script lang="ts">
  import { page } from '$app/state';
  import { AudioLines, Loader2 } from '@lucide/svelte';
  import Cover from '#lib/components/file-browser/Cover.svelte';
  import ListeningActivity from '#lib/components/listening/ListeningActivity.svelte';
  import ListeningClock from '#lib/components/listening/ListeningClock.svelte';
  import { Button } from '#lib/components/ui/button/index.ts';
  import { EmptyState } from '#lib/components/ui/empty-state/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { Skeleton } from '#lib/components/ui/skeleton/index.ts';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import { activateTrack } from '#lib/components/v2/TrackRowMenu.svelte';
  import {
    albumKeyForSong,
    fetchListeningStats,
    rowCoverUrlForSong,
    toPlayerSong,
    type ApiSong,
    type ListeningRange,
    type ListeningStats
  } from '#lib/api-client.ts';
  import { formatRelativeTime } from '#lib/formatters.ts';
  import { IsMobile } from '#lib/hooks/is-mobile.svelte.ts';
  import {
    activityBuckets,
    dayKeyAt,
    DEFAULT_LISTENING_RANGE,
    formatDayKey,
    formatHour,
    formatHours,
    groupRecentByDay,
    LISTENING_RANGES,
    parseListeningRange,
    streamsLabel
  } from '#lib/listening.ts';
  import { replaceUrl } from '#lib/navigation/replace-url.ts';
  import { playerStore } from '#lib/stores/player.svelte.ts';
  import { songsStore } from '#lib/stores/songs.svelte.ts';
  import { tabMemory } from '#lib/stores/tab-memory.svelte.ts';

  // Listen → Listening: the account's own play history as stats, the way stats.fm's profile shows
  // it. Everything comes from one request per range; the lists are capped by the server (10 tops,
  // 12 genres, 50 recent), so nothing here needs virtualising. Covers and playback join the
  // streams against the library this client already holds: a song that has since left it still
  // counts, it just cannot be played from here.

  const compact = new IsMobile();
  const offsetMinutes = -new Date().getTimezoneOffset();

  const range = $derived(parseListeningRange(page.url.searchParams.get('range')));

  $effect(() => {
    if (tabMemory.titleOf(page.url) !== 'Listening') tabMemory.setTitle(page.url, 'Listening');
  });
  $effect(() => {
    songsStore.revalidate();
  });

  let stats = $state<ListeningStats | null>(null);
  let loadedRange = $state<ListeningRange | null>(null);
  let error = $state<string | null>(null);
  let request = 0;

  async function load(target: ListeningRange) {
    const mine = ++request;
    error = null;
    try {
      const next = await fetchListeningStats(target, offsetMinutes);
      if (mine !== request) return;
      stats = next;
      loadedRange = target;
    } catch (err) {
      if (mine !== request) return;
      error = err instanceof Error ? err.message : 'Could not load your listening stats';
    }
  }

  $effect(() => {
    void load(range);
  });

  const busy = $derived(loadedRange !== range && !error);
  const rangeInfo = $derived(LISTENING_RANGES.find((r) => r.value === range)!);

  function selectRange(value: string) {
    const next = parseListeningRange(value);
    void replaceUrl(next === DEFAULT_LISTENING_RANGE ? '/listening' : `/listening?range=${next}`);
  }

  const songs = $derived(songsStore.songsById);
  const isEmpty = $derived(
    stats !== null && stats.totals.streams === 0 && stats.recent.length === 0
  );
  const meta = $derived(
    stats && !isEmpty
      ? `${stats.totals.streams.toLocaleString()} streams · ${rangeInfo.long}`
      : undefined
  );

  const todayKey = $derived(dayKeyAt(new Date().toISOString(), offsetMinutes));
  const buckets = $derived(
    stats ? activityBuckets(stats.days, stats.range, todayKey, stats.days[0]?.date) : []
  );
  const bucketUnit = $derived(
    range === '4w'
      ? 'day'
      : range === '6m'
        ? 'week'
        : range === '1y' || buckets.length > 6
          ? 'month'
          : 'year'
  );
  const recentGroups = $derived(stats ? groupRecentByDay(stats.recent, offsetMinutes) : []);

  function coverOf(songId: number): string | null {
    const song = songs.get(songId);
    return song ? rowCoverUrlForSong(song) : null;
  }

  function fallbackArtist(s: ApiSong): string {
    return (s.albumArtist ?? s.artist ?? '').trim() || 'Unknown Artist';
  }

  /** Plays the library's copies of `ids` from `from`, the tap rule on a phone and a plain play at md+. */
  function playFrom(ids: number[], from: number) {
    const playable = ids.map((id) => songs.get(id)).filter((s): s is ApiSong => s !== undefined);
    const target = songs.get(ids[from]);
    if (!target) return;
    const start = () =>
      void playerStore.startQueue(
        playable.map((s) => toPlayerSong(s, fallbackArtist(s))),
        playable.indexOf(target),
        { source: { label: 'Listening', href: '/listening' } }
      );
    if (compact.current) activateTrack(target.id, start);
    else start();
  }

  function albumHref(songId: number): string | null {
    const song = songs.get(songId);
    return song ? `/library?album=${encodeURIComponent(albumKeyForSong(song))}` : null;
  }

  const topTrackIds = $derived(stats?.topTracks.map((t) => t.songId) ?? []);
  const recentIds = $derived(stats?.recent.map((r) => r.songId) ?? []);

  const tiles = $derived(
    stats
      ? [
          { label: 'Streams', value: stats.totals.streams.toLocaleString() },
          { label: 'Minutes', value: stats.totals.minutes.toLocaleString() },
          { label: 'Hours', value: formatHours(stats.totals.minutes) },
          { label: 'Different tracks', value: stats.totals.distinctTracks.toLocaleString() },
          { label: 'Different artists', value: stats.totals.distinctArtists.toLocaleString() },
          { label: 'Different albums', value: stats.totals.distinctAlbums.toLocaleString() }
        ]
      : []
  );

  const facts = $derived.by(() => {
    if (!stats) return [];
    const f = stats.facts;
    const out: { label: string; value: string; detail?: string }[] = [];
    if (f.longestStreak) {
      const { days, start, end } = f.longestStreak;
      out.push({
        label: 'Longest streak',
        value: `${days} ${days === 1 ? 'day' : 'days'}`,
        detail: days > 1 ? `${formatDayKey(start)} to ${formatDayKey(end)}` : formatDayKey(start)
      });
    }
    if (f.currentStreakDays > 0) {
      out.push({
        label: 'Current streak',
        value: `${f.currentStreakDays} ${f.currentStreakDays === 1 ? 'day' : 'days'}`
      });
    }
    if (f.busiestDay) {
      out.push({
        label: 'Busiest day',
        value: `${f.busiestDay.streams.toLocaleString()} streams`,
        detail: formatDayKey(f.busiestDay.date)
      });
    }
    if (f.mostRepeated) {
      const r = f.mostRepeated;
      out.push({
        label: 'On repeat',
        value: `${r.streams} plays in a day`,
        detail: `${r.title}${r.artist ? ` · ${r.artist}` : ''} · ${formatDayKey(r.date)}`
      });
    }
    if (f.nightOwlShare !== undefined) {
      out.push({
        label: 'Night owl',
        value: `${Math.round(f.nightOwlShare * 100)}%`,
        detail: 'of streams between midnight and 5 am'
      });
    }
    if (f.peakHour !== undefined) {
      out.push({ label: 'Peak hour', value: formatHour(f.peakHour) });
    }
    return out;
  });
</script>

{#snippet sectionTitle(id: string, title: string)}
  <h2 {id} class="text-title-2 mb-3 md:text-lg">{title}</h2>
{/snippet}

{#snippet trackRow(
  songId: number,
  title: string,
  line: string,
  trailing: string,
  onclick: () => void
)}
  {@const playable = songs.has(songId)}
  {#if playable}
    <button
      type="button"
      class="focus-visible:ring-ring active:bg-accent flex min-h-14 w-full items-center gap-3 rounded-lg text-left focus-visible:ring-2 focus-visible:outline-hidden md:min-h-12"
      {onclick}
    >
      {@render rowBody(songId, title, line, trailing)}
    </button>
  {:else}
    <div class="flex min-h-14 w-full items-center gap-3 md:min-h-12">
      {@render rowBody(songId, title, line, trailing)}
    </div>
  {/if}
{/snippet}

{#snippet rowBody(songId: number, title: string, line: string, trailing: string)}
  <Cover
    artist={line}
    {title}
    coverUrl={coverOf(songId)}
    size={44}
    caption={false}
    class="!size-11 shrink-0 !rounded-md"
  />
  <span class="min-w-0 flex-1">
    <span class="text-body block truncate md:text-sm">{title}</span>
    <span class="text-footnote text-muted-foreground block truncate md:text-xs">{line}</span>
  </span>
  {#if trailing}
    <span class="text-footnote text-muted-foreground shrink-0 text-right tabular-nums md:text-xs"
      >{trailing}</span
    >
  {/if}
{/snippet}

<ScrollArea class="min-h-0 flex-1">
  <PageToolbarV2
    title="Listening"
    {meta}
    metaFrom="lg"
    tabs={LISTENING_RANGES.map((r) => ({ id: r.value, label: r.label }))}
    activeTab={range}
    onselectTab={selectRange}
  />

  <div class="flex flex-col gap-9 px-4 pt-2 pb-6 md:px-7 md:pt-4" aria-busy={busy}>
    {#if error && !stats}
      <div class="flex flex-col items-center gap-3 px-6 py-16 text-center">
        <p class="text-destructive-text text-subheadline md:text-sm">{error}</p>
        <Button variant="gray" class="rounded-full" onclick={() => void load(range)}>Retry</Button>
      </div>
    {:else if !stats}
      <div class="grid grid-cols-2 gap-3 md:grid-cols-3" role="status" aria-label="Loading">
        {#each Array(6) as _, i (i)}
          <Skeleton class="h-24 rounded-xl" />
        {/each}
      </div>
      <Skeleton class="h-48 rounded-xl" />
      <Skeleton class="h-64 rounded-xl" />
    {:else if isEmpty}
      <EmptyState
        icon={AudioLines}
        title="No streams recorded yet"
        hint="MusicHoarder keeps a history of what you play from now on. Play a few tracks and your stats show up here."
      />
    {:else}
      {#if busy}
        <p class="text-footnote text-muted-foreground flex items-center gap-2" role="status">
          <Loader2 class="size-4 animate-spin" aria-hidden="true" /> Updating…
        </p>
      {/if}

      <section aria-label="Totals">
        <dl class="grid grid-cols-2 gap-3 md:grid-cols-3">
          {#each tiles as tile (tile.label)}
            <div class="bg-secondary flex flex-col-reverse gap-1 rounded-xl px-4 py-3.5">
              <dt class="text-footnote text-muted-foreground">{tile.label}</dt>
              <dd class="text-title-1 tabular-nums">{tile.value}</dd>
            </div>
          {/each}
        </dl>
        {#if stats.trackingSinceUtc}
          <p class="text-footnote text-muted-foreground mt-2.5 px-1">
            Tracking since {formatDayKey(dayKeyAt(stats.trackingSinceUtc, offsetMinutes))}
          </p>
        {/if}
      </section>

      {#if stats.totals.streams === 0}
        <p class="text-subheadline text-muted-foreground px-1">
          No streams in {rangeInfo.long}. Your latest ones are at the bottom.
        </p>
      {/if}

      {#if stats.topGenres.length > 0}
        <section aria-labelledby="listening-genres">
          {@render sectionTitle('listening-genres', 'Top genres')}
          <ul class="flex flex-wrap gap-2">
            {#each stats.topGenres as genre (genre.name)}
              <li
                class="bg-secondary text-footnote flex min-h-8 items-center gap-1.5 rounded-full px-3 md:text-xs"
              >
                <span>{genre.name}</span>
                <span class="text-muted-foreground tabular-nums"
                  >{genre.streams.toLocaleString()}</span
                >
              </li>
            {/each}
          </ul>
        </section>
      {/if}

      {#if stats.topTracks.length > 0}
        <section aria-labelledby="listening-tracks">
          {@render sectionTitle('listening-tracks', 'Top tracks')}
          <ol class="flex flex-col">
            {#each stats.topTracks as track, i (track.songId)}
              <li class="flex items-center gap-2">
                <span
                  class="text-footnote text-muted-foreground w-6 shrink-0 text-center tabular-nums"
                  >{i + 1}</span
                >
                <div class="min-w-0 flex-1">
                  {@render trackRow(
                    track.songId,
                    track.title,
                    [track.artist, track.album].filter(Boolean).join(' · '),
                    streamsLabel(track.streams, track.minutes),
                    () => playFrom(topTrackIds, i)
                  )}
                </div>
              </li>
            {/each}
          </ol>
        </section>
      {/if}

      {#if stats.topArtists.length > 0}
        <section aria-labelledby="listening-artists">
          {@render sectionTitle('listening-artists', 'Top artists')}
          <ol
            data-scroll-x=""
            class="no-scrollbar -mx-4 flex snap-x scroll-px-4 gap-4 overflow-x-auto overflow-y-hidden px-4 pb-1 md:-mx-7 md:scroll-px-7 md:px-7"
          >
            {#each stats.topArtists as artist, i (artist.name)}
              <li class="w-[120px] shrink-0 snap-start md:w-[136px]">
                <a
                  href="/library?artist={encodeURIComponent(artist.name)}"
                  class="focus-visible:ring-ring flex flex-col items-center gap-2 rounded-lg focus-visible:ring-2 focus-visible:outline-hidden"
                  aria-label="{i + 1}. {artist.name}, {streamsLabel(
                    artist.streams,
                    artist.minutes
                  )}"
                >
                  <Cover
                    artist={artist.name}
                    title={artist.name}
                    coverUrl={coverOf(artist.songId)}
                    size={136}
                    caption={false}
                    class="aspect-square !h-auto !w-full !rounded-full"
                  />
                  <span class="w-full min-w-0 text-center">
                    <span class="text-subheadline block truncate font-semibold md:text-xs"
                      >{i + 1}. {artist.name}</span
                    >
                    <span class="text-footnote text-muted-foreground block truncate md:text-[11px]"
                      >{artist.streams.toLocaleString()} streams</span
                    >
                  </span>
                </a>
              </li>
            {/each}
          </ol>
        </section>
      {/if}

      {#if stats.topAlbums.length > 0}
        <section aria-labelledby="listening-albums">
          {@render sectionTitle('listening-albums', 'Top albums')}
          <ol
            data-scroll-x=""
            class="no-scrollbar -mx-4 flex snap-x scroll-px-4 gap-4 overflow-x-auto overflow-y-hidden px-4 pb-1 md:-mx-7 md:scroll-px-7 md:px-7"
          >
            {#each stats.topAlbums as album, i (`${album.album}\u0000${album.albumArtist ?? ''}`)}
              {@const href = albumHref(album.songId)}
              <li class="w-[136px] shrink-0 snap-start md:w-[152px]">
                {#if href}
                  <a
                    {href}
                    class="focus-visible:ring-ring flex flex-col gap-2 rounded-lg focus-visible:ring-2 focus-visible:outline-hidden"
                  >
                    {@render albumTile(album, i)}
                  </a>
                {:else}
                  <div class="flex flex-col gap-2">{@render albumTile(album, i)}</div>
                {/if}
              </li>
            {/each}
          </ol>
        </section>
      {/if}

      <section aria-labelledby="listening-clock">
        {@render sectionTitle('listening-clock', 'Listening clock')}
        <ListeningClock
          clock={stats.clock}
          weekdays={stats.weekdays}
          peakHour={stats.facts.peakHour}
        />
      </section>

      <section aria-labelledby="listening-activity">
        {@render sectionTitle('listening-activity', 'Activity')}
        <ListeningActivity {buckets} unit={bucketUnit} />
      </section>

      {#if facts.length > 0}
        <section aria-labelledby="listening-facts">
          {@render sectionTitle('listening-facts', 'Fun facts')}
          <dl class="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
            {#each facts as fact (fact.label)}
              <div class="bg-secondary flex min-w-0 flex-col gap-0.5 rounded-xl px-4 py-3.5">
                <dt class="text-footnote text-muted-foreground">{fact.label}</dt>
                <dd class="text-title-2 truncate">{fact.value}</dd>
                {#if fact.detail}
                  <dd class="text-footnote text-muted-foreground">{fact.detail}</dd>
                {/if}
              </div>
            {/each}
          </dl>
        </section>
      {/if}

      {#if recentGroups.length > 0}
        <section aria-labelledby="listening-recent">
          {@render sectionTitle('listening-recent', 'Recent streams')}
          <div class="flex flex-col gap-4">
            {#each recentGroups as group (group.key)}
              <div>
                <h3 class="text-footnote text-muted-foreground mb-1 px-1 font-semibold">
                  {group.label}
                </h3>
                <ul class="flex flex-col">
                  {#each group.items as item, i (`${item.songId}-${item.playedAtUtc}-${i}`)}
                    <li>
                      {@render trackRow(
                        item.songId,
                        item.title,
                        [item.artist, item.album].filter(Boolean).join(' • '),
                        formatRelativeTime(item.playedAtUtc),
                        () => playFrom(recentIds, recentIds.indexOf(item.songId))
                      )}
                    </li>
                  {/each}
                </ul>
              </div>
            {/each}
          </div>
        </section>
      {/if}
    {/if}
  </div>
</ScrollArea>

{#snippet albumTile(
  album: { album: string; albumArtist?: string; streams: number; songId: number },
  i: number
)}
  <Cover
    artist={album.albumArtist ?? ''}
    title={album.album}
    coverUrl={coverOf(album.songId)}
    size={152}
    caption={false}
    class="aspect-square !h-auto !w-full"
  />
  <span class="w-full min-w-0">
    <span class="text-subheadline block truncate font-semibold md:text-xs"
      >{i + 1}. {album.album}</span
    >
    <span class="text-footnote text-muted-foreground block truncate md:text-[11px]"
      >{album.albumArtist ?? 'Unknown artist'} · {album.streams.toLocaleString()}</span
    >
  </span>
{/snippet}
