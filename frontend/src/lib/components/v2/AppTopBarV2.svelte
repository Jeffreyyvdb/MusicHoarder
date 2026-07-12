<script lang="ts">
  import { Plus } from '@lucide/svelte';
  import { page } from '$app/state';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import { Button } from '$lib/components/ui/button';
  import ThemeToggle from '$lib/components/ThemeToggle.svelte';
  import AddFromUrlDialog from '$lib/components/v2/AddFromUrlDialog.svelte';

  let addOpen = $state(false);

  // macOS-Music-style window title: the current section name rendered in the
  // bar itself, so wayfinding survives a collapsed sidebar and the mobile
  // off-canvas drawer. Section grouping mirrors AppSidebarV2's NAV.
  function sectionTitle(pathname: string): string | null {
    const path = pathname.length > 1 && pathname.endsWith('/') ? pathname.slice(0, -1) : pathname;
    if (
      path === '/pipeline' ||
      path.startsWith('/directories') ||
      path.startsWith('/quality') ||
      path.startsWith('/album-quality') ||
      path.startsWith('/performance')
    )
      return 'Pipeline';
    if (path.startsWith('/inbox')) return 'Inbox';
    if (
      path === '/library' ||
      path.startsWith('/library/') ||
      path.startsWith('/artists') ||
      path.startsWith('/tracks') ||
      path.startsWith('/spotify') ||
      path.startsWith('/wishlist') ||
      path.startsWith('/playlists')
    )
      return 'Library';
    if (path.startsWith('/stats')) return 'Stats';
    if (path.startsWith('/history')) return 'History';
    if (path.startsWith('/settings')) return 'Settings';
    return null;
  }

  const title = $derived(sectionTitle(page.url.pathname));
</script>

<!--
  Apple-Music-style: no breadcrumb — just the section title as wayfinding, the
  sidebar trigger (collapse on desktop / off-canvas on mobile), and the theme
  toggle. Banners render beneath it from AppShellV2.
-->
<header
  class="border-border bg-background flex h-12 shrink-0 items-center gap-2.5 border-b px-3.5"
>
  <Sidebar.Trigger class="-ml-1 size-9 md:size-7" />
  {#if title}
    <span class="text-foreground min-w-0 truncate text-[13px] font-semibold tracking-[-0.01em]">
      {title}
    </span>
  {/if}
  <div class="ml-auto flex items-center gap-1.5">
    <Button
      variant="outline"
      size="sm"
      class="h-8 gap-1.5 px-2.5"
      onclick={() => (addOpen = true)}
      title="Add a track from a Spotify or YouTube URL"
    >
      <Plus class="size-4" />
      <span class="hidden text-[12.5px] sm:inline">Add</span>
    </Button>
    <ThemeToggle />
  </div>
</header>

<AddFromUrlDialog bind:open={addOpen} />
