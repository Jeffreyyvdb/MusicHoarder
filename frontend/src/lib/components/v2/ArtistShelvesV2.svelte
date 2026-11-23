<script lang="ts">
  import { onMount, type Snippet } from 'svelte';
  import { goto } from '$app/navigation';
  import { toast } from 'svelte-sonner';
  import { ChevronRight, Plus } from '@lucide/svelte';
  import Cover from '#lib/components/file-browser/Cover.svelte';
  import { ScrollArea } from '#lib/components/ui/scroll-area/index.ts';
  import { getArtistImageUrl, type ArtistSuggestion } from '#lib/api-client.ts';
  import { messageOf } from '#lib/artist-discography.ts';
  import { artistShelves } from '#lib/stores/artist-shelves.svelte.ts';
  import { cn } from '#lib/utils.ts';

  // Above the Artists grid, for an admin: the artists you follow first, then the ones your listening
  // suggests. Both stay out of the way of the grid: nothing is shown until there is something to show.

  onMount(() => {
    void artistShelves.load();
  });

  const follows = $derived(artistShelves.follows ?? []);
  const suggestions = $derived(artistShelves.suggestions ?? []);

  function artistHref(name: string): string {
    return `/library?artist=${encodeURIComponent(name)}`;
  }

  function plural(n: number, word: string): string {
    return `${n.toLocaleString()} ${word}${n === 1 ? '' : 's'}`;
  }

  async function follow(s: ArtistSuggestion) {
    try {
      const result = await artistShelves.follow(s);
      if (result === 'followed') {
        toast.success(`Following ${s.artist}`, {
          description: 'New releases will show up under Following.'
        });
      } else {
        toast(`Which ${s.artist}?`, {
          description: 'The catalog has more than one. Pick yours on the artist page to follow it.'
        });
        await goto(artistHref(s.artist));
      }
    } catch (err) {
      toast.error(messageOf(err, `Could not follow ${s.artist}.`));
    }
  }

  const TILE =
    'flex w-[120px] shrink-0 snap-start flex-col items-center gap-2 rounded-lg md:w-[136px]';
  const PORTRAIT =
    'aspect-square !h-auto !w-full !rounded-full shadow-[0_2px_10px_rgba(0,0,0,0.15)]';
</script>

{#snippet header(title: string, id: string, subtitle: string, href: string | null)}
  <div class="flex items-end justify-between gap-3 px-4 md:px-7">
    <div class="min-w-0">
      <h2 {id} class="text-title-2 md:text-lg md:font-semibold">{title}</h2>
      <p class="text-footnote text-muted-foreground md:text-xs">{subtitle}</p>
    </div>
    {#if href}
      <a
        {href}
        aria-label="See all {title.toLowerCase()}"
        class="text-primary text-body relative -mr-2 flex h-11 shrink-0 items-center gap-0.5 px-2 hover:underline md:h-8 md:text-sm"
      >
        See all
        <ChevronRight class="size-4 md:hidden" aria-hidden="true" />
      </a>
    {/if}
  </div>
{/snippet}

{#snippet shelf(children: Snippet)}
  <ScrollArea
    orientation="horizontal"
    data-scroll-x=""
    class="[&>[data-slot=scroll-area-viewport]]:snap-x [&>[data-slot=scroll-area-viewport]]:scroll-px-4 md:[&>[data-slot=scroll-area-viewport]]:scroll-px-7"
  >
    <ul class="flex gap-4 px-4 pt-2 pb-3 md:px-7">
      {@render children()}
    </ul>
  </ScrollArea>
{/snippet}

{#if follows.length > 0 || suggestions.length > 0}
  <div class="flex flex-col gap-5 pt-2 pb-4 md:pt-5">
    {#if follows.length > 0}
      <section aria-labelledby="artists-following">
        {@render header(
          'Following',
          'artists-following',
          plural(follows.length, 'artist'),
          '/following'
        )}
        {#snippet followTiles()}
          {#each follows as f (f.id)}
            <li class={TILE}>
              <a
                href={artistHref(f.name)}
                class="focus-visible:ring-ring flex w-full flex-col items-center gap-2 rounded-lg focus-visible:ring-2 focus-visible:outline-hidden"
              >
                <Cover
                  artist={f.name}
                  title={f.name}
                  coverUrl={f.pictureUrl ?? getArtistImageUrl(f.name)}
                  size={136}
                  caption={false}
                  interactive
                  class={PORTRAIT}
                />
                <span class="w-full min-w-0 text-center">
                  <span
                    class="text-subheadline block truncate font-semibold md:text-[12.5px] md:font-medium"
                  >
                    {f.name}
                  </span>
                  <span
                    class={cn(
                      'text-footnote block truncate md:text-[11px]',
                      f.newReleaseCount > 0 ? 'text-primary' : 'text-muted-foreground'
                    )}
                  >
                    {f.newReleaseCount > 0
                      ? plural(f.newReleaseCount, 'new release')
                      : 'No new releases'}
                  </span>
                </span>
              </a>
            </li>
          {/each}
        {/snippet}
        {@render shelf(followTiles)}
      </section>
    {/if}

    {#if suggestions.length > 0}
      <section aria-labelledby="artists-suggested">
        {@render header(
          'Suggested for you',
          'artists-suggested',
          'Artists you have been playing lately',
          null
        )}
        {#snippet suggestionTiles()}
          {#each suggestions as s (s.artist)}
            {@const busy = artistShelves.pending.has(s.artist)}
            <li class={TILE}>
              <a
                href={artistHref(s.artist)}
                class="focus-visible:ring-ring flex w-full flex-col items-center gap-2 rounded-lg focus-visible:ring-2 focus-visible:outline-hidden"
              >
                <Cover
                  artist={s.artist}
                  title={s.artist}
                  coverUrl={getArtistImageUrl(s.artist)}
                  size={136}
                  caption={false}
                  interactive
                  class={PORTRAIT}
                />
                <span class="w-full min-w-0 text-center">
                  <span
                    class="text-subheadline block truncate font-semibold md:text-[12.5px] md:font-medium"
                  >
                    {s.artist}
                  </span>
                  <span
                    class="text-footnote text-muted-foreground block truncate tabular-nums md:text-[11px]"
                  >
                    {plural(s.plays, 'play')} · {plural(s.songs, 'song')}
                  </span>
                </span>
              </a>
              <button
                type="button"
                disabled={busy}
                aria-label="Follow {s.artist}"
                onclick={() => follow(s)}
                class="bg-secondary hover:bg-secondary-hover text-foreground focus-visible:ring-ring text-subheadline inline-flex min-h-11 items-center gap-1 rounded-full px-4 font-semibold transition-colors focus-visible:ring-2 focus-visible:outline-hidden disabled:opacity-60 md:min-h-7 md:px-3 md:text-xs"
              >
                <Plus class="size-3.5" aria-hidden="true" />
                {busy ? 'Following…' : 'Follow'}
              </button>
            </li>
          {/each}
        {/snippet}
        {@render shelf(suggestionTiles)}
      </section>
    {/if}
  </div>
{/if}
