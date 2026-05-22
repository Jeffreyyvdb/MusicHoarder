<script lang="ts">
  import type { Snippet } from 'svelte';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import AppSidebar from '$lib/components/AppSidebar.svelte';
  import AppHeader from '$lib/components/AppHeader.svelte';
  import MiniPlayer from '$lib/components/MiniPlayer.svelte';
  import MobileTabBar from '$lib/components/mobile/MobileTabBar.svelte';
  import LibraryOfflineBanner from '$lib/components/LibraryOfflineBanner.svelte';
  import ImportPipelineDrawer from '$lib/components/pipeline/ImportPipelineDrawer.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { cn } from '$lib/utils';

  type Props = { children: Snippet };
  const { children }: Props = $props();

  const isMobile = new IsMobile();

  // Subscribe to the pipeline progress stream while the layout is mounted so
  // the AppHeader pill can pulse during running jobs even with the drawer closed.
  $effect(() => pipelineOverlay.mount());

  const drawerOpen = $derived(pipelineOverlay.isOpen);
  const playerPad = $derived(playerStore.currentSong && !playerStore.isPanelMounted);
</script>

<!-- Render children() exactly once so resizing across the mobile breakpoint only
     swaps the surrounding chrome (sidebar/header vs bottom tab bar) — the page itself
     is never destroyed and recreated, avoiding a refetch/loading flash on resize. -->
<Sidebar.Provider>
  {#if !isMobile.current}
    <AppSidebar />
  {/if}
  <Sidebar.Inset
    class={cn(
      'bg-background h-svh',
      isMobile.current
        ? 'overflow-hidden'
        : cn(playerPad && !drawerOpen && 'pb-[60px] sm:pb-[68px]', drawerOpen && 'pb-[340px]')
    )}
  >
    {#if !isMobile.current}
      <AppHeader />
      <LibraryOfflineBanner />
    {/if}
    <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
      {@render children()}
    </div>
    {#if isMobile.current}
      <MobileTabBar />
    {/if}
  </Sidebar.Inset>
</Sidebar.Provider>

<!-- Always mount MiniPlayer so its hidden audio element persists across nav and
     resize. MiniPlayer hides its UI internally when the in-page TrackPanel is mounted. -->
<MiniPlayer mobileInset={isMobile.current} />

{#if drawerOpen && !isMobile.current}
  <ImportPipelineDrawer />
{/if}
