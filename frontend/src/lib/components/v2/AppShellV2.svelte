<script lang="ts">
  import type { Snippet } from 'svelte';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import AppSidebarV2 from '$lib/components/v2/AppSidebarV2.svelte';
  import AppTopBarV2 from '$lib/components/v2/AppTopBarV2.svelte';
  import BottomNavV2 from '$lib/components/v2/BottomNavV2.svelte';
  import MiniPlayer from '$lib/components/MiniPlayer.svelte';
  import SongDetailHost from '$lib/components/v2/SongDetailHost.svelte';
  import SectionSubNav from '$lib/components/v2/SectionSubNav.svelte';
  import LibraryOfflineBanner from '$lib/components/LibraryOfflineBanner.svelte';
  import QualityGradingErrorBanner from '$lib/components/QualityGradingErrorBanner.svelte';
  import VersionUpdateBanner from '$lib/components/VersionUpdateBanner.svelte';
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

  // Banner policy: at most ONE banner renders at a time — offline (pipeline is
  // actually paused) outranks the grading error, which outranks the update
  // notice. Each banner reports its underlying state up; lower priorities are
  // suppressed while a higher one is visible.
  let offlineVisible = $state(false);
  let gradingErrorVisible = $state(false);
</script>

<Sidebar.Provider>
  <AppSidebarV2 />
  <Sidebar.Inset class={cn('bg-background h-svh min-w-0', drawerOpen && 'md:pb-[340px]')}>
    <AppTopBarV2 />
    <LibraryOfflineBanner bind:visible={offlineVisible} />
    <QualityGradingErrorBanner bind:visible={gradingErrorVisible} suppressed={offlineVisible} />
    <VersionUpdateBanner suppressed={offlineVisible || gradingErrorVisible} />
    <!-- Page content scrolls *behind* the floating MiniPlayer / mobile bottom nav
         so the frosted glass reveals moving content. Rather than reserving dead
         space on the inset (which left the bar over blank background), we publish
         the clearance as `--mh-content-pad`; each scroll viewport consumes it as
         trailing padding so the last items still clear the chrome. The mobile nav
         is always present (80px); the player adds ~60px on top when showing. -->
    <div
      data-mh-content
      class={cn(
        'flex min-h-0 flex-1 flex-col overflow-hidden',
        playerPad
          ? '[--mh-content-pad:calc(140px_+_max(env(safe-area-inset-bottom),var(--mh-vv-bottom,0px)))]'
          : '[--mh-content-pad:calc(80px_+_max(env(safe-area-inset-bottom),var(--mh-vv-bottom,0px)))]',
        playerPad && !drawerOpen ? 'md:[--mh-content-pad:88px]' : 'md:[--mh-content-pad:0px]'
      )}
    >
      <SectionSubNav />
      {@render children()}
    </div>
  </Sidebar.Inset>
  <!-- Global song-detail sidebar. On desktop it's a floating right-docked pane
       that pushes the inset (a flex sibling shrinks the flex-1 Sidebar.Inset);
       on mobile it's a bottom Sheet. Opened from the MiniPlayer, Library track
       rows, deep-links, and Cmd/Ctrl+I — never via navigation. -->
  <SongDetailHost />
  <BottomNavV2 />
  <!-- MiniPlayer is the global playback UI; it hides itself when the in-page
       TrackPanel is mounted. Its audio element is owned by the store (not the
       DOM), so playback survives re-renders, navigation, and resize. Mounted
       inside the provider — as a following sibling of the `peer` sidebar — so it
       can read `--sidebar-width` / the sidebar's `data-state` to inset the
       desktop bar to the content area. -->
  <MiniPlayer />
</Sidebar.Provider>
