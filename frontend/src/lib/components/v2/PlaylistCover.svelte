<script lang="ts">
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import {
    albumKeyForSong,
    coverUrlForSong,
    type ApiSong,
    type LibraryPlaylist
  } from '$lib/api-client';
  import { playlistSongs } from '$lib/stores/playlists.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { cn } from '$lib/utils';

  // A playlist's artwork: the remote playlist's own image when it follows one, else Apple Music's
  // mosaic of the first four albums on it, else the first song's cover, else a tinted tile with the
  // playlist's initials (Cover's own placeholder).
  type Props = {
    playlist: Pick<LibraryPlaylist, 'name' | 'songIds' | 'source'>;
    size?: number;
    corner?: number;
    dprCap?: number;
    class?: string;
  };

  const { playlist, size = 176, corner = 8, dprCap = 2, class: className }: Props = $props();

  const imageUrl = $derived(playlist.source?.imageUrl ?? null);

  // The first songs with distinct albums, just enough of them for the mosaic.
  const tiles = $derived.by(() => {
    if (imageUrl) return [];
    const seen = new Set<string>();
    const out: ApiSong[] = [];
    for (const song of playlistSongs(playlist, songsStore.songsById)) {
      if (!coverUrlForSong(song)) continue;
      const key = albumKeyForSong(song);
      if (seen.has(key)) continue;
      seen.add(key);
      out.push(song);
      if (out.length === 4) break;
    }
    return out;
  });
</script>

{#if !imageUrl && tiles.length === 4}
  <div
    class={cn('mh-cover grid shrink-0 grid-cols-2 overflow-hidden shadow-sm', className)}
    style="width: {size}px; height: {size}px; border-radius: {corner}px;"
    aria-hidden="true"
  >
    {#each tiles as song (song.id)}
      <Cover
        artist={song.albumArtist ?? song.artist ?? ''}
        title={song.album ?? ''}
        coverUrl={coverUrlForSong(song)}
        size={size / 2}
        corner={0}
        caption={false}
        {dprCap}
        class="aspect-square !h-auto !w-full shadow-none"
      />
    {/each}
  </div>
{:else}
  <Cover
    artist={playlist.name}
    title={playlist.name}
    coverUrl={imageUrl ?? (tiles[0] ? coverUrlForSong(tiles[0]) : null)}
    {size}
    {corner}
    caption={false}
    {dprCap}
    class={cn('shrink-0', className)}
  />
{/if}
