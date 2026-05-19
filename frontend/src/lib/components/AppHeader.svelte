<script lang="ts">
  import { Search } from '@lucide/svelte';
  import ThemeToggle from '$lib/components/ThemeToggle.svelte';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import { Separator } from '$lib/components/ui/separator';
  import { Input } from '$lib/components/ui/input';
</script>

<header
  class="border-border bg-background/80 sticky top-0 z-30 flex h-14 shrink-0 items-center gap-2 border-b px-3 backdrop-blur md:px-4"
>
  <Sidebar.Trigger class="-ml-1" />
  <Separator orientation="vertical" class="mx-1 h-5" />

  <div class="relative flex min-w-0 flex-1 items-center">
    <Search
      class="text-muted-foreground pointer-events-none absolute left-3 size-4"
      aria-hidden="true"
    />
    <Input
      type="search"
      placeholder="Search your library..."
      class="bg-secondary h-9 w-full max-w-xl border-0 pl-9 shadow-none focus-visible:ring-1"
      aria-label="Search your library"
    />
  </div>

  <ThemeToggle />
</header>
