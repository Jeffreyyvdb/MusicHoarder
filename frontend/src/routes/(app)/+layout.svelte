<script lang="ts">
  import type { Snippet } from 'svelte';
  import { afterNavigate, beforeNavigate } from '$app/navigation';
  import { page } from '$app/state';
  import ImportPipelineDrawer from '$lib/components/pipeline/ImportPipelineDrawer.svelte';
  import CommandPalette from '$lib/components/CommandPalette.svelte';
  import AppShellV2 from '$lib/components/v2/AppShellV2.svelte';
  import { initPlayer, playerStore } from '$lib/stores/player.svelte';
  import { playbackSync } from '$lib/stores/playback-sync.svelte';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { commandPalette } from '$lib/stores/command-palette.svelte';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { tabMemory } from '$lib/stores/tab-memory.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { isInboxHub, resolveNav } from '$lib/nav';
  import { isAdmin } from '$lib/auth/capabilities';

  type Props = { children: Snippet };
  const { children }: Props = $props();

  // Non-admins browse the SAME Listen routes and components. There is no data-layer switch any
  // more: the ordinary endpoints already return their own rows plus whatever was shared with
  // them. This flag only hides administration chrome that would render empty or 403.
  const isFriendSession = $derived(!isAdmin(page.data.user));

  const isMobile = new IsMobile();

  // The (app) group is ssr=false and the pages render their content through shared
  // components, so set the browser-tab title here in one place rather than in every
  // +page.svelte. Taken from the shared nav, so the tab always reads the same label
  // the sidebar highlights — a hand-kept map here used to miss routes silently
  // (/playlists had no entry and its tab just read "MusicHoarder").
  //
  // A drill-in leads with the thing on screen — "Nightswim · Albums · MusicHoarder" — because
  // SvelteKit's route announcer reads this title on every navigation, and VoiceOver saying
  // "Albums" as an album opens names the wrong page. The name is the one the page recorded for its
  // Back label (tabMemory.setTitle), else what the URL names directly.
  const sectionLabel = $derived.by(() => {
    // A track page belongs to the Listen group but is not one of its items.
    if (page.url.pathname.startsWith('/track/')) return 'Timeline';
    // A bare /inbox is the list of queues on a phone (InboxV2 renders the hub there), not Tag
    // review — which is what resolveNav answers, because a desktop opens on the first queue.
    if (isMobile.current && isInboxHub(page.url)) return 'Inbox';
    const match = resolveNav(page.url);
    return match?.item?.label ?? match?.group.label ?? null;
  });
  const pageName = $derived.by(() => {
    const own = tabMemory.titleOf(page.url);
    if (own) return own;
    const params = page.url.searchParams;
    if (page.url.pathname === '/library') {
      const album = params.get('album');
      if (album) return songsStore.albums.find((a) => a.key === album)?.title ?? null;
      return params.get('artist')?.trim() || null;
    }
    const track = /^\/track\/(\d+)/.exec(page.url.pathname);
    if (track) return songsStore.songsById.get(Number(track[1]))?.title ?? null;
    return null;
  });
  const pageTitle = $derived(
    [pageName !== sectionLabel ? pageName : null, sectionLabel, 'MusicHoarder']
      .filter(Boolean)
      .join(' · ')
  );

  // Per-tab navigation stacks (tab-memory): every completed navigation is folded into the stack
  // of the tab that owns it, which is what the tab bar and each nav bar's Back read. The scroll
  // position is taken on the way out, because this app scrolls inside its pages and SvelteKit
  // only restores the window's. Keyed per account; record() loads the right one itself, init()
  // just does it before the first navigation lands.
  $effect(() => tabMemory.init(page.data.user?.id));
  beforeNavigate(({ from }) => {
    if (from) tabMemory.captureScroll(from.url);
  });
  afterNavigate((nav) => tabMemory.record(nav, page.data.user));

  // Space plays and pauses, as it does in every media app — but only when nothing else claims the
  // key: a focused control activates on Space (a button, a lyric line, a switch), a field types a
  // space, and the expanded music video handles Space itself (it cancels the event in the capture
  // phase). Scrolling the page with Space is given up only while something is loaded to play.
  const SPACE_OWNERS =
    'a[href], button, input, textarea, select, summary, [contenteditable]:not([contenteditable="false"]), ' +
    '[role="button"], [role="link"], [role="checkbox"], [role="switch"], [role="radio"], [role="tab"], ' +
    '[role="menuitem"], [role="menuitemcheckbox"], [role="menuitemradio"], [role="option"], ' +
    '[role="slider"], [role="textbox"], [role="combobox"], [role="spinbutton"]';

  function spaceTogglesPlayback(e: KeyboardEvent): boolean {
    if (e.key !== ' ' || e.defaultPrevented || e.repeat) return false;
    if (e.metaKey || e.ctrlKey || e.altKey || e.shiftKey) return false;
    if (typeof document !== 'undefined' && document.fullscreenElement) return false;
    const target = e.target instanceof Element ? e.target : null;
    if (target?.closest(SPACE_OWNERS)) return false;
    return playerStore.currentSong != null;
  }

  // Global Cmd/Ctrl+K opens the "search everywhere" command palette; Cmd/Ctrl+I
  // toggles the song-detail overlay for the now-playing track (mirrors the nav
  // sidebar's Cmd/Ctrl+B, which is desktop-only).
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
        return;
      }
      if (spaceTogglesPlayback(e)) {
        e.preventDefault();
        playerStore.togglePlay();
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
  //
  // The account's playback session (one per account, shared by its devices) starts first, so the
  // reload restore can wait for its first snapshot before resuming on its own. Both are idempotent
  // per account; the session never starts for the demo account.
  $effect(() => {
    const user = page.data.user;
    playbackSync.start(user);
    initPlayer(user?.id);
  });
  // Leaving the app shell (signing out, the share page) closes the stream and unplugs the player.
  $effect(() => () => playbackSync.stop());

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
