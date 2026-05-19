<script lang="ts">
  import { ArrowLeft, Disc3, Pause, Play } from '@lucide/svelte';
  import { ScrollArea } from '$lib/components/ui/scroll-area';
  import { Button } from '$lib/components/ui/button';
  import { cn } from '$lib/utils';
  import { playerStore } from '$lib/stores/player.svelte';
  import { getSongStreamUrl, type ApiSong } from '$lib/api-client';

  type Props = {
    songs: ApiSong[];
    albumKey: string;
    isLoading: boolean;
  };
  const { songs, albumKey, isLoading }: Props = $props();

  const UNKNOWN_ALBUM = 'Unknown Album';
  const UNKNOWN_ARTIST = 'Unknown Artist';

  function albumKeyForSong(song: ApiSong): string {
    const title = (song.album ?? UNKNOWN_ALBUM).trim() || UNKNOWN_ALBUM;
    const artist =
      (song.albumArtist ?? song.artist ?? UNKNOWN_ARTIST).trim() || UNKNOWN_ARTIST;
    return `${artist.toLowerCase()}::${title.toLowerCase()}`;
  }

  function computeInitials(title: string): string {
    const letters = title
      .split(/\s+/)
      .filter(Boolean)
      .slice(0, 2)
      .map((w) => w[0]?.toUpperCase() ?? '')
      .join('');
    return letters || title.slice(0, 2).toUpperCase();
  }

  function formatDuration(seconds: number | null | undefined): string {
    if (!seconds || seconds <= 0) return '—';
    const total = Math.floor(seconds);
    const hrs = Math.floor(total / 3600);
    const mins = Math.floor((total % 3600) / 60);
    const secs = total % 60;
    if (hrs > 0) {
      return `${hrs}:${mins.toString().padStart(2, '0')}:${secs.toString().padStart(2, '0')}`;
    }
    return `${mins}:${secs.toString().padStart(2, '0')}`;
  }

  function hideOnError(e: Event) {
    (e.currentTarget as HTMLImageElement).style.display = 'none';
  }

  const albumSongs = $derived(
    songs
      .filter((s) => albumKeyForSong(s) === albumKey)
      .sort((a, b) => {
        const na = a.trackNumber ?? Number.POSITIVE_INFINITY;
        const nb = b.trackNumber ?? Number.POSITIVE_INFINITY;
        if (na !== nb) return na - nb;
        return (a.title ?? a.fileName).localeCompare(b.title ?? b.fileName);
      })
  );

  const first = $derived(albumSongs[0]);
  const title = $derived((first?.album ?? UNKNOWN_ALBUM).trim() || UNKNOWN_ALBUM);
  const artist = $derived(
    (first?.albumArtist ?? first?.artist ?? UNKNOWN_ARTIST).trim() || UNKNOWN_ARTIST
  );
  const year = $derived(
    albumSongs.reduce<number | null>((acc, s) => {
      if (!s.year) return acc;
      return acc == null ? s.year : Math.min(acc, s.year);
    }, null)
  );
  const totalSeconds = $derived(
    albumSongs.reduce((sum, s) => sum + (s.durationSeconds ?? 0), 0)
  );
  const initials = $derived(computeInitials(title));
  const coverUrl = $derived(albumSongs.find((s) => s.albumArt)?.albumArt ?? null);

  function playFirst() {
    const target = albumSongs[0];
    if (!target) return;
    void playerStore.playSong({
      id: target.id,
      title: (target.title ?? target.fileName).trim() || target.fileName,
      artist: (target.artist ?? artist).trim() || artist,
      streamUrl: getSongStreamUrl(target.id)
    });
  }
</script>

