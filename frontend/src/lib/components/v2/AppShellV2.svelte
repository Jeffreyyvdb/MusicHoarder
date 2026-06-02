<script lang="ts">
  import type { Snippet } from 'svelte';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import AppSidebarV2 from '$lib/components/v2/AppSidebarV2.svelte';
  import AppTopBarV2 from '$lib/components/v2/AppTopBarV2.svelte';
  import BottomNavV2 from '$lib/components/v2/BottomNavV2.svelte';
  import SectionSubNav from '$lib/components/v2/SectionSubNav.svelte';
  import LibraryOfflineBanner from '$lib/components/LibraryOfflineBanner.svelte';
  import QualityGradingErrorBanner from '$lib/components/QualityGradingErrorBanner.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { cn } from '$lib/utils';

  type Props = { children: Snippet };
  const { children }: Props = $props();

  // Mirror the v1 inset padding so the MiniPlayer / pipeline drawer never overlap
  // the page body. Both global widgets stay mounted by the layout, shared across
  // versions.
  const drawerOpen = $derived(pipelineOverlay.isOpen);
  const playerPad = $derived(playerStore.currentSong && !playerStore.isPanelMounted);
</script>

<Sidebar.Provider>
  <AppSidebarV2 />
  <Sidebar.Inset
    class={cn(
      'bg-background h-svh min-w-0',
      // Mobile reserves space for the floating bottom bar (hidden ≥ md); add the
      // MiniPlayer's height on top when it's showing. Desktop keeps the original
      // behaviour via the md: overrides (no bottom bar there).
      !drawerOpen && [
        playerPad
          ? 'pb-[calc(140px+env(safe-area-inset-bottom))]'
          : 'pb-[calc(80px+env(safe-area-inset-bottom))]',
        playerPad ? 'md:pb-[68px]' : 'md:pb-0'
      ],
      drawerOpen && 'pb-[340px]'
    )}
  >
    <AppTopBarV2 />
    <LibraryOfflineBanner />
    <QualityGradingErrorBanner />
    <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
      <SectionSubNav />
      {@render children()}
    </div>
  </Sidebar.Inset>
  <BottomNavV2 />
</Sidebar.Provider>
