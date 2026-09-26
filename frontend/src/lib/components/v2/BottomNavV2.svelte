<script lang="ts">
  import { Search } from '@lucide/svelte';
  import { page } from '$app/state';
  import { tabFor, tabsFor, type NavTab } from '$lib/nav';
  import { tabMemory } from '$lib/stores/tab-memory.svelte';
  import { bottomBar } from '$lib/stores/bottom-bar.svelte';
  import { songDetail } from '$lib/stores/song-detail.svelte';
  import { commandPalette } from '$lib/stores/command-palette.svelte';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { inboxBadgeCount } from '$lib/stores/nav-badges.svelte';
  import { chatStore } from '$lib/stores/chat.svelte';
  import { isAdmin } from '$lib/auth/capabilities';
  import { cn } from '$lib/utils';

  // The compact tab bar: a floating glass capsule of tabs plus a separate search circle (iOS 26's
  // "search tab, button appearance"). Always there below md — members included, whose tabs are
  // Listen's own four pages — except under Now Playing (a modal that covers it) and while a pushed
  // view owns the bottom slot (the Inbox decision toolbar, via the bottomBar store).
  //
  // Tabs come from $lib/nav (tabsFor), so this, the sidebar and the route guard agree on what an
  // account can reach. Each tab keeps its own navigation stack (tab-memory): tapping another tab
  // returns to where you were in it, tapping the active one pops it to its root, and tapping it at
  // its root scrolls the page to the top — the status-bar tap an installed web app does not get.
  const user = $derived(page.data.user);
  const tabs = $derived(tabsFor(user));
  const activeId = $derived.by(() => {
    // tab-memory lights a tapped tab before its page lands; before the first navigation is
    // recorded, the URL decides.
    const remembered = tabMemory.activeTabId;
    if (remembered && tabs.some((t) => t.id === remembered)) return remembered;
    return tabFor(page.url, user)?.id ?? null;
  });
  const visible = $derived(!songDetail.isOpen && bottomBar.kind === 'tabs');

  // Admins only: a demo session has no pipeline stream, so its dot could only ever lie.
  const running = $derived(isAdmin(user) && pipelineOverlay.isAnyRunning);
  // The same figure as the sidebar's Inbox badge and the sum of the Inbox hub's first section —
  // see nav-badges.svelte.ts.
  const inboxBadge = $derived(inboxBadgeCount());

  function badgeFor(tab: NavTab): number | null {
    if (tab.badge === 'chats') return chatStore.unreadTotal > 0 ? chatStore.unreadTotal : null;
    return tab.badge === 'inbox' && inboxBadge != null && inboxBadge > 0 ? inboxBadge : null;
  }

  function onTabClick(event: MouseEvent, tab: NavTab) {
    // A modified click (new tab/window) keeps the link's native behaviour.
    if (event.metaKey || event.ctrlKey || event.shiftKey || event.altKey || event.button !== 0)
      return;
    event.preventDefault();
    void tabMemory.select(tab, page.url, user);
  }
</script>

{#if visible}
  <!-- The row itself lets touches through between the capsule and the circle; only the two glass
       shapes take them. Positioned from the shared geometry vars (app.css), like the MiniPlayer
       that docks above it and the content padding that clears both. -->
  <nav
    aria-label="Tabs"
    class="pointer-events-none fixed inset-x-0 bottom-(--mh-tabbar-offset) z-40 flex gap-2 pr-[max(16px,env(safe-area-inset-right))] pl-[max(16px,env(safe-area-inset-left))] md:hidden"
  >
    <div
      class="mh-glass mh-chrome pointer-events-auto flex h-(--mh-tabbar-h) min-w-0 flex-1 items-stretch rounded-full p-1"
    >
      {#each tabs as tab (tab.id)}
        {@const isActive = tab.id === activeId}
        {@const live = Boolean(tab.live && running)}
        {@const badge = badgeFor(tab)}
        {@const extras = [
          live ? 'pipeline running' : null,
          badge != null
            ? `${badge > 99 ? 'more than 99' : badge} ${tab.badge === 'chats' ? 'unread messages' : 'items need review'}`
            : null
        ].filter((s) => s != null)}
        <a
          href={tabMemory.hrefFor(tab)}
          onclick={(e) => onTabClick(e, tab)}
          data-active={isActive || undefined}
          aria-current={isActive ? 'page' : undefined}
          aria-label={extras.length ? `${tab.label}, ${extras.join(', ')}` : undefined}
          class={cn(
            'relative flex min-w-0 flex-1 flex-col items-center justify-center gap-0.5 rounded-full outline-none',
            'text-foreground data-[active=true]:bg-foreground/[0.08] data-[active=true]:text-tab-active',
            'focus-visible:ring-ring focus-visible:ring-2 focus-visible:ring-inset'
          )}
        >
          <tab.icon
            class="size-6 shrink-0"
            strokeWidth={isActive ? 2.25 : 1.75}
            aria-hidden="true"
          />
          <span class="text-caption-2 max-w-full truncate px-1">{tab.label}</span>
          {#if badge != null}
            <span
              class="bg-destructive text-destructive-foreground text-caption-2 absolute top-[3px] left-[calc(50%+4px)] grid h-[18px] min-w-[18px] place-items-center rounded-full px-[5px] font-semibold tabular-nums"
              aria-hidden="true">{badge > 99 ? '99+' : badge}</span
            >
          {/if}
          {#if live}
            <span
              class="bg-primary mh-v2-pulse absolute top-[7px] left-[calc(50%+11px)] size-2 rounded-full"
              aria-hidden="true"
            ></span>
          {/if}
        </a>
      {/each}
    </div>
    <!-- Search opens the command palette (a documented departure: a Spotlight-shaped dialog rather
         than a field docked above the keyboard). Warms the songs dataset on touch-down so results
         are there by the time it opens. -->
    <button
      type="button"
      aria-label="Search"
      class="mh-glass mh-chrome text-foreground focus-visible:ring-ring pointer-events-auto grid size-(--mh-tabbar-h) shrink-0 place-items-center rounded-full outline-none focus-visible:ring-2"
      onpointerdown={() => songsStore.ensureLoaded()}
      onfocus={() => songsStore.ensureLoaded()}
      onclick={() => commandPalette.setOpen(true)}
    >
      <Search class="size-6" strokeWidth={1.75} aria-hidden="true" />
    </button>
  </nav>
{/if}
