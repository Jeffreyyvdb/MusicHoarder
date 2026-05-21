<script lang="ts">
  import GroupGrid from '$lib/components/organize/GroupGrid.svelte';
  import { buildYearGroups, fetchSongs, type ApiSong, type GroupSummary } from '$lib/api-client';

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

  const groups = $derived(buildYearGroups(songs));

  function hrefFor(group: GroupSummary): string {
    return `/app?year=${encodeURIComponent(group.key.toLowerCase())}`;
  }
</script>

<GroupGrid
  title="By Year"
  noun="year"
  searchPlaceholder="Search years..."
  {groups}
  {isLoading}
  {hrefFor}
/>
