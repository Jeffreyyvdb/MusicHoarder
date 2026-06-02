<script lang="ts">
  import { Disc3, Play } from '@lucide/svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { prettyProvider, toPlayerSong, type AlbumStatusInfo, type AlbumSummary } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';

  type Props = {
    albums: AlbumSummary[];
    /** href builder for an album card (keeps deep-linkable `?album=` URLs). */
    hrefFor: (album: AlbumSummary) => string;
    /** Whether the underlying songs are still loading (controls the empty/skeleton copy). */
    isLoading?: boolean;
    /** Per-album provider-link status (keyed by `album.key`) for the corner badge. */
    statuses?: Map<string, AlbumStatusInfo>;
  };
  const { albums, hrefFor, isLoading = false, statuses }: Props = $props();

  /** Corner-badge appearance for an album's link status, or null to show nothing. */
  function badgeFor(album: AlbumSummary): { dotClass: string; label: string } | null {
    const info = statuses?.get(album.key);
    if (!info) return null;
    // A confirmed mis-match dominates the badge regardless of link state.
    if (info.verdict === 'Wrong') {
      return { dotClass: 'bg-red-500', label: 'Likely wrong album — AI flagged the match' };
    }
    if (info.status === 'linked') {
      const names = info.providers.map(prettyProvider).join(', ');
      return { dotClass: 'bg-emerald-400', label: names ? `Linked · ${names}` : 'Linked to a provider' };
    }
    if (info.status === 'localOnly') {
      return { dotClass: 'bg-white/70 dark:bg-white/60', label: 'Local only — not on any provider' };
    }
    return { dotClass: 'bg-amber-300/80 animate-pulse', label: 'Checking providers…' };
  }

  function playFirst(album: AlbumSummary, e: MouseEvent) {
    e.preventDefault();
    e.stopPropagation();
    if (album.songs.length === 0) return;
    const queue = album.songs.map((s) => toPlayerSong(s, album.artist));
    void playerStore.playSong(queue[0], queue, 0);
  }
</script>

{#if albums.length === 0}
  <div class="text-muted-foreground flex flex-col items-center justify-center gap-3 py-16 text-center">
    <Disc3 class="size-10 opacity-40" />
    <p class="text-sm">{isLoading ? 'Loading albums…' : 'No albums match.'}</p>
  </div>
{:else}
  <div
    class="grid grid-cols-2 gap-x-3 gap-y-6 sm:grid-cols-3 sm:gap-x-5 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6"
  >
    {#each albums as album (album.key)}
      <a
        href={hrefFor(album)}
        class="group focus-visible:ring-ring outline-hidden flex flex-col gap-2 rounded-lg p-1 transition-transform [content-visibility:auto] [contain-intrinsic-size:auto_13rem] hover:-translate-y-0.5 focus-visible:ring-2 focus-visible:ring-offset-2"
        aria-label={`Open album ${album.title} by ${album.artist}`}
      >
        <div class="relative">
          <Cover
            artist={album.artist}
            title={album.title}
            coverUrl={album.coverUrl}
            size={176}
            interactive
            class="!h-auto !w-full aspect-square"
          />
          {#if badgeFor(album)}
            {@const badge = badgeFor(album)}
            <span
              class="absolute top-1.5 left-1.5 size-2.5 rounded-full ring-2 ring-black/35 {badge!.dotClass}"
              title={badge!.label}
            ></span>
          {/if}
          <button
            type="button"
            aria-label={`Play ${album.title}`}
            onclick={(e) => playFirst(album, e)}
            class="bg-primary text-primary-foreground absolute right-2 bottom-2 grid size-9 translate-y-1 place-items-center rounded-full opacity-0 shadow-md transition-all duration-150 group-hover:translate-y-0 group-hover:opacity-100"
          >
            <Play class="size-4" />
          </button>
        </div>
        <div class="min-w-0 px-0.5">
          <p class="truncate text-[12.5px] font-medium">{album.title}</p>
          <p class="text-muted-foreground truncate text-[11.5px]">
            {album.artist}{album.year ? ` · ${album.year}` : ''}
          </p>
        </div>
      </a>
    {/each}
  </div>
{/if}
