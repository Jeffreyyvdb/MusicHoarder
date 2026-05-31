<script lang="ts">
  import { goto } from '$app/navigation';
  import { uiVersion } from '$lib/stores/ui-version.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import PipelineHomeV2 from '$lib/components/v2/PipelineHomeV2.svelte';

  const isMobile = new IsMobile();

  // /pipeline is a v2-only route. When the design flag is 'v1' it has no
  // equivalent, so redirect to the v1 conveyor (Ingest history at /runs) and
  // never dead-end a deep link. v2 mobile is a later phase, so mobile users also
  // fall back to /runs. The flag is client-only (localStorage) and the (app)
  // group is ssr=false, so guard on mount in an effect.
  const isV2 = $derived(uiVersion.isV2);
  const showHome = $derived(isV2 && !isMobile.current);

  $effect(() => {
    if (!showHome) void goto('/runs', { replaceState: true });
  });
</script>

{#if showHome}
  <PipelineHomeV2 />
{/if}
