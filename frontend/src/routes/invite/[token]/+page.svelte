<script lang="ts">
  import { page } from '$app/state';
  import { UserRoundPlus } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import BrandMark from '$lib/components/BrandMark.svelte';
  import type { PageData } from './$types';

  const { data }: { data: PageData } = $props();

  const invite = $derived(data.invite);
  // The accept route bounces back with ?error=1 when the token died between peek and accept
  // (revoked, expired, or already used in another tab).
  const acceptFailed = $derived(page.url.searchParams.get('error') === '1');

  let submitting = $state(false);
</script>

<svelte:head>
  <title>You're invited · MusicHoarder</title>
  <meta name="robots" content="noindex" />
</svelte:head>

<!-- The sign-in page's frame, exactly: a full-bleed phone screen, top-anchored and leading-aligned,
     a card from md — so moving between invite, sign-in and error pages never moves the brand
     mark or the title. -->
<main
  class="bg-background text-foreground flex min-h-dvh flex-col md:items-center md:justify-center md:p-6"
>
  <div
    class="md:border-border md:bg-card flex w-full flex-1 flex-col px-6 pt-[calc(2.5rem+env(safe-area-inset-top))] pb-[calc(2rem+env(safe-area-inset-bottom))] md:max-w-md md:flex-none md:rounded-2xl md:border md:p-8 md:shadow-sm"
  >
    <BrandMark class="size-12 md:size-10" />

    {#if invite && !acceptFailed}
      <h1 class="text-title-1 mt-6 md:mt-5 md:text-2xl md:font-semibold">You’re invited</h1>
      <p class="text-body text-muted-foreground mt-2 md:text-sm">
        <span class="text-foreground font-semibold">{invite.inviterName}</span> invited you to
        listen to their MusicHoarder library. Your account will use
        <span class="text-foreground font-semibold break-all">{invite.email}</span> to sign in.
      </p>

      <!-- A real form navigation (not fetch): the accept route sets the session cookie and
           303s onward, both of which the browser handles natively on a document request. -->
      <form
        method="POST"
        action={`/invite/${encodeURIComponent(data.token)}/accept`}
        onsubmit={() => (submitting = true)}
        class="mt-8 w-full md:mt-6"
      >
        <!-- A one-shot tap from an emailed link: a full-width 50pt capsule on a phone. -->
        <Button
          type="submit"
          class="text-headline h-[50px] w-full rounded-full md:h-10 md:text-sm md:font-medium"
          disabled={submitting}
        >
          <UserRoundPlus class="size-5 md:size-4" />
          {submitting ? 'Setting up your account…' : 'Accept invite'}
        </Button>
      </form>

      <p class="text-footnote text-muted-foreground mt-4 md:text-xs">
        This link can be used once. Next time, sign in at
        <!-- The footnote stays 13pt; a pseudo-element makes the link a 44pt target. -->
        <a
          href="/login"
          class="text-primary relative underline-offset-2 after:absolute after:-inset-x-1 after:-inset-y-3.5 hover:underline"
          >the sign-in page</a
        >
        with your email.
      </p>
    {:else}
      <h1 class="text-title-1 mt-6 md:mt-5 md:text-2xl md:font-semibold">
        This invite isn’t valid anymore
      </h1>
      <p class="text-body text-muted-foreground mt-2 md:text-sm">
        The link may have expired, been replaced by a newer one, or already been used. Ask the
        person who invited you for a fresh link — or if you already have an account, sign in with
        your email.
      </p>
      <Button
        href="/login"
        variant="gray"
        class="text-headline mt-8 h-[50px] w-full rounded-full md:mt-6 md:h-10 md:text-sm md:font-medium"
      >
        Go to sign in
      </Button>
    {/if}
  </div>
</main>