{#if isLoading && albumSongs.length === 0}
  <div class="text-muted-foreground flex flex-1 items-center justify-center p-8 text-sm">
    Loading album...
  </div>
{:else if albumSongs.length === 0}
  <div
    class="text-muted-foreground flex flex-1 flex-col items-center justify-center gap-3 p-8 text-center"
  >
    <Disc3 class="size-10 opacity-40" />
    <p class="text-sm">Album not found in your library.</p>
    <a href="/app" class="text-primary text-sm underline-offset-4 hover:underline">
      Back to all albums
    </a>
  </div>
{:else}
  <ScrollArea class="min-h-0 flex-1">
    <div class="p-4 md:p-6">
      <a
        href="/app"
        class="text-muted-foreground hover:text-foreground mb-4 inline-flex items-center gap-1.5 text-sm transition-colors"
      >
        <ArrowLeft class="size-4" />
        All albums
      </a>

      <div class="mb-6 flex flex-col gap-4 sm:flex-row sm:items-end sm:gap-6">
        <div
          class="border-border from-secondary to-muted relative aspect-square w-40 shrink-0 overflow-hidden rounded-lg border bg-gradient-to-br shadow-sm sm:w-48"
        >
          {#if coverUrl}
            <img
              src={coverUrl}
              alt=""
              class="size-full object-cover"
              onerror={hideOnError}
            />
          {:else}
            <div class="flex size-full items-center justify-center">
              <span class="text-muted-foreground/60 text-4xl font-semibold tracking-wide">
                {initials}
              </span>
            </div>
          {/if}
        </div>
        <div class="min-w-0 flex-1">
          <p class="text-muted-foreground text-xs font-medium tracking-wide uppercase">Album</p>
          <h1 class="truncate text-3xl font-bold sm:text-4xl">{title}</h1>
          <p class="text-muted-foreground mt-1 truncate text-sm">
            {artist}{year ? ` · ${year}` : ''}{` · ${albumSongs.length} track${albumSongs.length === 1 ? '' : 's'}`}{totalSeconds >
            0
              ? ` · ${formatDuration(totalSeconds)}`
              : ''}
          </p>
          <div class="mt-4">
            <Button size="sm" onclick={playFirst}>
              <Play class="mr-1.5 size-3.5" />
              Play
            </Button>
          </div>
        </div>
      </div>

      <div class="border-border overflow-hidden rounded-lg border">
        <div
          class="border-border bg-card/30 text-muted-foreground grid grid-cols-[32px_minmax(0,1fr)_72px] items-center gap-3 border-b px-3 py-2 text-xs font-medium tracking-wide uppercase sm:grid-cols-[32px_minmax(0,1fr)_minmax(0,160px)_72px]"
        >
          <span class="text-right">#</span>
          <span>Title</span>
          <span class="hidden sm:block">Artist</span>
          <span class="text-right">Duration</span>
        </div>
        {#each albumSongs as song, i (song.id)}
          {@const isCurrentlyLoaded = playerStore.currentSong?.id === song.id}
          {@const isCurrentlyPlaying = isCurrentlyLoaded && playerStore.isPlaying}
          {@const trackNum = song.trackNumber ?? i + 1}
          {@const trackTitle = (song.title ?? song.fileName).trim() || song.fileName}
          {@const trackArtist = (song.artist ?? artist).trim() || artist}
          <button
            type="button"
            onclick={() =>
              playerStore.playSong({
                id: song.id,
                title: trackTitle,
                artist: trackArtist,
                streamUrl: getSongStreamUrl(song.id)
              })}
            class={cn(
              'group border-border hover:bg-secondary/40 grid w-full grid-cols-[32px_minmax(0,1fr)_72px] items-center gap-3 border-b px-3 py-2.5 text-left transition-colors last:border-b-0 sm:grid-cols-[32px_minmax(0,1fr)_minmax(0,160px)_72px]',
              isCurrentlyLoaded && 'bg-primary/5'
            )}
            aria-label={isCurrentlyPlaying ? `Pause ${trackTitle}` : `Play ${trackTitle}`}
          >
            <span class="relative flex h-6 items-center justify-end">
              <span
                class={cn(
                  'text-muted-foreground text-sm tabular-nums transition-opacity group-hover:opacity-0',
                  isCurrentlyPlaying && 'opacity-0'
                )}
              >
                {trackNum}
              </span>
              <span
                class={cn(
                  'text-primary absolute inset-0 flex items-center justify-end opacity-0 transition-opacity group-hover:opacity-100',
                  isCurrentlyPlaying && 'opacity-100'
                )}
              >
                {#if isCurrentlyPlaying}
                  <Pause class="size-4" />
                {:else}
                  <Play class="size-4" />
                {/if}
              </span>
            </span>
            <span class="min-w-0">
              <span
                class={cn(
                  'block truncate text-sm font-medium',
                  isCurrentlyLoaded && 'text-primary'
                )}
              >
                {trackTitle}
              </span>
              <span class="text-muted-foreground block truncate text-xs sm:hidden">
                {trackArtist}
              </span>
            </span>
            <span class="text-muted-foreground hidden truncate text-sm sm:block">
              {trackArtist}
            </span>
            <span class="text-muted-foreground text-right text-xs tabular-nums">
              {formatDuration(song.durationSeconds)}
            </span>
          </button>
        {/each}
      </div>
    </div>
  </ScrollArea>
{/if}
