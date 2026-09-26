<script lang="ts">
  import { page } from '$app/state';
  import { ChevronDown } from '@lucide/svelte';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import BrandMark from '$lib/components/BrandMark.svelte';
  import { navGroupsFor, resolveNav, type NavGroupId, type NavItem } from '$lib/nav';
  import { APP_HOME } from '$lib/app-home';
  import { isBuiltSong } from '$lib/album-sections';
  import { isTrackListSong } from '$lib/track-list-view.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { inboxCounts, refreshInboxNameCounts } from '$lib/stores/nav-badges.svelte';
  import { chatStore } from '$lib/stores/chat.svelte';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { storageUsage } from '$lib/stores/storage-usage.svelte';
  import { storageSummary } from '$lib/storage-usage-meta';
  import { cn } from '$lib/utils';
  import { isAdmin } from '$lib/auth/capabilities';

  // The running build's version (clean semver), surfaced by the root layout load.
  const version = $derived(page.data.appVersion as string | null | undefined);

  // ── v2 information architecture ───────────────────────────────────────────
  // Four groups — Listen / Inbox / Add / Manage — each with its items listed flush beneath
  // (the shadcn "sidebar-04" docs style). The groups, their items and the active-route rules
  // all live in $lib/nav; this component only renders them and attaches the live counts.
  //
  // Desktop only. Below md the tab bar, the hubs and each page's nav bar carry all of this, and the
  // account lives behind the avatar in the top bar (AccountButton) at every width. The shell warms
  // the data the counts read (songs, storage, the overview, the Inbox queue sizes), so nothing
  // here fetches.
  const sidebar = Sidebar.useSidebar();

  const user = $derived(page.data.user);
  const isFriend = $derived(!isAdmin(user));
  // Members see only the Listen group; the guard bounces them off everything else anyway.
  const navGroups = $derived(navGroupsFor(user));

  const songs = $derived(songsStore.songs);

  // ── derived counts ────────────────────────────────────────────────────────
  // Albums and Artists reflect the clean output only, so their counts are over built
  // (LibraryBuildStatus.Done + destinationPath) songs — matching what those grids list.
  const builtSongs = $derived(songs.filter(isBuiltSong));
  // The Tracks list's own predicate, so this badge and the page's "N tracks" cannot disagree.
  const trackCount = $derived.by(() =>
    songs.length === 0 ? null : songs.filter(isTrackListSong).length
  );
  // Storage is measured on disk by the API (every managed folder, not the DB's source-size sum)
  // and shown against the real volume capacity — the same figure and bar as the Manage hub and
  // the account panel.
  const storage = $derived(storageSummary(storageUsage.snapshot, storageUsage.computing));

  // Live state has ONE source: the pipeline stream (the tab bar and the Manage hub read it too).
  // The overview it carries is the shell's start-up copy until the live poll replaces it.
  const indexing = $derived(!isFriend && pipelineOverlay.isAnyRunning);
  const queueRemaining = $derived.by(() => {
    const job = pipelineOverlay.overview?.job;
    if (!indexing || !job) return null;
    return Math.max(0, (job.tracksDiscovered ?? 0) - (job.tracksProcessed ?? 0));
  });

  // The Inbox group's badge is the tab bar's badge, and each queue's count is its hub row's —
  // one set of figures (nav-badges.svelte.ts), so no two surfaces disagree about a queue. A desktop
  // never shows the Inbox hub (it opens on the first queue), so the sidebar is what asks for the
  // name-merge counts here, while its Inbox group is expanded — a row with no figure next to rows
  // with one reads as zero.
  const inbox = $derived(inboxCounts());
  $effect(() => {
    // Admin only: the name-merge endpoints are admin-gated.
    if (sidebar.isMobile || isFriend || collapsed.inbox) return;
    void refreshInboxNameCounts();
  });
  // The grid's own list, so this badge and its "N albums" footer cannot disagree.
  const albumCount = $derived.by(() => (songs.length === 0 ? null : songsStore.albums.length));

  const artistCount = $derived.by(() => {
    if (songs.length === 0) return null;
    const set = new Set<string>();
    for (const s of builtSongs) {
      const a = (s.albumArtist ?? s.artist ?? '').trim();
      if (a) set.add(a.toLowerCase());
    }
    return set.size;
  });

  // Counts stay here rather than in $lib/nav: that module is pure data with no store access,
  // which is what lets the tests import it. Keyed by item id; a count shows once it is known (the
  // name-merge queues' only after the Inbox hub has fetched them).
  const COUNTS: Record<string, () => number | string | null> = {
    albums: () => albumCount,
    artists: () => artistCount,
    tracks: () => trackCount,
    review: () => inbox.review,
    dupes: () => inbox.dupes,
    aiflag: () => inbox.ai,
    'dupe-artists': () => inbox.artists,
    'dupe-albums': () => inbox.albums,
    // Unread messages; nothing at all when everything is read, like the tab bar's badge.
    messages: () => (chatStore.unreadTotal > 0 ? chatStore.unreadTotal : null)
  };
  // The one group that carries a total on its header: the tab bar's badge figure.
  const TOTALS: Partial<Record<NavGroupId, () => number | null>> = {
    inbox: () => inbox.total,
    chats: () => (chatStore.unreadTotal > 0 ? chatStore.unreadTotal : null)
  };

  // Single matcher, shared with the tab bar and the browser-tab title.
  const match = $derived(resolveNav(page.url));

  function itemActive(item: NavItem): boolean {
    return match?.item?.id === item.id;
  }

  function fmtCount(n: number | string): string {
    return typeof n === 'number' ? n.toLocaleString() : n;
  }

  // ── collapsible groups ────────────────────────────────────────────────────
  // Four groups of items outgrow a laptop-height sidebar (825px of items in a 729px column at
  // 1440×900), so each group folds away behind a disclosure, the macOS sidebar way. Remembered per
  // viewer in this browser — a convenience, so storage failures (private mode) just start expanded.
  const COLLAPSE_KEY = 'mh:sidebar-collapsed';
  function readCollapsed(): Partial<Record<NavGroupId, boolean>> {
    try {
      const raw = typeof localStorage === 'undefined' ? null : localStorage.getItem(COLLAPSE_KEY);
      const parsed: unknown = raw ? JSON.parse(raw) : null;
      return parsed && typeof parsed === 'object' ? (parsed as Record<NavGroupId, boolean>) : {};
    } catch {
      return {};
    }
  }
  let collapsed = $state<Partial<Record<NavGroupId, boolean>>>(readCollapsed());
  function toggleGroup(id: NavGroupId) {
    collapsed = { ...collapsed, [id]: !collapsed[id] };
    try {
      localStorage.setItem(COLLAPSE_KEY, JSON.stringify(collapsed));
    } catch {
      /* not remembered — still toggles for this page load */
    }
  }

  // ── scroll edge ───────────────────────────────────────────────────────────
  // Nothing else says the list scrolls (the scrollbar is hidden), so while items continue below
  // the fold the bottom edge fades out.
  let contentEl = $state<HTMLElement | null>(null);
  let moreBelow = $state(false);
  function measureEdge() {
    const el = contentEl;
    moreBelow = el ? el.scrollTop + el.clientHeight < el.scrollHeight - 1 : false;
  }
  $effect(() => {
    const el = contentEl;
    if (!el) return;
    measureEdge();
    const ro = new ResizeObserver(measureEdge);
    ro.observe(el);
    for (const child of el.children) ro.observe(child);
    el.addEventListener('scroll', measureEdge, { passive: true });
    return () => {
      ro.disconnect();
      el.removeEventListener('scroll', measureEdge);
    };
  });
  // Folding a group changes the content height without resizing the scroller itself.
  $effect(() => {
    void collapsed;
    requestAnimationFrame(measureEdge);
  });
