<script lang="ts">
  import type { Snippet } from 'svelte';
  import * as Sidebar from '$lib/components/ui/sidebar';
  import AppSidebar from '$lib/components/AppSidebar.svelte';
  import AppHeader from '$lib/components/AppHeader.svelte';
  import MiniPlayer from '$lib/components/MiniPlayer.svelte';
  import { playerStore } from '$lib/stores/player.svelte';
  import { cn } from '$lib/utils';

  type Props = { children: Snippet };
  const { children }: Props = $props();
</script>

<Sidebar.Provider>
  <AppSidebar />
  <Sidebar.Inset
    class={cn(
      'bg-background h-svh',
      playerStore.currentSong && 'pb-[60px] sm:pb-[68px]'
    )}
  >
    <AppHeader />
    {@render children()}
  </Sidebar.Inset>
</Sidebar.Provider>

<MiniPlayer />
