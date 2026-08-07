<script lang="ts">
  import { page } from '$app/state';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { resolveNav } from '$lib/nav';
  import SectionTabsV2 from './SectionTabsV2.svelte';

  // The section tab bar lives here (in the shell) rather than in each page, so
  // it stays pinned and identical across every route in a group — switching
  // tabs only moves the active highlight, never the bar itself.
  //
  // Tabs are the matched group's items, so the strip is always a complete map of
  // its group and can't drift from the sidebar. Two cases render nothing: routes
  // outside the shell, and Inbox — InboxV2 draws its own ?tab= bar with live
  // per-tab counts, so a shell strip would double it.
  const match = $derived(resolveNav(page.url));
  const nav = $derived(match && !match.group.ownsSubNav ? match : null);
</script>

{#if nav}
  <SectionTabsV2
    tabs={nav.group.items}
    active={nav.item?.id ?? ''}
    label="{nav.group.label} views"
    running={pipelineOverlay.isAnyRunning}
  />
{/if}
