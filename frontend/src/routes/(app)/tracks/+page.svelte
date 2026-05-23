<script lang="ts">
  import { page } from '$app/state';
  import TrackList from '$lib/components/file-browser/TrackList.svelte';
  import { fetchSongs, type ApiSong } from '$lib/api-client';

  let songs = $state<ApiSong[]>([]);
  let isLoading = $state(true);

  $effect(() => {
    let cancelled = false;
    void (async () => {
      try {
        const loaded = await fetchSongs();
        if (!cancelled) songs = loaded;
      } finally {
        if (!cancelled) isLoading = false;
      }
    })();
    return () => {
      cancelled = true;
    };
  });

  const searchQuery = $derived(page.url.searchParams.get('q') ?? '');
</script>

<TrackList {songs} {searchQuery} {isLoading} />
