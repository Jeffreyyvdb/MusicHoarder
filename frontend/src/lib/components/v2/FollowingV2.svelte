<script lang="ts">
  import { untrack } from 'svelte';
  import { BellRing, Ellipsis, RotateCw, TriangleAlert, UserPlus } from '@lucide/svelte';
  import { toast } from 'svelte-sonner';
  import { Button } from '#lib/components/ui/button/index.ts';
  import * as DropdownMenu from '#lib/components/ui/dropdown-menu/index.ts';
  import { EmptyState } from '#lib/components/ui/empty-state/index.ts';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { SearchField } from '#lib/components/ui/search-field/index.ts';
  import { Spinner } from '#lib/components/ui/spinner/index.ts';
  import Cover from '#lib/components/file-browser/Cover.svelte';
  import PageToolbarV2 from '#lib/components/v2/PageToolbarV2.svelte';
  import {
    checkFollowedArtist,
    fetchFollowing,
    followArtist,
    getArtistImageUrl,
    searchCatalogArtists,
    unfollowArtist,
    updateFollow,
    type ArtistReleaseKind,
    type CatalogArtist,
    type FollowedArtist,
    type FollowingResponse
  } from '#lib/api-client.ts';
  import {
    ALL_KINDS,
    catalogArtistCaption,
    decisionLabel,
    followSummary,
    KIND_PLURAL,
    messageOf,
    releaseCaption,
    toggleReleaseKind
  } from '#lib/artist-discography.ts';
  import { formatRelativeTime } from '#lib/formatters.ts';
  import { cn } from '#lib/utils.ts';

  // Following: the artists whose new releases MusicHoarder watches for, the releases it found, and a
  // catalog search to follow someone who is not in the library yet. Following from an artist's own
  // page is the usual way in; this is where they are all managed.

  let data = $state.raw<FollowingResponse | null>(null);
  let loading = $state(true);
  let loadError = $state<string | null>(null);
  let busy = $state.raw<ReadonlySet<number | string>>(new Set());

  async function load() {
    try {
      data = await fetchFollowing();
      loadError = null;
    } catch (err) {
      loadError = messageOf(err, 'Could not load the artists you follow.');
    } finally {
      loading = false;
    }
  }
  $effect(() => {
    void untrack(load);
  });

  async function withBusy(key: number | string, run: () => Promise<void>) {
    busy = new Set([...busy, key]);
    try {
      await run();
    } catch (err) {
      toast.error(messageOf(err, 'That did not work.'));
    } finally {
      busy = new Set([...busy].filter((k) => k !== key));
    }
  }

  // ── catalog search ────────────────────────────────────────────────────────
  let query = $state('');
  let results = $state.raw<CatalogArtist[]>([]);
  let searching = $state(false);
  let searchToken = 0;
  $effect(() => {
    const q = query.trim();
    const token = ++searchToken;
    if (q.length < 2) {
      results = [];
      searching = false;
      return;
    }
    searching = true;
    const timer = setTimeout(async () => {
      try {
        const found = await searchCatalogArtists(q);
        if (token === searchToken) results = found;
      } catch {
        if (token === searchToken) results = [];
      } finally {
        if (token === searchToken) searching = false;
      }
    }, 300);
    return () => clearTimeout(timer);
  });

  const followedIds = $derived(new Set((data?.follows ?? []).map((f) => f.deezerArtistId)));

  function follow(artist: CatalogArtist) {
    void withBusy(artist.id, async () => {
      const created = await followArtist({ deezerArtistId: artist.id });
      toast.success(`Following ${created.name}`, {
        description:
          'What they have out now stays as it is; new releases are picked up from here on.'
      });
      await load();
    });
  }

  function unfollow(f: FollowedArtist) {
    void withBusy(f.id, async () => {
      await unfollowArtist(f.id);
      toast(`Unfollowed ${f.name}`);
      await load();
    });
  }

  function patch(
    f: FollowedArtist,
    input: { releaseKinds?: ArtistReleaseKind[]; autoDownload?: boolean }
  ) {
    void withBusy(f.id, async () => {
      const updated = await updateFollow(f.id, input);
      if (data) data = { ...data, follows: data.follows.map((x) => (x.id === f.id ? updated : x)) };
    });
  }

  function toggleKind(f: FollowedArtist, kind: ArtistReleaseKind, on: boolean) {
    const next = toggleReleaseKind(f.releaseKinds, kind, on);
    if (next) patch(f, { releaseKinds: next });
  }

  function checkNow(f: FollowedArtist) {
    void withBusy(f.id, async () => {
      const result = await checkFollowedArtist(f.id);
      if (result.idleReason === 'disabled') toast('The artist monitor is off on this server.');
      else if (result.newReleases > 0)
        toast.success(
          `${result.newReleases} new release${result.newReleases === 1 ? '' : 's'} from ${f.name}`
        );
      else toast(`Nothing new from ${f.name}`);
      await load();
    });
  }

  function artistHref(name: string): string {
    return `/library?artist=${encodeURIComponent(name)}`;
  }

  const meta = $derived(
    data ? `${data.follows.length} artist${data.follows.length === 1 ? '' : 's'}` : undefined
  );
