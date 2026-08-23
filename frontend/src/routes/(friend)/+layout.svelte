<script lang="ts">
  import type { Snippet } from 'svelte';
  import { page } from '$app/state';
  import { LogOut, Music } from '@lucide/svelte';
  import MiniPlayer from '$lib/components/MiniPlayer.svelte';
  import { Button } from '$lib/components/ui/button';
  import { initPlayer } from '$lib/stores/player.svelte';
  import { signOutAndReset } from '$lib/auth/sign-out';

  type Props = { children: Snippet };
  const { children }: Props = $props();

  // Deliberately minimal chrome: friends get exactly one surface (the shared library), so no
  // sidebar, command palette, or pipeline overlay — those are all wired to owner-only state.
  const user = $derived(page.data.user as { email: string; displayName: string | null } | undefined);

  // The player owns its audio element in JS (not the DOM), same warm-up as the (app) shell.
  $effect(() => initPlayer());

  let signingOut = $state(false);
  async function onSignOut() {
    if (signingOut) return;
    signingOut = true;
    try {
      await signOutAndReset();
    } finally {
      signingOut = false;
    }
  }
</script>

<svelte:head>
  <title>Shared with me · MusicHoarder</title>
</svelte:head>

<div class="bg-background text-foreground flex min-h-screen flex-col">
  <header class="border-border bg-background/95 sticky top-0 z-20 border-b backdrop-blur">
    <div class="mx-auto flex w-full max-w-6xl items-center gap-3 px-4 py-3">
      <div class="bg-primary/10 text-primary flex size-8 items-center justify-center rounded-lg">
        <Music class="size-4" />
      </div>
      <div class="min-w-0 flex-1">
        <p class="truncate text-sm font-semibold">Shared with me</p>
        <p class="text-muted-foreground truncate text-xs">MusicHoarder</p>
      </div>
      {#if user}
        <span class="text-muted-foreground hidden max-w-48 truncate text-xs sm:inline">
          {user.displayName ?? user.email}
        </span>
      {/if}
      <Button variant="ghost" size="sm" onclick={onSignOut} disabled={signingOut}>
        <LogOut class="size-4" />
        <span class="hidden sm:inline">Sign out</span>
      </Button>
    </div>
  </header>

  <!-- Bottom padding keeps the floating MiniPlayer clear of the last rows. -->
  <main class="mx-auto w-full max-w-6xl flex-1 px-4 pt-6 pb-32">
    {@render children()}
  </main>

  <MiniPlayer />
</div>
