<script lang="ts">
  import { page } from '$app/state';
  import { Music, UserRoundPlus } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
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

<div class="bg-background text-foreground flex min-h-screen items-center justify-center px-4">
  <div class="border-border bg-card w-full max-w-md rounded-xl border p-8 text-center shadow-sm">
    <div class="bg-primary/10 text-primary mx-auto flex size-12 items-center justify-center rounded-xl">
      <Music class="size-6" />
    </div>

    {#if invite && !acceptFailed}
      <h1 class="mt-5 text-xl font-semibold">You're invited</h1>
      <p class="text-muted-foreground mt-2 text-sm">
        <span class="text-foreground font-medium">{invite.inviterName}</span> invited you to listen
        to their MusicHoarder library. Your account will use
        <span class="text-foreground font-medium">{invite.email}</span> to sign in.
      </p>

      <!-- A real form navigation (not fetch): the accept route sets the session cookie and
           303s onward, both of which the browser handles natively on a document request. -->
      <form
        method="POST"
        action={`/invite/${encodeURIComponent(data.token)}/accept`}
        onsubmit={() => (submitting = true)}
        class="mt-6"
      >
        <Button type="submit" class="w-full" disabled={submitting}>
          <UserRoundPlus class="size-4" />
          {submitting ? 'Setting up your account…' : 'Accept invite'}
        </Button>
      </form>

      <p class="text-muted-foreground mt-4 text-xs">
        This link can be used once. Next time, sign in at
        <a href="/login" class="underline underline-offset-2">the sign-in page</a> with your email.
      </p>
    {:else}
      <h1 class="mt-5 text-xl font-semibold">This invite isn't valid anymore</h1>
      <p class="text-muted-foreground mt-2 text-sm">
        The link may have expired, been replaced by a newer one, or already been used. Ask the
        person who invited you for a fresh link — or if you already have an account,
        <a href="/login" class="underline underline-offset-2">sign in with your email</a>.
      </p>
    {/if}
  </div>
</div>
