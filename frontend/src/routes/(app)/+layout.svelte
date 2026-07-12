<script lang="ts">
  import type { Snippet } from 'svelte';
  import { page } from '$app/state';
  import ImportPipelineDrawer from '$lib/components/pipeline/ImportPipelineDrawer.svelte';
  import CommandPalette from '$lib/components/CommandPalette.svelte';
  import AppShellV2 from '$lib/components/v2/AppShellV2.svelte';
  import { initPlayer } from '$lib/stores/player.svelte';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { commandPalette } from '$lib/stores/command-palette.svelte';
  import { songDetail } from '$lib/stores/song-detail.svelte';

  type Props = { children: Snippet };
  const { children }: Props = $props();

  // The (app) group is ssr=false and the pages render their content through shared
  // components, so set the browser-tab title here in one place rather than in every
  // +page.svelte. Labels mirror the sidebar nav so the tab matches the active item.
  const PAGE_TITLES: Record<string, string> = {
    '/stats': 'Stats',
    '/pipeline': 'Pipeline',
    '/directories': 'By folder',
    '/quality': 'AI quality',
    '/album-quality': 'Album quality',
    '/performance': 'Performance',
    '/inbox': 'Inbox',
    '/overview': 'Overview',
    '/library': 'Library',
    '/artists': 'Artists',
    '/tracks': 'Tracks',
    '/liked': 'Liked songs',
    '/spotify': 'Spotify',
    '/wishlist': 'Wishlist',
    '/history': 'History',
    '/settings': 'Settings'
  };

  const pageTitle = $derived.by(() => {
    const path = page.url.pathname;
    const label = path.startsWith('/track/') ? 'Track' : PAGE_TITLES[path];
    return label ? `${label} · MusicHoarder` : 'MusicHoarder';
  });

  // Global Cmd/Ctrl+K opens the "search everywhere" command palette; Cmd/Ctrl+I
  // toggles the song-detail sidebar for the now-playing track (mirrors the nav
  // sidebar's Cmd/Ctrl+B).
  $effect(() => {
    function onKeydown(e: KeyboardEvent) {
      if (e.metaKey || e.ctrlKey) {
        const key = e.key.toLowerCase();
        if (key === 'k') {
          e.preventDefault();
          commandPalette.toggle();
        } else if (key === 'i') {
          e.preventDefault();
          songDetail.toggle();
        }
      }
    }
    window.addEventListener('keydown', onKeydown);
    return () => window.removeEventListener('keydown', onKeydown);
  });

  // Subscribe to the pipeline progress stream while the layout is mounted so the
  // header/sidebar can pulse during running jobs even with the drawer closed.
  $effect(() => pipelineOverlay.mount());

  // The player owns its audio element in JS (not the DOM), so warm it up once for
  // the session — it then survives every re-render and navigation.
  $effect(() => initPlayer());

  const drawerOpen = $derived(pipelineOverlay.isOpen);
</script>

<svelte:head>
  <title>{pageTitle}</title>
</svelte:head>

<AppShellV2>
  {@render children()}
</AppShellV2>

<CommandPalette />

{#if drawerOpen}
  <ImportPipelineDrawer />
{/if}
