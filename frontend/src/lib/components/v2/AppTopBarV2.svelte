<script lang="ts">
  import * as Sidebar from '$lib/components/ui/sidebar';
  import ThemeToggle from '$lib/components/ThemeToggle.svelte';
</script>

<!--
  Apple-Music-style: no breadcrumb. Section identity comes from the sidebar
  active state + the in-content tab row. The bar keeps only the sidebar trigger
  (collapse on desktop / off-canvas on mobile) and the theme toggle; banners
  render beneath it from AppShellV2.
-->
<header
  class="border-border bg-background flex h-12 shrink-0 items-center gap-2.5 border-b px-3.5"
>
  <Sidebar.Trigger class="-ml-1 size-9 md:size-7" />
  <div class="ml-auto flex items-center gap-1.5">
    <ThemeToggle />
  </div>
</header>
