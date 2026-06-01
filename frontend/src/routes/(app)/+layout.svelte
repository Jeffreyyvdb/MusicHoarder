<script lang="ts">
  import type { Snippet } from 'svelte';
  import MiniPlayer from '$lib/components/MiniPlayer.svelte';
  import ImportPipelineDrawer from '$lib/components/pipeline/ImportPipelineDrawer.svelte';
  import CommandPalette from '$lib/components/CommandPalette.svelte';
  import AppShellV2 from '$lib/components/v2/AppShellV2.svelte';
  import { initPlayer } from '$lib/stores/player.svelte';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { commandPalette } from '$lib/stores/command-palette.svelte';

  type Props = { children: Snippet };
  const { children }: Props = $props();

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

  // Subscribe to the pipeline progress stream while the layout is mounted so the
  // header/sidebar can pulse during running jobs even with the drawer closed.
  $effect(() => pipelineOverlay.mount());

  // The player owns its audio element in JS (not the DOM), so warm it up once for
  // the session — it then survives every re-render and navigation.
  $effect(() => initPlayer());

  const drawerOpen = $derived(pipelineOverlay.isOpen);
</script>

<AppShellV2>
  {@render children()}
</AppShellV2>

<!-- MiniPlayer is the global playback UI; it hides itself when the in-page
     TrackPanel is mounted. Its audio element is owned by the store (not the
     DOM), so playback survives re-renders, navigation, and resize. -->
<MiniPlayer />

<CommandPalette />

{#if drawerOpen}
  <ImportPipelineDrawer />
{/if}
