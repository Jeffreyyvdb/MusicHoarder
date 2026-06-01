<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { uiVersion } from '$lib/stores/ui-version.svelte';
  import TrackTimelineV2 from '$lib/components/v2/TrackTimelineV2.svelte';

  // /track/[id] is a v2-only provenance surface. When the design flag is 'v1' it
  // has no standalone equivalent, so redirect to the v1 Provenance & review screen
  // for that same song (?song=<id>), never dead-ending the deep link. v2 now renders
  // responsively on mobile, so the only fallbacks are v1 (→ /review) and an invalid
  // id (→ /library). The flag is client-only (localStorage) and the (app) group is
  // ssr=false, so guard on mount in an effect.
  const songId = $derived(Number.parseInt(page.params.id ?? '', 10));
  const isV2 = $derived(uiVersion.isV2);
  const showTimeline = $derived(isV2 && Number.isFinite(songId));

  $effect(() => {
    if (showTimeline) return;
    // Invalid id → library; valid id but v1 → the v1 provenance view.
    if (!Number.isFinite(songId)) {
      void goto('/library', { replaceState: true });
    } else {
      void goto(`/review?song=${songId}`, { replaceState: true });
    }
  });
</script>

{#if showTimeline}
  <TrackTimelineV2 {songId} />
{/if}
