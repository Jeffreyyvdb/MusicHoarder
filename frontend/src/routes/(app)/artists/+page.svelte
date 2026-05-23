<script lang="ts">
  import GroupGrid from '$lib/components/organize/GroupGrid.svelte';
  import { buildArtistGroups, fetchSongs, type ApiSong, type GroupSummary } from '$lib/api-client';

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

  const groups = $derived(buildArtistGroups(songs));

  function hrefFor(group: GroupSummary): string {
    return `/library?artist=${encodeURIComponent(group.key)}`;
  }
</script>

<GroupGrid
  title="Artists"
  noun="artist"
  searchPlaceholder="Search artists..."
  {groups}
  {isLoading}
  {hrefFor}
/>
