<script lang="ts">
  import { page } from '$app/state';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import BrandMark from '$lib/components/BrandMark.svelte';
  import { requestMagicLink, signInAsDemo, loginWithPasskey } from '$lib/api-client';
  import { isPasskeySupported } from '$lib/webauthn-client';
  import { isInstalledApp } from '$lib/hooks/viewport-insets.svelte';
  import { isIosDevice } from '$lib/ios-safari';
  import { APP_HOME } from '$lib/app-home';
  import {
    CircleAlert,
    CircleCheck,
    KeyRound,
    Loader2,
    Mail,
    Sparkles,
    UserPlus
  } from '@lucide/svelte';
  import type { PageData } from './$types';

  let { data }: { data: PageData } = $props();

  // After sign-in, land on the app home — shared with the magic-link callback and the
  // landing-page CTAs so every door opens onto the same screen.
  const landingRoute = () => APP_HOME;

  // Hard navigation, not `goto`: the (app) group's stores are module singletons, and in
  // ?switch mode a soft nav would carry the previous account's data into the new session.
  const enterApp = () => location.assign(landingRoute());

  // The magic-link callback bounces a dead link back here with a code. Codes, not free text: the
  // page must not render whatever a crafted ?error= says.
  const CALLBACK_ERRORS: Record<string, string> = {
    link: 'That sign-in link has expired or was already used. Enter your email to get a new one.',
    signin: "Sign-in didn't go through. Try again, or ask for a new link."
  };

  const callbackError = page.url.searchParams.get('error');

  let email = $state('');
  let emailError = $state<string | null>(null);
  let isSending = $state(false);
  let result = $state<
    | null
    | { ok: true; sent: true; magicLinkUrl?: string | null; magicLinkInLogs?: boolean }
    | { ok: false; message: string }
  >(
    callbackError
      ? { ok: false, message: CALLBACK_ERRORS[callbackError] ?? CALLBACK_ERRORS.signin }
      : null
  );
  let isStartingDemo = $state(false);
  let isPasskeyLogin = $state(false);
  let passkeySupported = $state(false);
  // An iOS Home Screen app has its own cookie jar and never receives links — an emailed sign-in
  // link always opens in Safari — so inside it a passkey is the way in.
  let inIosHomeScreenApp = $state(false);
  // The server render cannot know either of those. Everywhere but the installed iOS app the
  // order is the server's (email first), so the form and the demo show from the first paint —
  // on a cold start over a weak signal the bundle can take seconds — and only the passkey
  // button waits, invisibly holding its place so nothing shifts when it appears. The installed
  // iOS app puts the passkey first instead; the inline script below marks it before the first
  // paint, and there the actions wait for the client (a frame or two, from the cached bundle)
  // rather than painting one order and flipping to the other as a finger reaches for it.
  let ready = $state(false);

  $effect(() => {
    passkeySupported = isPasskeySupported();
    inIosHomeScreenApp = isInstalledApp() && isIosDevice();
    ready = true;
  });

  // The prominent action is the one that can actually finish here (HIG buttons: the most likely
  // action gets the prominent style). In the installed iOS app that is the passkey — an emailed
  // link would sign in Safari, not this app — so it moves first and takes the fill, and email
  // steps down to a gray button with the reason beside it. Everywhere else email leads.
  const passkeyFirst = $derived(inIosHomeScreenApp && passkeySupported);

  // The send button stays enabled at rest — a dimmed main action reads as broken — so an empty or
  // mistyped address is caught here, with the reason next to the field.
  function emailProblem(value: string): string | null {
    if (!value) return 'Enter your email address to get a sign-in link.';
    if (!/^[^\s@]+@[^\s@]+$/.test(value)) return "That doesn't look like an email address.";
    return null;
  }

  function onEmailInput() {
    result = null;
    emailError = null;
  }

  async function handlePasskeyLogin() {
    isPasskeyLogin = true;
    result = null;
    try {
      await loginWithPasskey();
      enterApp();
    } catch (err) {
      result = {
        ok: false,
        message: err instanceof Error ? err.message : 'Passkey sign-in failed.'
      };
    } finally {
      isPasskeyLogin = false;
    }
  }

  async function handleSubmit(event: SubmitEvent) {
    event.preventDefault();
    const value = email.trim();
    emailError = emailProblem(value);
    if (emailError) {
      (event.currentTarget as HTMLFormElement).querySelector('input')?.focus();
      return;
    }
    isSending = true;
    result = null;
    try {
      const r = await requestMagicLink(value);
      if (r.ok) {
        result = {
          ok: true,
          sent: true,
          magicLinkUrl: r.magicLinkUrl,
          magicLinkInLogs: r.magicLinkInLogs
        };
      } else {
        // The API logs why; the person signing in can't act on "check the mail provider".
        result = {
          ok: false,
          message:
            "Couldn't send the sign-in email. Ask whoever runs this server to check its email settings."
        };
      }
    } catch {
      // requestMagicLink only throws when the request never got an answer.
      result = {
        ok: false,
        message: "Couldn't reach the server. Check your connection and try again."
      };
    } finally {
      isSending = false;
    }
  }

  async function handleTryDemo() {
    isStartingDemo = true;
    try {
      await signInAsDemo();
      enterApp();
    } catch (err) {
      result = {
        ok: false,
        message: err instanceof Error ? err.message : 'Could not start demo session.'
      };
    } finally {
      isStartingDemo = false;
    }
  }

  // Leaving "Add an account" without adding one (HIG modality: a modal view always has an obvious
  // way out; the installed app has no browser Back). Back where they came from when that was a
  // page of this app — Settings › Account or the account sheet — else the app home. No identity
  // changed, so the app's module state is still right for the page it returns to.
  function cancelSwitch() {
    let fromThisApp = false;
    try {
      fromThisApp =
        document.referrer !== '' && new URL(document.referrer).origin === location.origin;
    } catch {
      // an unparsable referrer: treat as none
    }
    if (fromThisApp && history.length > 1) history.back();
    else location.assign(landingRoute());
  }

  // iOS sign-in proportions: 50pt filled fields and capsule buttons with headline labels on a
  // phone; the desktop card keeps a 40px control height.
  const BIG = 'h-[50px] w-full rounded-full text-headline md:h-10 md:text-sm md:font-medium';

  // Marks the installed iOS app on <html> before the first paint (the same test as isInstalledApp
  // + isIosDevice), so the action blocks below can wait for the client only there.
  const MARK_IOS_APP =
    `<script>try{var n=navigator;if(n.standalone===true&&(/\\b(iPhone|iPad|iPod)\\b/.test(n.userAgent)||(/\\bMacintosh\\b/.test(n.userAgent)&&n.maxTouchPoints>1)))document.documentElement.setAttribute('data-mh-ios-app','')}catch(e){}<` +
    '/script>';
  // Until the client has decided, the action blocks are hidden only inside the installed iOS app.
  const waiting = $derived(ready ? '' : 'in-data-mh-ios-app:invisible');
