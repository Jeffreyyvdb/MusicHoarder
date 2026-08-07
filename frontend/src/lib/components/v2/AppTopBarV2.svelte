<script lang="ts">
  import { Plus, Search } from '@lucide/svelte';
  import { page } from '$app/state';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import { Button } from '$lib/components/ui/button';
  import ThemeToggle from '$lib/components/ThemeToggle.svelte';
  import AddFromUrlDialog from '$lib/components/v2/AddFromUrlDialog.svelte';
  import { commandPalette } from '$lib/stores/command-palette.svelte';
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

  // macOS-Music-style window title: the current group name rendered in the bar
  // itself, so wayfinding survives a collapsed sidebar and the mobile off-canvas
  // drawer. Resolved from the shared nav, so it can never name a different group
  // than the sidebar highlights.
  const title = $derived(resolveNav(page.url)?.group.label ?? null);
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
    <span class="text-foreground text-nav min-w-0 truncate font-semibold tracking-[-0.01em]">
      {title}
    </span>
  {/if}
  <div class="ml-auto flex items-center gap-1.5">
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
