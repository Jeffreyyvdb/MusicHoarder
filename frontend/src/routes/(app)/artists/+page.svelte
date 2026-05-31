<script lang="ts">
  import GroupGrid from '$lib/components/organize/GroupGrid.svelte';
  import { buildArtistGroups, fetchSongs, type ApiSong, type GroupSummary } from '$lib/api-client';
  import { uiVersion } from '$lib/stores/ui-version.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import LibraryV2 from '$lib/components/v2/LibraryV2.svelte';

  const isMobile = new IsMobile();
  // v2 desktop renders the redesigned Library shell (Artists tab) in-place; v1
  // (and v2 on mobile) keeps the existing GroupGrid.
  const showV2 = $derived(uiVersion.isV2 && !isMobile.current);

  let songs = $state<ApiSong[]>([]);
  let isLoading = $state(true);

  $effect(() => {
    // v2 owns its own fetching; skip the v1 data layer when it's showing.
    if (showV2) return;
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

{#if showV2}
  <LibraryV2 tab="artists" />
{:else}
  <GroupGrid
    title="Artists"
    noun="artist"
    searchPlaceholder="Search artists..."
    {groups}
    {isLoading}
    {hrefFor}
  />
{/if}