</script>

{#snippet searchField()}
  <SearchField bind:value={query} label="Find an artist to follow" />
{/snippet}

<ScrollArea class="min-h-0 flex-1">
  <PageToolbarV2 title="Following" {meta} metaFrom="lg" search={searchField} />

  <div class="mx-auto flex w-full max-w-3xl flex-col gap-8 pt-2 pb-6 md:px-7 md:pt-4">
    {#if data && (!data.monitorEnabled || !data.downloadsEnabled)}
      <div class="bg-card mx-4 flex gap-3 rounded-xl p-4 md:mx-0" data-elevated="">
        <TriangleAlert class="text-warning-text mt-0.5 size-5 shrink-0" aria-hidden="true" />
        <p class="text-subheadline md:text-sm">
          {#if !data.monitorEnabled}
            The artist monitor is off on this server (<code>MusicEnricher:EnableArtistMonitor</code
            >), so nobody is checking for new releases.
          {:else}
            Downloads are off on this server, so new releases are noticed and announced but not
            downloaded.
          {/if}
        </p>
      </div>
    {/if}

    {#if query.trim().length >= 2}
      <section aria-labelledby="following-search-heading">
        <h2 id="following-search-heading" class="text-title-2 mb-1 px-4 md:px-0 md:text-lg">
          Artists
        </h2>
        {#if searching && results.length === 0}
          <div class="flex justify-center py-8"><Spinner class="size-5" /></div>
        {:else if results.length === 0}
          <p class="text-muted-foreground text-subheadline px-4 py-4 md:px-0 md:text-sm">
            No artist called “{query.trim()}”.
          </p>
        {:else}
          <ul>
            {#each results as artist (artist.id)}
              {@const following = followedIds.has(artist.id)}
              <li class="flex min-h-16 items-center gap-3 px-4 md:rounded-lg md:px-2">
                <Cover
                  artist={artist.name ?? ''}
                  title={artist.name ?? ''}
                  coverUrl={artist.pictureUrl}
                  size={48}
                  corner={24}
                  caption={false}
                />
                <div class="min-w-0 flex-1">
                  <p class="text-body truncate md:text-sm md:font-medium">{artist.name}</p>
                  <p class="text-muted-foreground text-footnote md:text-xs">
                    {catalogArtistCaption(artist)}
                  </p>
                </div>
                <button
                  type="button"
                  class={cn(
                    'border-border focus-visible:ring-ring text-subheadline relative inline-flex h-8 shrink-0 items-center gap-1.5 rounded-full border px-4 font-semibold after:absolute after:inset-x-0 after:-inset-y-1.5 after:content-[""] focus-visible:ring-2 focus-visible:outline-hidden disabled:opacity-60 md:text-sm',
                    !following && 'hover:border-foreground'
                  )}
                  disabled={following || busy.has(artist.id)}
                  aria-pressed={following ? 'true' : 'false'}
                  onclick={() => follow(artist)}
                >
                  {#if busy.has(artist.id)}<Spinner class="size-3.5" />{/if}
                  {following ? 'Following' : 'Follow'}
                </button>
              </li>
            {/each}
          </ul>
        {/if}
      </section>
    {/if}

    {#if loading}
      <div class="flex justify-center py-16"><Spinner class="size-6" /></div>
    {:else if loadError}
      <div class="flex flex-col items-center gap-3 px-6 py-16 text-center">
        <p class="text-destructive-text text-subheadline md:text-sm">{loadError}</p>
        <Button variant="gray" class="rounded-full" onclick={() => void load()}
          ><RotateCw /> Retry</Button
        >
      </div>
    {:else if data && data.follows.length === 0}
      {#if query.trim().length < 2}
        <EmptyState
          icon={UserPlus}
          title="No artists followed yet"
          hint="Follow an artist from their page, or search for one above. Their next album downloads when it comes out."
        />
      {/if}
    {:else if data}
      {#if data.recent.length > 0}
        <section aria-labelledby="following-new-heading">
          <h2 id="following-new-heading" class="text-title-2 mb-1 px-4 md:px-0 md:text-lg">
            New releases
          </h2>
          <ul>
            {#each data.recent as release (release.id)}
              <li>
                <a
                  href={artistHref(release.artist)}
                  class="hover:bg-accent focus-visible:ring-ring flex min-h-16 items-center gap-3 px-4 focus-visible:ring-2 focus-visible:outline-hidden focus-visible:ring-inset md:rounded-lg md:px-2"
                >
                  <Cover
                    artist={release.artist}
                    title={release.title}
                    coverUrl={release.coverUrl}
                    size={48}
                    corner={6}
                    caption={false}
                  />
                  <div class="min-w-0 flex-1">
                    <p class="text-body truncate md:text-sm md:font-medium">{release.title}</p>
                    <p class="text-muted-foreground text-footnote truncate md:text-xs">
                      {release.artist} · {releaseCaption(release)}
                    </p>
                  </div>
                  <div class="flex shrink-0 flex-col items-end text-right">
                    <span class="text-footnote md:text-xs">{decisionLabel(release)}</span>
                    <span class="text-muted-foreground text-footnote md:text-xs">
                      {formatRelativeTime(release.decidedAtUtc)}
                    </span>
                  </div>
                </a>
              </li>
            {/each}
          </ul>
        </section>
      {/if}

      <section aria-labelledby="following-artists-heading">
        <h2 id="following-artists-heading" class="text-title-2 mb-1 px-4 md:px-0 md:text-lg">
          Artists you follow
        </h2>
        <ul>
          {#each data.follows as f (f.id)}
            <li class="flex min-h-[72px] items-center gap-1 pr-1 md:rounded-lg">
              <a
                href={artistHref(f.name)}
                class="hover:bg-accent focus-visible:ring-ring flex min-h-[72px] min-w-0 flex-1 items-center gap-3 pl-4 focus-visible:ring-2 focus-visible:outline-hidden focus-visible:ring-inset md:rounded-lg md:pl-2"
              >
                <Cover
                  artist={f.name}
                  title={f.name}
                  coverUrl={getArtistImageUrl(f.name)}
                  fallbackUrl={f.pictureUrl}
                  size={56}
                  corner={28}
                  caption={false}
                />
                <div class="min-w-0 flex-1">
                  <p class="text-body truncate md:text-sm md:font-medium">{f.name}</p>
                  <p class="text-muted-foreground text-footnote truncate md:text-xs">
                    {followSummary(f.releaseKinds, f.autoDownload)}
                  </p>
                  <p class="text-muted-foreground text-footnote truncate md:text-xs">
                    {#if f.lastError}
                      <span class="text-warning-text">{f.lastError}</span>
                    {:else if f.lastCheckedAtUtc}
                      Checked {formatRelativeTime(f.lastCheckedAtUtc)}{f.newReleaseCount > 0
                        ? ` · ${f.newReleaseCount} new since you followed`
                        : ''}
                    {:else}
                      Not checked yet
                    {/if}
                  </p>
                </div>
              </a>
              {#if busy.has(f.id)}
                <span class="grid size-11 place-items-center"><Spinner class="size-4" /></span>
              {:else}
                <DropdownMenu.Root>
                  <DropdownMenu.Trigger>
                    {#snippet child({ props })}
                      <Button
                        {...props}
                        variant="ghost"
                        size="icon"
                        class="size-11 rounded-full"
                        aria-label={`Settings for ${f.name}`}
                      >
                        <Ellipsis class="size-5" />
                      </Button>
                    {/snippet}
                  </DropdownMenu.Trigger>
                  <DropdownMenu.Content align="end" class="w-60">
                    <DropdownMenu.Label>New releases</DropdownMenu.Label>
                    {#each ALL_KINDS as kind (kind)}
                      <DropdownMenu.CheckboxItem
                        checked={f.releaseKinds.includes(kind)}
                        onCheckedChange={(v) => toggleKind(f, kind, v)}
                      >
                        {KIND_PLURAL[kind]}
                      </DropdownMenu.CheckboxItem>
                    {/each}
                    <DropdownMenu.Separator />
                    <DropdownMenu.CheckboxItem
                      checked={f.autoDownload}
                      onCheckedChange={(v) => patch(f, { autoDownload: v })}
                    >
                      Download automatically
                    </DropdownMenu.CheckboxItem>
                    <DropdownMenu.Separator />
                    <DropdownMenu.Item onclick={() => checkNow(f)}>
                      <BellRing /> Check for new releases now
                    </DropdownMenu.Item>
                    <DropdownMenu.Item variant="destructive" onclick={() => unfollow(f)}
                      >Unfollow</DropdownMenu.Item
                    >
                  </DropdownMenu.Content>
                </DropdownMenu.Root>
              {/if}
            </li>
          {/each}
        </ul>
      </section>
    {/if}
  </div>
</ScrollArea>
