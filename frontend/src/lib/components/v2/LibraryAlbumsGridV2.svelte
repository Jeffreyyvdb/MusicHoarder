<script lang="ts">
  import { Check, Disc3, Play } from '@lucide/svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { Skeleton } from '$lib/components/ui/skeleton';
  import { prettyProvider, toPlayerSong, type AlbumStatusInfo, type AlbumSummary } from '$lib/api-client';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn } from '$lib/utils';

  type Props = {
    albums: AlbumSummary[];
    /** href builder for an album card (keeps deep-linkable `?album=` URLs). */
    hrefFor: (album: AlbumSummary) => string;
    /** Whether the underlying songs are still loading (controls the empty/skeleton copy). */
    isLoading?: boolean;
    /** Per-album provider-link status (keyed by `artistLower::titleLower`) for the corner badge. */
    statuses?: Map<string, AlbumStatusInfo>;
  };
  const { albums, hrefFor, isLoading = false, statuses }: Props = $props();

  const GRID_CLASS = 'grid grid-cols-2 gap-x-3 gap-y-6 sm:grid-cols-3 sm:gap-x-5 md:grid-cols-4 lg:grid-cols-5 xl:grid-cols-6';

  /**
   * Corner-badge appearance for an album's link status, or null to show nothing. Kept as a `kind`
   * rather than a colour class so the template can differentiate every state by shape too, not just
   * hue — a `title` alone never reaches a touch tap, and colour alone doesn't survive Increase
   * Contrast or colour-blindness (F23).
   */
  type Badge = { kind: 'wrong' | 'linked' | 'localOnly' | 'checking'; label: string };
  function badgeFor(album: AlbumSummary): Badge | null {
    // Canonical link-status is keyed by album name (artist+title), not the folder-based album.key —
    // cards split across releases share the same name-based status badge.
    const info = statuses?.get(`${album.artist.toLowerCase()}::${album.title.toLowerCase()}`);
    if (!info) return null;
    // A confirmed mis-match dominates the badge regardless of link state.
    if (info.verdict === 'Wrong') {
      return { kind: 'wrong', label: 'Likely wrong album — AI flagged the match' };
    }
    if (info.status === 'linked') {
      const names = info.providers.map(prettyProvider).join(', ');
      return { kind: 'linked', label: names ? `Linked · ${names}` : 'Linked to a provider' };
    }
    if (info.status === 'localOnly') {
      return { kind: 'localOnly', label: 'Local only — not on any provider' };
    }
    return { kind: 'checking', label: 'Checking providers…' };
  }

  function playFirst(album: AlbumSummary, e: MouseEvent) {
    e.preventDefault();
    e.stopPropagation();
    if (album.songs.length === 0) return;
    const queue = album.songs.map((s) => toPlayerSong(s, album.artist));
    void playerStore.playSong(queue[0], queue, 0);
  }
</script>

{#if isLoading && albums.length === 0}
  <!-- Skeleton tiles in the real grid, not a spinner or a sentence — a first-run visitor should see
       an incoming grid, not what reads as an empty page (F26). -->
  <div class={GRID_CLASS}>
    {#each Array(12) as _, i (i)}
      <div class="flex flex-col gap-2 p-1">
        <Skeleton class="aspect-square w-full rounded-lg" />
        <div class="min-w-0 space-y-1.5 px-0.5">
          <Skeleton class="h-3 w-4/5" />
          <Skeleton class="h-3 w-3/5" />
        </div>
      </div>
    {/each}
  </div>
{:else if albums.length === 0}
  <div class="text-muted-foreground flex flex-col items-center justify-center gap-3 py-16 text-center">
    <Disc3 class="size-10 opacity-40" />
    <p class="text-sm">No albums match.</p>
  </div>
{:else}
  <div class={GRID_CLASS}>
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
            class="!h-auto !w-full aspect-square shadow-[0_2px_10px_rgba(0,0,0,0.12)] hover:shadow-[0_8px_24px_rgba(0,0,0,0.18)] dark:shadow-[0_4px_14px_rgba(0,0,0,0.5)] dark:hover:shadow-[0_10px_28px_rgba(0,0,0,0.6)]"
          />
          {#if badgeFor(album)}
            {@const badge = badgeFor(album)}
            <span
              role="img"
              aria-label={badge!.label}
              title={badge!.label}
              class={cn(
                'absolute top-1.5 left-1.5 grid size-2.5 place-items-center rounded-full ring-2 ring-black/35',
                badge!.kind === 'wrong' && 'border-2 border-red-500 bg-red-500/30',
                badge!.kind === 'linked' && 'bg-emerald-400',
                badge!.kind === 'localOnly' && 'border-2 border-white/80 bg-transparent',
                badge!.kind === 'checking' && 'animate-pulse bg-amber-300/80'
              )}
            >
              {#if badge!.kind === 'linked'}
                <Check class="size-[7px] text-black/70" strokeWidth={3.5} />
              {/if}
            </span>
          {/if}
          <button
            type="button"
            aria-label={`Play ${album.title}`}
            onclick={(e) => playFirst(album, e)}
            class="bg-primary text-primary-foreground absolute right-2 bottom-2 grid size-9 translate-y-1 place-items-center rounded-full opacity-0 shadow-md transition-all duration-150 group-hover:translate-y-0 group-hover:opacity-100 focus-visible:translate-y-0 focus-visible:opacity-100 group-focus-within:opacity-100 pointer-coarse:translate-y-0 pointer-coarse:opacity-100"
          >
            <Play class="size-4" />
          </button>
        </div>
        <div class="min-w-0 px-0.5">
          <p class="truncate text-[12.5px] font-medium">{album.title}</p>
          <p class="text-muted-foreground truncate text-[11.5px]">
            {album.artist}{album.year ? ` · ${album.year}` : ''}
          </p>
          {#if album.folderKeys.length > 1}
            <!-- The card folds together several destination folders — say so rather than silently
                 hiding that this album is split on disk. -->
            <p
              class="text-muted-foreground-dim text-[11px]"
              title={album.folderKeys.join('\n')}
            >
              {album.folderKeys.length} editions
            </p>
          {/if}
        </div>
      </a>
    {/each}
  </div>
{/if}
