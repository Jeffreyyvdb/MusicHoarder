<script lang="ts">
  import { page } from '$app/state';
  import { Button } from '$lib/components/ui/button';
  import BrandMark from '$lib/components/BrandMark.svelte';
  import { APP_HOME } from '$lib/app-home';
  import { isInstalledApp } from '$lib/hooks/viewport-insets.svelte';
  import { RefreshCw } from '@lucide/svelte';

  // 503 is specifically "the auth gate could not reach the API" — the session is intact, so the
  // fix is to retry, not to sign in again.
  const isUnreachable = $derived(page.status === 503);
  // A 404 is not an outage: retrying the same dead URL cannot help, and "sign in" is a red herring.
  // Point people at the pages that do exist instead — the same recovery routes the Markdown 404
  // hands to agents (see src/hooks.server.ts).
  const isNotFound = $derived(page.status === 404);
  const message = $derived(page.error?.message ?? 'Something went wrong.');

  // Inside the installed app (its scope is the whole origin) or with a session, "home" is the
  // library, not the marketing page — which has no way back from a standalone window. Decided after
  // mount: the server render cannot know the display mode, and must not differ from hydration.
  let inApp = $state(false);
  $effect(() => {
    inApp = isInstalledApp() || Boolean(page.data.user);
  });
</script>

<svelte:head>
  <title
    >{isUnreachable ? 'Reconnecting' : isNotFound ? 'Page not found' : 'Error'} · MusicHoarder</title
  >
</svelte:head>

<div class="bg-background flex min-h-dvh items-center justify-center p-6">
  <div class="border-border bg-card w-full max-w-md rounded-2xl border p-8 shadow-sm">
    <div class="mb-5 flex items-center gap-3">
      <BrandMark class="size-10" />
      <div class="min-w-0">
        <h1 class="text-xl font-semibold tracking-tight">
          {isUnreachable
            ? 'Server unreachable'
            : isNotFound
              ? 'Page not found'
              : `Error ${page.status}`}
        </h1>
        <p class="text-muted-foreground text-sm break-words">
          {isNotFound ? `There is nothing at ${page.url.pathname}.` : message}
        </p>
      </div>
    </div>

    {#if isUnreachable}
      <p class="text-muted-foreground mb-5 text-[13px] leading-[1.6]">
        This usually means the server is restarting (after an update, for instance). Give it a few
        seconds and try again — you do not need to sign in again.
      </p>
    {/if}

    <!-- One-shot recovery taps: stacked and full width at 44px on a phone, a row from sm up. -->
    {#if isNotFound}
      <p class="text-muted-foreground mb-5 text-[13px] leading-[1.6]">
        {#if inApp}
          The link may be out of date. Your library is one tap away.
        {:else}
          The link may be out of date. The home page explains what MusicHoarder does, and everything
          else is one hop from there.
        {/if}
      </p>

      <div class="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
        {#if inApp}
          <Button size="lg" class="h-11 sm:h-9" href={APP_HOME}>Go to your library</Button>
        {:else}
          <Button size="lg" class="h-11 sm:h-9" href="/">Go to the home page</Button>
        {/if}
        <div class="flex gap-3">
          <Button size="lg" variant="outline" class="h-11 flex-1 sm:h-9 sm:flex-none" href="/about">
            About
          </Button>
          <Button
            size="lg"
            variant="outline"
            class="h-11 flex-1 sm:h-9 sm:flex-none"
            href="/contact"
          >
            Contact
          </Button>
        </div>
      </div>
    {:else}
      <div class="flex flex-col gap-3 sm:flex-row sm:flex-wrap">
        <Button size="lg" class="h-11 sm:h-9" onclick={() => location.reload()}>
          <RefreshCw class="size-4" />
          Retry
        </Button>
        <Button size="lg" variant="outline" class="h-11 sm:h-9" href="/login"
          >Sign in instead</Button
        >
      </div>
    {/if}
  </div>
</div>
