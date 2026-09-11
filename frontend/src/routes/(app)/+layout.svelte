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
  import { resolveNav } from '$lib/nav';
  import { isAdmin } from '$lib/auth/capabilities';

  type Props = { children: Snippet };
  const { children }: Props = $props();

  // Non-admins browse the SAME Listen routes and components. There is no data-layer switch any
  // more: the ordinary endpoints already return their own rows plus whatever was shared with
  // them. This flag only hides administration chrome that would render empty or 403.
  const isFriendSession = $derived(!isAdmin(page.data.user));

  // The (app) group is ssr=false and the pages render their content through shared
  // components, so set the browser-tab title here in one place rather than in every
  // +page.svelte. Taken from the shared nav, so the tab always reads the same label
  // the sidebar highlights — a hand-kept map here used to miss routes silently
  // (/playlists had no entry and its tab just read "MusicHoarder").
  const pageTitle = $derived.by(() => {
    // A track page belongs to the Listen group but is not one of its items; name the
    // thing you're looking at rather than the group.
    if (page.url.pathname.startsWith('/track/')) return 'Track · MusicHoarder';
    const match = resolveNav(page.url);
    const label = match?.item?.label ?? match?.group.label;
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
  // Friends have no pipeline — skip the SSE subscription entirely.
  $effect(() => {
    if (!isFriendSession) return pipelineOverlay.mount();
  });

  // The player owns its audio element in JS (not the DOM), so warm it up once for
  // the session — it then survives every re-render and navigation. Handing it the
  // account id also brings back the queue a reload interrupted; the store keys the
  // snapshot on it so an account switch (always a hard reload) starts clean.
  $effect(() => initPlayer(page.data.user?.id));

  const drawerOpen = $derived(pipelineOverlay.isOpen);
</script>

<svelte:head>
  <title>{pageTitle}</title>
</svelte:head>

<AppShellV2>
  {@render children()}
</AppShellV2>

<CommandPalette />

{#if drawerOpen && !isFriendSession}
  <ImportPipelineDrawer />
{/if}
