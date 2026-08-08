<script lang="ts">
  import { Plus, Search } from '@lucide/svelte';
  import { page } from '$app/state';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import { Button } from '$lib/components/ui/button';
  import ThemeToggle from '$lib/components/ThemeToggle.svelte';
  import AddFromUrlDialog from '$lib/components/v2/AddFromUrlDialog.svelte';
  import SectionTabsV2 from '$lib/components/v2/SectionTabsV2.svelte';
  import { commandPalette } from '$lib/stores/command-palette.svelte';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { songsStore } from '$lib/stores/songs.svelte';
  import { resolveNav } from '$lib/nav';

  let addOpen = $state(false);

  // Cmd/Ctrl+K is the primary way into the palette, but there's no keyboard on
  // mobile — so the top bar carries a tap target on every page. The shortcut
  // hint only renders where a modifier key actually exists (md and up).
  const shortcutHint = $derived(
    typeof navigator !== 'undefined' && /mac|iphone|ipad|ipod/i.test(navigator.platform)
      ? '⌘K'
      : 'Ctrl K'
  );

  // The section tab strip lives here rather than in a row of its own, and only
  // when the sidebar isn't showing the same links.
  //
  // Tabs are the matched group's items, so the strip is always a complete map of
  // its group and can't drift from the sidebar — which is exactly why rendering
  // both at once is pure duplication. The sidebar is `collapsible="offcanvas"`,
  // so "collapsed" means gone rather than an icon rail, and the mobile bottom
  // bar carries only the four group headers; in both of those cases the strip is
  // the only one-tap route to an item, so it comes back.
  //
  // Routes outside the shell render no strip; /track/[id] matches Listen with no
  // item, so the pills show with none active — "you're in Listen, off-tab", one
  // tap back.
  const sidebar = Sidebar.useSidebar();
  const sidebarShowsItems = $derived(!sidebar.isMobile && sidebar.state === 'expanded');
  const nav = $derived(sidebarShowsItems ? null : resolveNav(page.url));
</script>

<!--
  Apple-Music-style: no breadcrumb — the sidebar trigger (collapse on desktop /
  off-canvas on mobile), the section tabs as wayfinding when the sidebar isn't
  already showing them, then search / add / theme. Banners render beneath it
  from AppShellV2.

  The strip takes the slack (min-w-0 flex-1 and it scrolls internally) while the
  action cluster is shrink-0 — the only arrangement where the eight Manage tabs
  can't push the Search button off a phone screen.
-->
<header class="border-border bg-background flex h-12 shrink-0 items-center gap-2 border-b px-3.5">
  <Sidebar.Trigger class="-ml-1 size-9 shrink-0 md:size-7" />
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
      variant="outline"
      size="sm"
      class="h-8 gap-1.5 px-2.5"
      onclick={() => commandPalette.setOpen(true)}
      onpointerenter={() => songsStore.ensureLoaded()}
      onfocus={() => songsStore.ensureLoaded()}
      aria-label="Search everywhere"
      title="Search everywhere ({shortcutHint})"
    >
      <Search class="size-4" />
      <span class="text-nav-sm hidden sm:inline">Search</span>
      <kbd
        class="border-border bg-muted text-muted-foreground text-nav-badge hidden rounded border px-1 font-sans leading-[1.4] font-medium md:inline"
      >
        {shortcutHint}
      </kbd>
    </Button>
    <Button
      variant="outline"
      size="sm"
      class="h-8 gap-1.5 px-2.5"
      onclick={() => (addOpen = true)}
      title="Add a track from a Spotify or YouTube URL"
    >
      <Plus class="size-4" />
      <span class="text-nav-sm hidden sm:inline">Add</span>
    </Button>
    <ThemeToggle />
  </div>
</header>

<AddFromUrlDialog bind:open={addOpen} />
