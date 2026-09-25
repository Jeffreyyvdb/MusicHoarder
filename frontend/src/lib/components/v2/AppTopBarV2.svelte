<script lang="ts">
  import { Link, Search } from '@lucide/svelte';
  import { page } from '$app/state';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import { Button } from '$lib/components/ui/button';
  import AccountButton from '$lib/components/v2/AccountButton.svelte';
  import AddFromUrlDialog from '$lib/components/v2/AddFromUrlDialog.svelte';
  import SectionTabsV2 from '$lib/components/v2/SectionTabsV2.svelte';
  import { commandPalette } from '$lib/stores/command-palette.svelte';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { navGroupsFor, resolveNav } from '$lib/nav';
  import { isAdmin } from '$lib/auth/capabilities';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';

  let addOpen = $state(false);
  const isMobile = new IsMobile();

  // Cmd/Ctrl+K is the primary way into the palette; the button is the pointer's way in. The hint
  // names the modifier the keyboard actually has.
  const shortcutHint = $derived(
    typeof navigator !== 'undefined' && /mac|iphone|ipad|ipod/i.test(navigator.platform)
      ? '⌘K'
      : 'Ctrl K'
  );

  // The section tab strip lives here rather than in a row of its own, and only when the sidebar
  // isn't showing the same links (it is collapsed: `offcanvas` means gone, not an icon rail).
  //
  // Tabs are the matched group's items, so the strip is always a complete map of its group and
  // can't drift from the sidebar. The group must be one this account can see: a member may open
  // /settings, which resolves to Manage, and must not be shown seven Manage pages the route guard
  // would bounce them from. /track/[id] matches Listen with no item, so the pills show with none
  // active — "you're in Listen, off-tab", one tap back.
  const sidebar = Sidebar.useSidebar();
  const nav = $derived.by(() => {
    if (sidebar.state === 'expanded') return null;
    const match = resolveNav(page.url);
    if (!match) return null;
    return navGroupsFor(page.data.user).some((g) => g.id === match.group.id) ? match : null;
  });

  // Adding music is admin work: Demo and members keep search and their account only.
  const isFriend = $derived(!isAdmin(page.data.user));
</script>

<!--
  The desktop toolbar (md and up — a phone has the tab bar and each page's own nav bar instead):
  the sidebar toggle, the section tabs when the sidebar isn't showing them, then Search, Add from
  link and the account. Banners render beneath it from AppShellV2. The Light/Dark/System picker
  lives in the account menu now.

  The strip takes the slack (min-w-0 flex-1, it scrolls internally) while the action cluster is
  shrink-0, so eight Manage tabs can never push Search off a narrow window.

  The top safe-area inset is 0 in a browser tab and in the installed app (status-bar style
  `default`); it stays for fullscreen and landscape notches.
-->
<header
  class="border-separator bg-background hidden h-[calc(3rem_+_env(safe-area-inset-top))] shrink-0 items-center gap-2 border-b px-3.5 pt-[env(safe-area-inset-top)] md:flex"
>
  <Sidebar.Trigger class="-ml-1 size-7 shrink-0" />
  {#if nav}
    <SectionTabsV2
      class="min-w-0 flex-1"
      tabs={nav.group.items}
      active={nav.item?.id ?? ''}
      label="{nav.group.label} views"
      running={pipelineOverlay.isAnyRunning}
    />
  {/if}
  <div class="ml-auto flex shrink-0 items-center gap-1.5">
    <Button
      variant="gray"
      size="sm"
      class="h-8 gap-1.5 rounded-full px-3"
      onclick={() => commandPalette.setOpen(true)}
      onpointerenter={() => songsStore.ensureLoaded()}
      onfocus={() => songsStore.ensureLoaded()}
      aria-label="Search everywhere"
      title="Search everywhere ({shortcutHint})"
    >
      <Search class="size-4" />
      <span class="text-nav-sm">Search</span>
      <kbd class="text-muted-foreground text-nav-badge font-sans leading-[1.4] font-medium">
        {shortcutHint}
      </kbd>
    </Button>
    {#if !isFriend}
      <Button
        variant="gray"
        size="sm"
        class="h-8 gap-1.5 rounded-full px-3"
        onclick={() => (addOpen = true)}
        title="Add a track from a Spotify or YouTube link"
      >
        <Link class="size-4" />
        <span class="text-nav-sm">Add from link</span>
      </Button>
    {/if}
    <!-- Mounted at md+ only: this bar is merely display:none on a phone, where the nav bar's own
         AccountButton is the one in use — a second copy would mount a second account sheet. -->
    {#if !isMobile.current}
      <AccountButton class="ml-1" />
    {/if}
  </div>
</header>

{#if !isFriend}
  <AddFromUrlDialog bind:open={addOpen} />
{/if}
