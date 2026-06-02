<script lang="ts">
  import { page } from '$app/state';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { resolveSectionSubNav } from '$lib/section-nav';
  import PipelineSubNavV2 from './PipelineSubNavV2.svelte';

  // The section tab bar lives here (in the shell) rather than in each page, so
  // it stays pinned and identical across every route in a section — switching
  // tabs only moves the active highlight, never the bar itself.
  const nav = $derived(resolveSectionSubNav(page.url.pathname));
</script>

{#if nav}
  <PipelineSubNavV2 tabs={nav.tabs} active={nav.active} running={pipelineOverlay.isAnyRunning} />
{/if}
