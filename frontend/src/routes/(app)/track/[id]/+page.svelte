<script lang="ts">
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import TrackTimelineV2 from '$lib/components/v2/TrackTimelineV2.svelte';

  // Per-song provenance timeline. An invalid id can't render anything useful, so
  // bounce back to the library rather than dead-ending the deep link. The (app)
  // group is ssr=false, so guard the redirect in an effect.
  const songId = $derived(Number.parseInt(page.params.id ?? '', 10));
  const valid = $derived(Number.isFinite(songId));

  $effect(() => {
    if (!valid) void goto('/library', { replaceState: true });
  });
</script>

{#if valid}
  <TrackTimelineV2 {songId} />
{/if}
