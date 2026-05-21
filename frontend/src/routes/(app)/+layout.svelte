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

{#if isMobile.current}
  <!-- Mobile shell: dedicated bottom tab navigation, no desktop sidebar/header. -->
  <div class="bg-background flex h-svh flex-col overflow-hidden">
    <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
      {@render children()}
    </div>
    <MobileTabBar />
  </div>
  <MiniPlayer mobileInset />
{:else}
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
      <LibraryOfflineBanner />
      {@render children()}
    </Sidebar.Inset>
  </Sidebar.Provider>

  <!-- Always mount MiniPlayer so its hidden audio element persists across nav.
       MiniPlayer hides its UI internally when the in-page TrackPanel is mounted. -->
  <MiniPlayer />

  {#if drawerOpen}
    <ImportPipelineDrawer />
  {/if}
{/if}
