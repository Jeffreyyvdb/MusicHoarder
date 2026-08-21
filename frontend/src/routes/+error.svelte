<script lang="ts">
  import { page } from '$app/state';
  import { Button } from '$lib/components/ui/button';
  import { Compass, RefreshCw, ServerCrash } from '@lucide/svelte';

  // 503 is specifically "the auth gate could not reach the API" — the session is intact, so the
  // fix is to retry, not to sign in again.
  const isUnreachable = $derived(page.status === 503);
  // A 404 is not an outage: retrying the same dead URL cannot help, and "sign in" is a red herring.
  // Point people at the pages that do exist instead — the same recovery routes the Markdown 404
  // hands to agents (see src/hooks.server.ts).
  const isNotFound = $derived(page.status === 404);
  const message = $derived(page.error?.message ?? 'Something went wrong.');
</script>

<svelte:head>
  <title
    >{isUnreachable ? 'Reconnecting' : isNotFound ? 'Page not found' : 'Error'} · MusicHoarder</title
  >
</svelte:head>

<div class="bg-background flex min-h-screen items-center justify-center p-6">
  <div class="border-border bg-card w-full max-w-md rounded-2xl border p-8 shadow-sm">
    <div class="mb-5 flex items-center gap-3">
      <div class="bg-secondary flex size-10 items-center justify-center rounded-lg">
        {#if isNotFound}
          <Compass class="text-foreground size-5" />
        {:else}
          <ServerCrash class="text-foreground size-5" />
        {/if}
      </div>
      <div>
        <h1 class="text-xl font-semibold tracking-tight">
          {isUnreachable
            ? 'Server unreachable'
            : isNotFound
              ? 'Page not found'
              : `Error ${page.status}`}
        </h1>
        <p class="text-muted-foreground text-sm">
          {isNotFound ? `There is nothing at ${page.url.pathname}.` : message}
        </p>
      </div>
    </div>

    {#if isUnreachable}
      <p class="text-muted-foreground mb-5 text-[13px] leading-[1.6]">
        This usually means the API is restarting (a deploy, for instance). Give it a few seconds and
        try again — you do not need to sign in again.
      </p>
    {/if}

    {#if isNotFound}
      <p class="text-muted-foreground mb-5 text-[13px] leading-[1.6]">
        The link may be out of date. The home page explains what MusicHoarder does, and everything
        else is one hop from there.
      </p>

      <div class="flex flex-wrap gap-3">
        <Button href="/">Go to the home page</Button>
        <Button variant="outline" href="/about">About</Button>
        <Button variant="outline" href="/contact">Contact</Button>
      </div>
    {:else}
      <div class="flex flex-wrap gap-3">
        <Button onclick={() => location.reload()}>
          <RefreshCw class="size-4" />
          Retry
        </Button>
        <Button variant="outline" href="/login">Sign in instead</Button>
      </div>
    {/if}
  </div>
</div>
