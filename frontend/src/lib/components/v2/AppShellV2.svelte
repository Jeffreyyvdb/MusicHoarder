<script lang="ts">
  import { untrack, type Snippet } from 'svelte';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import AppSidebarV2 from '$lib/components/v2/AppSidebarV2.svelte';
  import AppTopBarV2 from '$lib/components/v2/AppTopBarV2.svelte';
  import BottomNavV2 from '$lib/components/v2/BottomNavV2.svelte';
  import MiniPlayer from '$lib/components/MiniPlayer.svelte';
  import SongDetailHost from '$lib/components/v2/SongDetailHost.svelte';
  import StorageBreakdownDialog from '$lib/components/v2/StorageBreakdownDialog.svelte';
  import LibraryOfflineBanner from '$lib/components/LibraryOfflineBanner.svelte';
  import QualityGradingErrorBanner from '$lib/components/QualityGradingErrorBanner.svelte';
  import VersionUpdateBanner from '$lib/components/VersionUpdateBanner.svelte';
  import { page } from '$app/state';
  import { fetchOverview, type ApiOverview } from '$lib/api-client';
  import { edgeSwipeBack } from '$lib/actions/edge-swipe-back';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { storageUsage } from '$lib/stores/storage-usage.svelte';
  import { bottomBar } from '$lib/stores/bottom-bar.svelte';
  import { cn } from '$lib/utils';
  import { isAdmin } from '$lib/auth/capabilities';
  import { navGroupsFor, surfaceFor } from '$lib/nav';
  import { refreshInboxQueueCounts } from '$lib/stores/nav-badges.svelte';

  type Props = { children: Snippet };
  const { children }: Props = $props();

  // The three banners report pipeline/grading/update state — owner concerns their endpoints would
  // just 403/empty for anyone else. Demo counts as "anyone else" here even though it keeps the
  // full nav: it has no pipeline stream, no storage, no Add-from-link.
  const isFriend = $derived(!isAdmin(page.data.user));

  const isMobile = new IsMobile();

  // ── warm-ups ───────────────────────────────────────────────────────────────
  // The shell is the one component every app page mounts at every width, so it warms the shared
  // data the chrome reads (this used to live in the sidebar, which no longer mounts on a phone):
  // the songs dataset for everyone — the tab-bar badge, the command palette and the song-detail
  // overlay all resolve against it — and, for admins, the storage snapshot and one overview. The
  // overview is handed to the pipeline store, so the sidebar, the Manage hub and the account panel
  // all read that one copy (the live poll replaces it with fresher ones while a job runs).
  // The pipeline stream only runs while something asks for it (the drawer, the Pipeline page, a
  // running job it has already seen), so a job that was running when the app opened would never
  // light the live dots. The overview says so; attach the stream just long enough to hear from
  // it — from then on the store keeps itself alive while the job runs and idles after it.
  let releaseSeed: (() => void) | null = null;
  function dropSeed() {
    releaseSeed?.();
    releaseSeed = null;
  }
  $effect(() => {
    // untrack: ensureLoaded reads the same loading flags its own fetch writes, and a tracked read
    // would re-run this effect (and its overview call).
    untrack(() => songsStore.ensureLoaded());
    if (isFriend) return;
    // Shared with the sidebar footer, the Manage hub and the breakdown dialog: one number.
    untrack(() => storageUsage.ensureLoaded());
    let cancelled = false;
    void fetchOverview()
      .then((result: ApiOverview) => {
        if (cancelled) return;
        pipelineOverlay.seedOverview(result);
        if (result.job?.status === 'running' && !pipelineOverlay.snapshot) {
          releaseSeed = pipelineOverlay.keepLive();
        }
      })
      .catch(() => {
        /* the footer lines and the seed are simply skipped */
      });
    return () => {
      cancelled = true;
      dropSeed();
    };
  });
  $effect(() => {
    if (pipelineOverlay.snapshot) untrack(dropSeed);
  });

  // The Inbox badge adds the Duplicate tracks and AI flagged queues to Tag review, and those two
  // figures need requests of their own (nav-badges.svelte.ts). Whoever has an Inbox (admin and
  // demo) fetches them once at start, and again on every entry to and exit from the Inbox — the
  // moments a decision there can have changed them.
  const hasInbox = $derived(navGroupsFor(page.data.user).some((g) => g.id === 'inbox'));
  const inInbox = $derived(page.url.pathname.startsWith('/inbox'));
  let wasInInbox: boolean | null = null;
  $effect(() => {
    if (!hasInbox) return;
    const now = inInbox;
    untrack(() => {
      void refreshInboxQueueCounts(wasInInbox !== null && wasInInbox !== now);
      wasInInbox = now;
    });
  });

  // ── bottom geometry (app.css) ─────────────────────────────────────────────
  // What occupies the bottom of the screen, as data attributes on the Sidebar.Provider wrapper;
  // app.css derives --mh-content-pad and every floating bar's offset from them.
  const drawerOpen = $derived(!isFriend && pipelineOverlay.isOpen);
  const playerShowing = $derived(
    Boolean(
      playerStore.currentSong && !playerStore.isPanelMounted && !playerStore.isMiniPlayerDismissed
    )
  );

  // A grouped page (a hub, Settings) paints its own background, but the column behind it shows
  // through the safe-area strip and the rubber-band overscroll; match it so neither flashes white.
  const grouped = $derived(surfaceFor(page.url, isMobile.current) === 'grouped');

  // Banner policy: at most ONE banner renders at a time — offline (pipeline is
  // actually paused) outranks the grading error, which outranks the update
  // notice. Each banner reports its underlying state up; lower priorities are
  // suppressed while a higher one is visible.
  let offlineVisible = $state(false);
  let contentEl = $state<HTMLElement | null>(null);
  let gradingErrorVisible = $state(false);