</script>

<svelte:head>
  <title>{data.switching ? 'Add an account' : 'Sign in'} · MusicHoarder</title>
  <!-- eslint-disable-next-line svelte/no-at-html-tags -- a fixed string, no input in it -->
  {@html MARK_IOS_APP}
</svelte:head>

{#snippet passkeyButton(prominent: boolean)}
  <Button
    type="button"
    variant={prominent ? 'default' : 'gray'}
    class={BIG}
    onclick={handlePasskeyLogin}
    disabled={isPasskeyLogin}
  >
    {#if isPasskeyLogin}
      <Loader2 class="size-5 animate-spin md:size-4" />
      Waiting for passkey…
    {:else}
      <KeyRound class="size-5 md:size-4" />
      Sign in with a passkey
    {/if}
  </Button>
{/snippet}

{#snippet divider()}
  <div class="text-footnote text-muted-foreground flex items-center gap-3" aria-hidden="true">
    <span class="bg-separator h-(--hairline) flex-1"></span>
    or
    <span class="bg-separator h-(--hairline) flex-1"></span>
  </div>
{/snippet}

{#snippet emailForm(prominent: boolean)}
  <form onsubmit={handleSubmit} class="flex flex-col gap-3" novalidate>
    <div class="flex flex-col gap-1.5">
      <label for="login-email" class="text-subheadline text-muted-foreground md:text-sm"
        >Email</label
      >
      <Input
        id="login-email"
        type="email"
        autocomplete="email"
        enterkeyhint="go"
        placeholder="you@example.com"
        bind:value={email}
        oninput={onEmailInput}
        aria-invalid={emailError ? 'true' : undefined}
        aria-describedby={emailError ? 'login-email-error' : undefined}
        class="text-body h-[50px] rounded-xl px-4 md:h-10 md:rounded-lg md:px-3 md:text-sm"
        required
      />
      {#if emailError}
        <p
          id="login-email-error"
          class="text-footnote text-destructive-text md:text-sm"
          role="alert"
        >
          {emailError}
        </p>
      {/if}
    </div>
    <Button type="submit" variant={prominent ? 'default' : 'gray'} class={BIG} disabled={isSending}>
      {#if isSending}
        <Loader2 class="size-5 animate-spin md:size-4" />
        Sending…
      {:else}
        <Mail class="size-5 md:size-4" />
        Send me a magic link
      {/if}
    </Button>
  </form>
{/snippet}

<!-- One tree at every width: a full-bleed phone screen, a centred card from md. (The page used
     to be two trees with different titles and copy; every change had to be made twice.) -->
<main
  class="bg-background text-foreground flex min-h-dvh flex-col md:items-center md:justify-center md:p-6"
>
  <div
    class="md:border-border md:bg-card flex w-full flex-1 flex-col px-6 pt-[calc(2.5rem+env(safe-area-inset-top))] pb-[calc(2rem+env(safe-area-inset-bottom))] md:max-w-md md:flex-none md:rounded-2xl md:border md:p-8 md:shadow-sm"
  >
    {#if data.switching}
      <!-- Cancel leads, where iOS puts a modal's way out. -->
      <div class="-mx-3 -mt-7 mb-3 flex md:-mx-2 md:-mt-4 md:mb-2">
        <Button
          variant="ghost"
          class="text-primary hover:text-primary aria-expanded:text-primary text-body h-11 rounded-full px-3 font-normal md:h-9 md:px-2 md:text-sm"
          onclick={cancelSwitch}
        >
          Cancel
        </Button>
      </div>
    {/if}
    <BrandMark class="size-12 md:size-10" />
    <h1 class="text-title-1 mt-6 md:mt-5 md:text-2xl md:font-semibold">
      {data.switching ? 'Add an account' : 'Sign in'}
    </h1>
    <!-- Static, so it cannot reflow after hydration. -->
    <p class="text-callout text-muted-foreground mt-1.5 md:text-sm">
      Sign in to your MusicHoarder library with an emailed link or a passkey — there are no
      passwords.
    </p>

    {#if data.switching && data.currentUser}
      <div
        class="bg-muted text-subheadline mt-5 flex items-start gap-2.5 rounded-xl px-4 py-3 md:text-sm"
      >
        <UserPlus class="text-muted-foreground mt-0.5 size-4 shrink-0" aria-hidden="true" />
        <span>
          You stay signed in as
          <span class="font-semibold">{data.currentUser.displayName ?? data.currentUser.email}</span
          > — the account you sign in with here becomes the active one, and you can switch back any time.
        </span>
      </div>
    {/if}

    <div class="mt-7 flex flex-col gap-4 md:mt-6 {waiting}">
      {#if passkeyFirst}
        {@render passkeyButton(true)}
        <p class="text-footnote text-muted-foreground text-center">
          Email links open in Safari, not in this app — sign in here with a passkey. You can add one
          in Safari under Settings → Account.
        </p>
        {@render divider()}
        {@render emailForm(false)}
      {:else}
        {@render emailForm(true)}
        {#if inIosHomeScreenApp}
          <p class="text-footnote text-muted-foreground text-center">
            Email links open in Safari, not in this app.
          </p>
        {/if}
      {/if}
    </div>

    {#if result?.ok}
      <div class="mt-5 flex items-start gap-2.5" role="status">
        <CircleCheck class="text-primary mt-0.5 size-5 shrink-0 md:size-4" aria-hidden="true" />
        <div class="min-w-0 flex-1">
          {#if result.magicLinkInLogs && !result.magicLinkUrl}
            <p class="text-headline md:text-sm md:font-semibold">
              Your sign-in link is in the server logs.
            </p>
            <p class="text-subheadline text-muted-foreground mt-1 md:text-xs">
              This server has no email service configured, so links are never emailed. If
              <span class="text-foreground break-words">{email}</span> matches a known account, the link
              was just written to the API logs. Find it with:
            </p>
            <code
              class="bg-muted text-footnote mt-2.5 block rounded-lg px-3 py-2.5 font-mono break-words select-all md:text-xs"
              >docker compose logs api | grep -i magic</code
            >
            <p class="text-subheadline text-muted-foreground mt-2 md:text-xs">
              Open the printed URL in this browser. It expires in 15 minutes.
            </p>
          {:else}
            <p class="text-headline md:text-sm md:font-semibold">Check your email.</p>
            <p class="text-subheadline text-muted-foreground mt-1 md:text-xs">
              If <span class="text-foreground break-words">{email}</span> matches a known account, a sign-in
              link is on its way. It expires in 15 minutes.
            </p>
            {#if result.magicLinkUrl}
              <div class="bg-muted mt-2.5 rounded-lg px-3 py-2.5">
                <p class="text-footnote text-muted-foreground flex items-center gap-1 md:text-xs">
                  <Sparkles class="size-3.5" aria-hidden="true" /> Dev mode — link not emailed:
                </p>
                <a
                  href={result.magicLinkUrl}
                  class="text-subheadline text-primary mt-1 inline-flex items-center py-1 underline-offset-2 hover:underline md:text-sm pointer-coarse:min-h-11"
                >
                  Sign in with this link
                </a>
              </div>
            {/if}
          {/if}
        </div>
      </div>
    {:else if result && !result.ok}
      <p
        class="text-subheadline text-destructive-text mt-5 flex items-start gap-2.5 md:text-sm"
        role="alert"
      >
        <CircleAlert class="mt-0.5 size-5 shrink-0 md:size-4" aria-hidden="true" />
        <span>{result.message}</span>
      </p>
    {/if}

    <div class="mt-7 flex flex-col gap-4 md:mt-6 {waiting}">
      {@render divider()}
      <!-- Held (invisible) until support is known: nearly every browser has passkeys, so the
           demo button below does not jump down when this one arrives. -->
      {#if !passkeyFirst && (passkeySupported || !ready)}
        <div class="flex flex-col {ready ? '' : 'invisible'}">
          {@render passkeyButton(false)}
        </div>
      {/if}

      <div class="flex flex-col gap-2">
        <Button
          type="button"
          variant="gray"
          class={BIG}
          onclick={handleTryDemo}
          disabled={isStartingDemo}
        >
          {#if isStartingDemo}
            <Loader2 class="size-5 animate-spin md:size-4" />
            Starting…
          {:else}
            <Sparkles class="size-5 md:size-4" />
            Try the demo
          {/if}
        </Button>
        <p class="text-footnote text-muted-foreground text-center md:text-xs">
          The demo account is read-only and shares a seeded library.
        </p>
      </div>
    </div>
  </div>
</main>
