<script lang="ts">
  import type { Snippet } from 'svelte';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import AppSidebar from '$lib/components/AppSidebar.svelte';
  import AppHeader from '$lib/components/AppHeader.svelte';
  import MiniPlayer from '$lib/components/MiniPlayer.svelte';
  import MobileTabBar from '$lib/components/mobile/MobileTabBar.svelte';
  import LibraryOfflineBanner from '$lib/components/LibraryOfflineBanner.svelte';
  import QualityGradingErrorBanner from '$lib/components/QualityGradingErrorBanner.svelte';
  import ImportPipelineDrawer from '$lib/components/pipeline/ImportPipelineDrawer.svelte';
  import CommandPalette from '$lib/components/CommandPalette.svelte';
  import AppShellV2 from '$lib/components/v2/AppShellV2.svelte';
  import { playerStore, initPlayer } from '$lib/stores/player.svelte';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { commandPalette } from '$lib/stores/command-palette.svelte';
  import { uiVersion } from '$lib/stores/ui-version.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { cn } from '$lib/utils';

  type Props = { children: Snippet };
  const { children }: Props = $props();

  const isMobile = new IsMobile();

  // Global Cmd/Ctrl+K opens the "search everywhere" command palette.
  $effect(() => {
    function onKeydown(e: KeyboardEvent) {
      if ((e.metaKey || e.ctrlKey) && e.key.toLowerCase() === 'k') {
        e.preventDefault();
        commandPalette.toggle();
      }
    }
    window.addEventListener('keydown', onKeydown);
    return () => window.removeEventListener('keydown', onKeydown);
  });

  // Subscribe to the pipeline progress stream while the layout is mounted so
  // the AppHeader pill can pulse during running jobs even with the drawer closed.
  $effect(() => pipelineOverlay.mount());

  // The player owns its audio element in JS (not the DOM), so warm it up once
  // for the session — it then survives every re-render and navigation.
  $effect(() => initPlayer());

  const drawerOpen = $derived(pipelineOverlay.isOpen);
  const playerPad = $derived(playerStore.currentSong && !playerStore.isPanelMounted);

  // The v2 redesign is an in-place shell swap (see ui-version store). It only
  // takes over the desktop chrome; mobile keeps the existing tab-bar layout for
  // both versions (v2 mobile is a later phase). The page body — children() — is
  // rendered exactly once per branch so navigation/resize never refetches.
  const useV2Shell = $derived(uiVersion.isV2 && !isMobile.current);
</script>

<!-- Render children() exactly once so resizing across the mobile breakpoint only
     swaps the surrounding chrome (sidebar/header vs bottom tab bar) — the page itself
     is never destroyed and recreated, avoiding a refetch/loading flash on resize. -->
{#if useV2Shell}
  <AppShellV2>
    {@render children()}
  </AppShellV2>
{:else}
  <Sidebar.Provider>
    {#if !isMobile.current}
      <AppSidebar />
    {/if}
    <Sidebar.Inset
      class={cn(
        'bg-background h-svh min-w-0',
        isMobile.current
          ? 'overflow-hidden'
          : cn(playerPad && !drawerOpen && 'pb-[60px] sm:pb-[68px]', drawerOpen && 'pb-[340px]')
      )}
    >
      {#if !isMobile.current}
        <AppHeader />
        <LibraryOfflineBanner />
        <QualityGradingErrorBanner />
      {/if}
      <div class="flex min-h-0 flex-1 flex-col overflow-hidden">
        {@render children()}
      </div>
      {#if isMobile.current}
        <MobileTabBar />
      {/if}
    </Sidebar.Inset>
  </Sidebar.Provider>
{/if}

<!-- MiniPlayer is the global playback UI; it hides itself when the in-page
     TrackPanel is mounted. Its audio element is owned by the store (not the
     DOM), so playback survives re-renders, navigation, and resize. -->
<MiniPlayer mobileInset={isMobile.current} />

<CommandPalette />

{#if drawerOpen && !isMobile.current}
  <ImportPipelineDrawer />
{/if}