</script>

{#if !sidebar.isMobile}
  <Sidebar.Root collapsible="offcanvas" variant="floating">
    <Sidebar.Header class="gap-0 px-2 pt-3 pb-2">
      <Sidebar.Menu>
        <Sidebar.MenuItem>
          <Sidebar.MenuButton size="lg" tooltipContent="MusicHoarder">
            {#snippet child({ props })}
              <a {...props} href={isFriend ? APP_HOME : '/pipeline'}>
                <!-- The one app mark: the same disc as the favicon, the Home Screen icon and the
                     sign-in, invite and error screens. -->
                <BrandMark class="size-[30px]" />
                <div class="grid min-w-0 flex-1 text-left leading-tight">
                  <span class="truncate text-sm font-semibold">MusicHoarder</span>
                  <span class="text-muted-foreground truncate text-[11px]">
                    {version ? `v${version} · ` : ''}self-hosted
                  </span>
                </div>
              </a>
            {/snippet}
          </Sidebar.MenuButton>
        </Sidebar.MenuItem>
      </Sidebar.Menu>
    </Sidebar.Header>

    <Sidebar.Content
      bind:ref={contentEl}
      data-more-below={moreBelow || undefined}
      class="gap-4 px-2 py-1 data-[more-below]:[mask-image:linear-gradient(to_bottom,#000_calc(100%-32px),transparent)]"
    >
      {#each navGroups as group (group.id)}
        {@const groupActive = match?.group.id === group.id}
        <!-- Only one nav level carries emphasis at a time: when an item is active it alone is
             highlighted and the group header stays neutral (it is already "expanded" by being
             on that route). The header takes the emphasis only where the group matched but no
             item did — a track page, or the library's source view. -->
        {@const headerActive = groupActive && match?.item == null}
        {@const isCollapsed = Boolean(collapsed[group.id])}
        <!-- Expanded, the rows beneath carry every figure — and the Inbox total adds up only three
             of its five rows — so a header shows its total only while folded, where it stands in
             for the hidden rows (macOS Mail's collapsed-mailbox count). -->
        {@const total = isCollapsed ? TOTALS[group.id]?.() : null}
        {@const pulse = Boolean(group.live && indexing)}
        {@const accessory = pulse || (total != null && total > 0)}
        {@const itemsId = `sidebar-group-${group.id}`}
        <!-- A folded group still shows the page you are on, so the sidebar never loses "you are
             here". -->
        {@const shownItems = isCollapsed ? group.items.filter(itemActive) : group.items}
        <Sidebar.Group class="p-0">
          <div
            data-active={headerActive || undefined}
            class={cn(
              'group/header relative flex w-full items-center rounded-md transition-colors',
              'hover:bg-sidebar-accent data-[active=true]:bg-sidebar-accent'
            )}
          >
            <a
              href={group.href}
              aria-current={headerActive ? 'page' : undefined}
              class="focus-visible:ring-sidebar-ring flex min-h-6 min-w-0 flex-1 items-center gap-2 rounded-md px-2 py-1 text-left outline-none focus-visible:ring-2"
            >
              <!-- A section label in the macOS sidebar style (small, secondary), still a link to
                   the group's landing page. -->
              <span
                class="text-nav-xs text-muted-foreground group-data-[active=true]/header:text-primary flex-1 font-semibold"
              >
                {group.label}
              </span>
              {#if pulse}
                <span class="sr-only">pipeline running</span>
              {/if}
              {#if accessory}
                <!-- Ends on the rows' px-2 edge, so the total sits in the row counts' column, and
                     is styled like them: the red capsule belongs to the tab bar, which has no rows
                     beneath it to carry the figures. With a mouse the disclosure takes this slot
                     over on hover or focus; on touch both stay, side by side. -->
                <span
                  class="flex shrink-0 items-center gap-2 pointer-fine:group-hover/header:opacity-0 pointer-fine:group-has-[button:focus-visible]/header:opacity-0"
                >
                  {#if pulse}
                    <span
                      class="bg-primary mh-v2-pulse size-[7px] shrink-0 rounded-full"
                      aria-hidden="true"
                    ></span>
                  {/if}
                  {#if total != null && total > 0}
                    <span class="text-nav-count text-muted-foreground tabular-nums"
                      >{fmtCount(total)}</span
                    >
                  {/if}
                </span>
              {/if}
            </a>
            <!-- The disclosure: quiet at rest, like macOS's "Hide"/"Show" on a section header, and
                 always shown while the group is folded so it can be found again — unless the
                 header has an accessory to show at rest instead. With a mouse it overlays the
                 header's trailing slot rather than taking a column of its own, which would push
                 the header's figures out of line with the rows'. -->
            <button
              type="button"
              aria-expanded={!isCollapsed}
              aria-controls={itemsId}
              aria-label={`${isCollapsed ? 'Show' : 'Hide'} ${group.label}`}
              title={isCollapsed ? 'Show' : 'Hide'}
              class={cn(
                'text-muted-foreground hover:text-foreground focus-visible:ring-sidebar-ring grid size-6 shrink-0 place-items-center rounded-md outline-none focus-visible:opacity-100 focus-visible:ring-2',
                'absolute inset-y-0 right-0 my-auto pointer-coarse:static',
                (!isCollapsed || accessory) &&
                  'opacity-0 group-hover/header:opacity-100 pointer-coarse:opacity-100'
              )}
              onclick={() => toggleGroup(group.id)}
            >
              <ChevronDown
                class={cn(
                  'size-3.5 transition-transform duration-150 motion-reduce:transition-none',
                  isCollapsed && '-rotate-90'
                )}
                aria-hidden="true"
              />
            </button>
          </div>
          <Sidebar.GroupContent id={itemsId} class="mt-0.5 flex flex-col gap-px">
            {#each shownItems as item (item.id)}
              {@const active = itemActive(item)}
              {@const count = COUNTS[item.id]?.()}
              {@const live = Boolean(item.live && indexing)}
              <a
                href={item.href}
                data-active={active || undefined}
                aria-current={active ? 'page' : undefined}
                class={cn(
                  // 28px rows, the macOS sidebar's medium size: all four groups fit a 900px-tall
                  // laptop window without folding one away.
                  'flex min-h-7 w-full items-center gap-2 rounded-md px-2 py-1 transition-colors',
                  'text-sidebar-foreground hover:bg-sidebar-accent',
                  'data-[active=true]:bg-sidebar-accent data-[active=true]:text-primary data-[active=true]:font-medium',
                  'focus-visible:ring-sidebar-ring outline-none focus-visible:ring-2'
                )}
              >
                <item.icon
                  class={cn(
                    'size-4 shrink-0',
                    active || live ? 'text-primary' : 'text-muted-foreground'
                  )}
                />
                <span class="text-nav flex-1 truncate">{item.label}</span>
                {#if live}
                  <span
                    class="bg-primary mh-v2-pulse size-1.5 shrink-0 rounded-full"
                    aria-hidden="true"
                  ></span>
                  <span class="sr-only">running</span>
                {/if}
                {#if count != null}
                  <span class="text-nav-count text-muted-foreground tabular-nums"
                    >{fmtCount(count)}</span
                  >
                {/if}
              </a>
            {/each}
          </Sidebar.GroupContent>
        </Sidebar.Group>
      {/each}
    </Sidebar.Content>

    <!-- One line of footer (plus "Indexing" while a job runs): the storage bar and its figure.
         Everything else it used to carry is one click away — "Watching N folders" and their paths
         in the account menu, the version in the header above. A tall footer is what pushed the
         last Manage items under the fold. -->
    {#if !isFriend && (queueRemaining || storage)}
      <Sidebar.Footer class="border-sidebar-border gap-1.5 border-t px-3.5 pt-2.5 pb-3">
        {#if queueRemaining}
          <div class="text-nav-xs flex items-center gap-2">
            <span class="bg-primary mh-v2-pulse size-[7px] shrink-0 rounded-full" aria-hidden="true"
            ></span>
            <span class="text-muted-foreground flex-1 whitespace-nowrap">Indexing</span>
            <span class="text-foreground text-nav-count whitespace-nowrap tabular-nums">
              {queueRemaining.toLocaleString()} active
            </span>
          </div>
        {/if}
        {#if storage}
          <button
            type="button"
            class="hover:bg-sidebar-accent focus-visible:ring-sidebar-ring -mx-1.5 flex items-center gap-2.5 rounded-md px-1.5 py-1 text-left transition-colors outline-none focus-visible:ring-2"
            aria-label="Storage, {storage.label}. Show breakdown"
            title="Storage breakdown"
            onclick={() => (storageUsage.dialogOpen = true)}
          >
            <span
              class="bg-muted flex h-[3px] min-w-0 flex-1 overflow-hidden rounded-full"
              aria-hidden="true"
            >
              {#each storage.segments as segment (segment.key)}
                <span
                  class="{segment.color} h-full transition-[width] duration-300"
                  style="width: {segment.pct}%;"
                ></span>
              {/each}
            </span>
            <span class="text-foreground text-nav-count shrink-0 whitespace-nowrap tabular-nums"
              >{storage.label}</span
            >
          </button>
        {/if}
      </Sidebar.Footer>
    {/if}
  </Sidebar.Root>
{/if}
