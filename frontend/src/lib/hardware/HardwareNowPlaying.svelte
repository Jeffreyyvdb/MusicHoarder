<script lang="ts">
  // Now Playing's middle under the hardware skin: the MH-1 device (HardwarePlayer) driven by the
  // player store, the same store the mini player and the plain Now Playing use, so every key on it
  // is a real play intent. TrackPanel loads this lazily, so the device's stylesheet (the vendored
  // SPEC-01 skin) downloads only for someone who has the easter egg on.
  import { toast } from 'svelte-sonner';
  import Hardware from './Hardware.svelte';
  import HardwarePlayer, { type HardwareTrack } from './HardwarePlayer.svelte';
  import { playerStore, type PlayerSong } from '#lib/stores/player.svelte.ts';
  import { songsStore } from '#lib/stores/songs.svelte.ts';

  let {
    maxRows = 5
  }: {
    /** Queue rows the device lists: the loaded song and what follows. */
    maxRows?: number;
  } = $props();

  const loaded = $derived(playerStore.currentSong);
  const queueSongs = $derived<PlayerSong[]>(
    loaded ? [loaded, ...playerStore.upNext.slice(0, Math.max(0, maxRows - 1))] : []
  );
  const rows = $derived<HardwareTrack[]>(
    queueSongs.map((song, i) => ({
      key: `${i}:${song.id}`,
      title: song.title,
      artist: song.artist,
      album: song.album,
      coverUrl: song.coverUrl,
      format: song.format,
      length: songsStore.songsById.get(song.id)?.durationSeconds ?? null
    }))
  );
  const librarySong = $derived(loaded ? songsStore.songsById.get(loaded.id) : undefined);

  async function like(next: boolean) {
    if (!librarySong || Boolean(librarySong.likedAtUtc) === next) return;
    try {
      await songsStore.toggleLike(librarySong.id);
    } catch (err) {
      toast.error('Could not update favourites', {
        description: err instanceof Error ? err.message : undefined
      });
    }
  }
</script>

<Hardware bare>
  <HardwarePlayer
    queue={rows}
    currentKey={rows[0]?.key ?? null}
    isPlaying={playerStore.isPlaying}
    currentTime={playerStore.currentTime}
    duration={playerStore.duration}
    volume={playerStore.volume}
    liked={Boolean(librarySong?.likedAtUtc)}
    canLike={Boolean(librarySong)}
    onPrevious={() => void playerStore.playPrevious()}
    onToggle={() => playerStore.togglePlay()}
    onNext={() => void playerStore.playNext()}
    onSeek={(t) => playerStore.seek(t)}
    onVolume={(v) => playerStore.setVolume(v)}
    onMute={() => playerStore.toggleMute()}
    onLike={like}
    onPick={(i) => void playerStore.startQueue(queueSongs, i)}
  />
</Hardware>