</script>

<Sidebar.Provider
  data-mh-bar={bottomBar.kind}
  data-mh-player={playerShowing ? '1' : '0'}
  data-mh-drawer={drawerOpen ? '1' : '0'}
>
  <!-- Skip link: the first Tab stop on every page, hidden until focused. Without it a keyboard
       user walked ~30 sidebar and top-bar stops before reaching any page content. It moves focus
       rather than following the hash, so no navigation lands on the tab stacks. -->
  <a
    href="#mh-content"
    onclick={(e) => {
      e.preventDefault();
      contentEl?.focus();
    }}
    class="bg-background text-primary ring-ring sr-only z-[90] rounded-full px-4 py-2 text-sm font-medium shadow-lg ring-2 focus:not-sr-only focus:fixed focus:top-3 focus:left-3"
  >
    Skip to content
  </a>
  <AppSidebarV2 />
  <!-- On compact the global top bar is gone (each page's nav bar replaces it), so the column owns
       the top safe-area inset once: 0 in the installed app with its `default` status bar, needed
       in fullscreen and landscape. The md+ top bar pads it itself. -->
  <Sidebar.Inset
    class={cn(
      'h-svh min-w-0 max-md:pt-[env(safe-area-inset-top)]',
      grouped ? 'bg-background-grouped' : 'bg-background',
      drawerOpen && 'md:pb-[340px]'
    )}
  >
    <AppTopBarV2 />
    {#if !isFriend}
      <LibraryOfflineBanner bind:visible={offlineVisible} />
      <QualityGradingErrorBanner bind:visible={gradingErrorVisible} suppressed={offlineVisible} />
      <VersionUpdateBanner suppressed={offlineVisible || gradingErrorVisible} />
    {/if}
    <!-- Page content scrolls *behind* the floating tab bar and MiniPlayer so the glass reveals
         moving content. The clearance they need is --mh-content-pad (app.css, derived from the
         data attributes on the provider above); every scroll viewport consumes it as trailing
         padding so the last items still clear the chrome. The edge-swipe-back gesture (installed
         app only) moves this column, so it is the content root. -->
    <div
      bind:this={contentEl}
      id="mh-content"
      tabindex="-1"
      data-mh-content
      use:edgeSwipeBack
      class="flex min-h-0 flex-1 flex-col overflow-hidden outline-none"
    >
      {@render children()}
    </div>
  </Sidebar.Inset>
  <!-- The global Now Playing / song-detail overlay: a portaled full-screen dialog, opened from the
       MiniPlayer, track rows, the command palette and Cmd/Ctrl+I — never via navigation. Mounted
       inside the provider only for its context. -->
  <SongDetailHost />
  <!-- Storage breakdown, opened from the sidebar footer, the Manage hub and the account panel. -->
  {#if !isFriend}
    <StorageBreakdownDialog />
  {/if}
  <BottomNavV2 />
  <!-- MiniPlayer is the global playback UI; it hides itself while Now Playing is open. Its audio
       element is owned by the store (not the DOM), so playback survives re-renders, navigation
       and resize. It positions from the same geometry vars as the tab bar. -->
  <MiniPlayer />
</Sidebar.Provider>
