<script lang="ts">
  import type { Snippet } from 'svelte';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import AppSidebar from '$lib/components/AppSidebar.svelte';
  import AppHeader from '$lib/components/AppHeader.svelte';
  import MiniPlayer from '$lib/components/MiniPlayer.svelte';
  import ImportPipelineDrawer from '$lib/components/pipeline/ImportPipelineDrawer.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { cn } from '$lib/utils';

  type Props = { children: Snippet };
  const { children }: Props = $props();

  // Subscribe to the pipeline progress stream while the layout is mounted so
  // the AppHeader pill can pulse during running jobs even with the drawer closed.
  $effect(() => pipelineOverlay.mount());

  const drawerOpen = $derived(pipelineOverlay.isOpen);
  const playerPad = $derived(playerStore.currentSong && !playerStore.isPanelMounted);
</script>

<Sidebar.Provider>
  <AppSidebar />
  <Sidebar.Inset
    class={cn(
      'bg-background h-svh',
      playerPad && !drawerOpen && 'pb-[60px] sm:pb-[68px]',
      drawerOpen && 'pb-[340px]'
    )}
  >
    <AppHeader />
    {@render children()}
  </Sidebar.Inset>
</Sidebar.Provider>

<!-- Always mount MiniPlayer so its hidden audio element persists across nav.
     MiniPlayer hides its UI internally when the in-page TrackPanel is mounted. -->
<MiniPlayer />

{#if drawerOpen}
  <ImportPipelineDrawer />
{/if}
