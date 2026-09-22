<script lang="ts">
  import { page } from '$app/state';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Label } from '$lib/components/ui/label';
  import BrandMark from '$lib/components/BrandMark.svelte';
  import { requestMagicLink, signInAsDemo, loginWithPasskey } from '$lib/api-client';
  import { isPasskeySupported } from '$lib/webauthn-client';
  import { isInstalledApp } from '$lib/hooks/viewport-insets.svelte';
  import { isIosDevice } from '$lib/ios-safari';
  import { APP_HOME } from '$lib/app-home';
  import { Mail, Loader2, CheckCircle2, AlertCircle, ExternalLink, Sparkles, KeyRound, UserPlus } from '@lucide/svelte';
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
  >(callbackError ? { ok: false, message: CALLBACK_ERRORS[callbackError] ?? CALLBACK_ERRORS.signin } : null);
  let isStartingDemo = $state(false);
  let isPasskeyLogin = $state(false);
  let passkeySupported = $state(false);
  // An iOS Home Screen app has its own cookie jar and never receives links — an emailed sign-in
  // link always opens in Safari — so inside it a passkey is the way in.
  let inIosHomeScreenApp = $state(false);

  $effect(() => {
    passkeySupported = isPasskeySupported();
    inIosHomeScreenApp = isInstalledApp() && isIosDevice();
  });

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
        result = { ok: true, sent: true, magicLinkUrl: r.magicLinkUrl, magicLinkInLogs: r.magicLinkInLogs };
      } else {
        // The API logs why; the person signing in can't act on "check the mail provider".
        result = {
          ok: false,
          message: "Couldn't send the sign-in email. Ask whoever runs this server to check its email settings."
        };
      }
    } catch {
      // requestMagicLink only throws when the request never got an answer.
      result = { ok: false, message: "Couldn't reach the server. Check your connection and try again." };
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
</script>

<svelte:head>
  <title>Sign in · MusicHoarder</title>
</svelte:head>

<!-- Mobile login -->
<div class="mob-surface flex min-h-dvh flex-col md:hidden">
  <div class="mob-login flex-1">
    <BrandMark class="size-11" />
    <h1 class="mob-login-h">{data.switching ? 'Add an account.' : 'Welcome back.'}</h1>
    <div class="mob-login-s">Magic-link sign-in to your library. It stays on this host.</div>

    {#if data.switching && data.currentUser}
      <div class="text-primary mt-3 text-[13px]">
        You stay signed in as <span class="font-medium">{data.currentUser.displayName ?? data.currentUser.email}</span>
        — the account you sign in with here becomes the active one, and you can switch back any time.
      </div>
    {/if}

    <form class="mob-login-fields" onsubmit={handleSubmit} novalidate>
      <label class="mob-login-field">
        <span>EMAIL</span>
        <input
          type="email"
          inputmode="email"
          autocomplete="email"
          autocapitalize="off"
          autocorrect="off"
          spellcheck={false}
          enterkeyhint="go"
          placeholder="you@example.com"
          bind:value={email}
          oninput={onEmailInput}
          aria-invalid={emailError ? 'true' : undefined}
          aria-describedby={emailError ? 'login-email-error-mobile' : undefined}
          required
        />
      </label>
      {#if emailError}
        <p id="login-email-error-mobile" class="text-destructive-text -mt-1 text-[13px]" role="alert">
          {emailError}
        </p>
      {/if}
      <button type="submit" class="mob-btn primary" disabled={isSending}>
        {isSending ? 'Sending…' : 'Send me a magic link'}
      </button>
      {#if inIosHomeScreenApp && passkeySupported}
        <p class="text-muted-foreground text-center text-[12px] leading-snug">
          Email links open in Safari, not in this app — sign in here with a passkey. You can add one
          in Safari under Settings → Account.
        </p>
      {/if}
    </form>

    {#if result?.ok}
      <div class="text-primary mt-4 text-[13px]">
        {#if result.magicLinkInLogs && !result.magicLinkUrl}
          No email service is configured on this server — if that address matches a known account,
          your sign-in link was just written to the server logs (expires in 15 min). Find it with:
          <code class="mt-2 block font-mono text-[12px] break-all select-all"
            >docker compose logs api | grep -i magic</code
          >
        {:else}
          Check your email — a sign-in link is on its way (expires in 15 min).
          {#if result.magicLinkUrl}
            <a href={result.magicLinkUrl} class="mob-login-link mt-2 block">Dev mode: sign in with this link →</a>
          {/if}
        {/if}
      </div>
    {:else if result && !result.ok}
      <div class="text-destructive-text mt-4 text-[13px]" role="alert">{result.message}</div>
    {/if}

    <div class="mob-login-or"><span>or</span></div>

    <div class="mob-login-alts">
      {#if passkeySupported}
        <button type="button" class="mob-btn" onclick={handlePasskeyLogin} disabled={isPasskeyLogin}>
          {isPasskeyLogin ? 'Waiting for passkey…' : 'Sign in with a passkey'}
        </button>
      {/if}

      <button type="button" class="mob-btn" onclick={handleTryDemo} disabled={isStartingDemo}>
        {isStartingDemo ? 'Starting…' : 'Try the demo'}
      </button>
    </div>
    <p class="text-muted-foreground mt-2 text-center text-[12px]">
      The demo account is read-only and shares a seeded library.
    </p>
  </div>
</div>

<!-- Desktop login -->
<div class="bg-background hidden min-h-dvh items-center justify-center p-6 md:flex">
  <div class="border-border bg-card w-full max-w-md rounded-2xl border p-8 shadow-sm">
    <div class="mb-6 flex items-center gap-3">
      <BrandMark class="size-10" />
      <div>
        <h1 class="text-2xl font-semibold tracking-tight">{data.switching ? 'Add an account' : 'Sign in'}</h1>
        <p class="text-muted-foreground text-sm">Magic-link sign-in to MusicHoarder.</p>
      </div>
    </div>

    {#if data.switching && data.currentUser}
      <div
        class="border-primary/40 bg-primary/5 text-primary mb-4 flex items-start gap-2 rounded-lg border px-4 py-3 text-sm"
      >
        <UserPlus class="mt-0.5 size-4 shrink-0" />
        <span>
          You stay signed in as
          <span class="font-medium">{data.currentUser.displayName ?? data.currentUser.email}</span> — the
          account you sign in with here becomes the active one, and you can switch back any time.
        </span>
      </div>
    {/if}

    <form onsubmit={handleSubmit} class="space-y-4" novalidate>
      <div class="space-y-2">
        <Label for="email">Email</Label>
        <Input
          id="email"
          type="email"
          autocomplete="email"
          enterkeyhint="go"
          placeholder="you@example.com"
          bind:value={email}
          oninput={onEmailInput}
          aria-invalid={emailError ? 'true' : undefined}
          aria-describedby={emailError ? 'login-email-error' : undefined}
          class="font-mono"
          required
        />
        {#if emailError}
          <p id="login-email-error" class="text-destructive-text text-sm" role="alert">{emailError}</p>
        {/if}
      </div>

      <Button type="submit" disabled={isSending} class="w-full">
        {#if isSending}
          <Loader2 class="mr-2 size-4 animate-spin" />
        {:else}
          <Mail class="mr-2 size-4" />
        {/if}
        Send me a magic link
      </Button>
      {#if inIosHomeScreenApp && passkeySupported}
        <p class="text-muted-foreground text-center text-xs">
          Email links open in Safari, not in this app — sign in here with a passkey. You can add one
          in Safari under Settings → Account.
        </p>
      {/if}
    </form>

    {#if result?.ok}
      <div
        class="border-primary/40 bg-primary/5 text-primary mt-4 flex items-start gap-2 rounded-lg border px-4 py-3 text-sm"
      >
        <CheckCircle2 class="mt-0.5 size-4 shrink-0" />
        <div class="min-w-0 flex-1">
          {#if result.magicLinkInLogs && !result.magicLinkUrl}
            <p class="font-medium">Your sign-in link is in the server logs.</p>
            <p class="text-foreground/70 mt-1 text-xs">
              This server has no email service configured, so links are never emailed. If
              <span class="font-mono">{email}</span> matches a known account, the link was just
              written to the API logs. Find it with:
            </p>
            <div
              class="border-primary/30 bg-background/80 mt-3 rounded-md border p-3 font-mono text-xs break-all select-all"
            >
              docker compose logs api | grep -i magic
            </div>
            <p class="text-foreground/70 mt-2 text-xs">
              Open the printed URL in this browser. It expires in 15 minutes.
            </p>
          {:else}
            <p class="font-medium">Check your email.</p>
            <p class="text-foreground/70 mt-1 text-xs">
              If <span class="font-mono">{email}</span> matches a known account, a sign-in link is on
              its way. The link expires in 15 minutes.
            </p>
            {#if result.magicLinkUrl}
              <div
                class="border-primary/30 bg-background/80 mt-3 rounded-md border p-3 text-xs break-all"
              >
                <div class="text-muted-foreground mb-1 flex items-center gap-1">
                  <Sparkles class="size-3" /> Dev mode — link not emailed:
                </div>
                <a
                  href={result.magicLinkUrl}
                  class="text-primary inline-flex items-center gap-1 hover:underline"
                >
                  Sign in with this link <ExternalLink class="size-3" />
                </a>
              </div>
            {/if}
          {/if}
        </div>
      </div>
    {:else if result && !result.ok}
      <div
        class="border-destructive/50 bg-destructive/10 text-destructive-text mt-4 flex items-start gap-2 rounded-lg border px-4 py-3 text-sm"
        role="alert"
      >
        <AlertCircle class="mt-0.5 size-4 shrink-0" />
        <span>{result.message}</span>
      </div>
    {/if}

    <div class="border-border my-6 border-t"></div>

    {#if passkeySupported}
      <Button
        type="button"
        variant="outline"
        class="mb-3 w-full"
        onclick={handlePasskeyLogin}
        disabled={isPasskeyLogin}
      >
        {#if isPasskeyLogin}
          <Loader2 class="mr-2 size-4 animate-spin" />
        {:else}
          <KeyRound class="mr-2 size-4" />
        {/if}
        Sign in with a passkey
      </Button>
    {/if}

    <Button
      type="button"
      variant="outline"
      class="w-full"
      onclick={handleTryDemo}
      disabled={isStartingDemo}
    >
      {#if isStartingDemo}
        <Loader2 class="mr-2 size-4 animate-spin" />
      {:else}
        <Sparkles class="mr-2 size-4" />
      {/if}
      Try the demo
    </Button>
    <p class="text-muted-foreground mt-2 text-center text-xs">
      The demo account is read-only and shares a seeded library.
    </p>
  </div>
</div>
