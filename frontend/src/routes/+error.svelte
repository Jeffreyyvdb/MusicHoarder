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

  // A title that says what happened (HIG alerts: never a bare "Error"); the status code stays
  // on the page as a footnote for whoever reports it.
  const title = $derived(
    isUnreachable
      ? 'Server unreachable'
      : isNotFound
        ? 'Page not found'
        : page.status === 403
          ? 'You don’t have access to this'
          : page.status === 401
            ? 'You’re signed out'
            : 'Something went wrong'
  );

  // Inside the installed app (its scope is the whole origin) or with a session, "home" is the
  // library, not the marketing page — which has no way back from a standalone window. Decided after
  // mount: the server render cannot know the display mode, and must not differ from hydration.
  let inApp = $state(false);
  $effect(() => {
    inApp = isInstalledApp() || Boolean(page.data.user);
  });

  // One-shot recovery taps: 50pt capsules stacked full width on a phone, a row from md.
  const BIG =
    'text-headline h-[50px] w-full rounded-full md:h-9 md:w-auto md:px-4 md:text-sm md:font-medium';
</script>

<!-- The tab title is what VoiceOver reads first: it says what happened, as the heading does. -->
<svelte:head>
  <title>{isUnreachable ? 'Reconnecting' : title} · MusicHoarder</title>
</svelte:head>

<!-- The sign-in page's frame: full-bleed, top-anchored and leading-aligned on a phone (as login
     and invite are, so the brand mark and title never jump between them), a card from md. -->
<main
  class="bg-background text-foreground flex min-h-dvh flex-col md:items-center md:justify-center md:p-6"
>
  <div
    class="md:border-border md:bg-card flex w-full flex-1 flex-col px-6 pt-[calc(2.5rem+env(safe-area-inset-top))] pb-[calc(2rem+env(safe-area-inset-bottom))] md:max-w-md md:flex-none md:rounded-2xl md:border md:p-8 md:shadow-sm"
  >
    <BrandMark class="size-12 md:size-10" />
    <h1 class="text-title-1 mt-6 md:mt-5 md:text-2xl md:font-semibold">{title}</h1>
    <p class="text-body text-muted-foreground mt-2 break-words md:text-sm">
      {isNotFound ? `There is nothing at ${page.url.pathname}.` : message}
    </p>

    {#if isUnreachable}
      <p class="text-callout text-muted-foreground mt-3 md:text-[13px] md:leading-[1.6]">
        This usually means the server is restarting (after an update, for instance). Give it a few
        seconds and try again — you do not need to sign in again.
      </p>
    {:else if isNotFound}
      <p class="text-callout text-muted-foreground mt-3 md:text-[13px] md:leading-[1.6]">
        {#if inApp}
          The link may be out of date. Your library is one tap away.
        {:else}
          The link may be out of date. The home page explains what MusicHoarder does, and everything
          else is one hop from there.
        {/if}
      </p>
    {/if}

    <div class="mt-8 flex flex-col gap-3 md:mt-6 md:flex-row md:flex-wrap">
      {#if isNotFound}
        {#if inApp}
          <Button class={BIG} href={APP_HOME}>Go to your library</Button>
        {:else}
          <Button class={BIG} href="/">Go to the home page</Button>
        {/if}
        <div class="flex gap-3">
          <Button variant="gray" class="{BIG} flex-1 md:flex-none" href="/about">About</Button>
          <Button variant="gray" class="{BIG} flex-1 md:flex-none" href="/contact">Contact</Button>
        </div>
      {:else}
        <Button class={BIG} onclick={() => location.reload()}>
          <RefreshCw class="size-5 md:size-4" />
          Retry
        </Button>
        <Button variant="gray" class={BIG} href="/login">Sign in instead</Button>
      {/if}
    </div>

    {#if !isUnreachable && !isNotFound}
      <p class="text-footnote text-muted-foreground mt-6 tabular-nums md:text-xs">
        Error {page.status}
      </p>
    {/if}
  </div>
</main>
