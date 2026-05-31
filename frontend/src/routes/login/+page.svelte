<script lang="ts">
  import { goto } from '$app/navigation';
  import { Button } from '$lib/components/ui/button';
  import { Input } from '$lib/components/ui/input';
  import { Label } from '$lib/components/ui/label';
  import { requestMagicLink, signInAsDemo, loginWithPasskey } from '$lib/api-client';
  import { isPasskeySupported } from '$lib/webauthn-client';
  import { uiVersion } from '$lib/stores/ui-version.svelte';
  import { LogIn, Mail, Loader2, CheckCircle2, AlertCircle, ExternalLink, Sparkles, KeyRound } from '@lucide/svelte';

  // The app entry differs by design version: v2 lands on the conveyor home
  // (/pipeline), v1 keeps its long-standing landing (/library). The flag is a
  // per-browser localStorage value, readable here in the browser; defaulting to
  // v1 means existing users are unaffected until they opt in.
  const landingRoute = () => (uiVersion.isV2 ? '/pipeline' : '/library');

  let email = $state('');
  let isSending = $state(false);
  let result = $state<
    | null
    | { ok: true; sent: true; magicLinkUrl?: string | null }
    | { ok: false; message: string }
  >(null);
  let isStartingDemo = $state(false);
  let isPasskeyLogin = $state(false);
  let passkeySupported = $state(false);

  $effect(() => {
    passkeySupported = isPasskeySupported();
  });

  async function handlePasskeyLogin() {
    isPasskeyLogin = true;
    result = null;
    try {
      await loginWithPasskey();
      await goto(landingRoute(), { invalidateAll: true });
    } catch (err) {
      result = {
        ok: false,
        message: err instanceof Error ? err.message : 'Passkey sign-in failed.'
      };
    } finally {
      isPasskeyLogin = false;
    }
  }

  async function handleSubmit(event: Event) {
    event.preventDefault();
    if (!email.trim()) return;
    isSending = true;
    result = null;
    try {
      const r = await requestMagicLink(email.trim());
      if (r.ok) {
        result = { ok: true, sent: true, magicLinkUrl: r.magicLinkUrl };
      } else {
        result = { ok: false, message: 'Could not send email. Check Resend configuration.' };
      }
    } catch (err) {
      result = {
        ok: false,
        message: err instanceof Error ? err.message : 'Unknown error.'
      };
    } finally {
      isSending = false;
    }
  }

  async function handleTryDemo() {
    isStartingDemo = true;
    try {
      await signInAsDemo();
      await goto(landingRoute(), { invalidateAll: true });
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

<!-- Mobile login -->
<div class="mob-surface flex min-h-screen flex-col md:hidden">
  <div class="mob-login flex-1">
    <div class="mob-logo size-11" style="background: var(--primary);">
      <svg width="22" height="22" viewBox="0 0 18 18" fill="none" aria-hidden="true">
        <rect x="1" y="1" width="16" height="16" rx="3.5" fill="var(--primary)" />
        <path d="M5.5 12.5V6l6-1.2V11" stroke="#fff" stroke-width="1.4" stroke-linecap="round" />
        <circle cx="5" cy="12.5" r="1.3" fill="#fff" />
        <circle cx="11" cy="11" r="1.3" fill="#fff" />
      </svg>
    </div>
    <h1 class="mob-login-h">Welcome back.</h1>
    <div class="mob-login-s">Magic-link sign-in to your library. It stays on this host.</div>

    <form class="mob-login-fields" onsubmit={handleSubmit}>
      <label class="mob-login-field">
        <span>EMAIL</span>
        <input
          type="email"
          autocomplete="email"
          placeholder="you@example.com"
          bind:value={email}
          oninput={() => (result = null)}
          required
        />
      </label>
      <button type="submit" class="mob-btn primary" disabled={isSending || !email.trim()}>
        {isSending ? 'Sending…' : 'Send me a magic link'}
      </button>
    </form>

    {#if result?.ok}
      <div class="text-primary mt-4 text-[13px]">
        Check your email — a sign-in link is on its way (expires in 15 min).
        {#if result.magicLinkUrl}
          <a href={result.magicLinkUrl} class="mob-login-link mt-2 block">Dev mode: click here to sign in →</a>
        {/if}
      </div>
    {:else if result && !result.ok}
      <div class="text-destructive mt-4 text-[13px]">{result.message}</div>
    {/if}

    <div class="mob-login-or"><span>or</span></div>

    {#if passkeySupported}
      <button type="button" class="mob-btn" onclick={handlePasskeyLogin} disabled={isPasskeyLogin}>
        {isPasskeyLogin ? 'Waiting for passkey…' : 'Sign in with a passkey'}
      </button>
    {/if}

    <button type="button" class="mob-btn" onclick={handleTryDemo} disabled={isStartingDemo}>
      {isStartingDemo ? 'Starting…' : 'Try the demo'}
    </button>
    <p class="text-muted-foreground mt-2 text-center text-[12px]">
      The demo account is read-only and shares a seeded library.
    </p>
  </div>
</div>

<!-- Desktop login -->
<div class="bg-background hidden min-h-screen items-center justify-center p-6 md:flex">
  <div class="border-border bg-card w-full max-w-md rounded-2xl border p-8 shadow-sm">
    <div class="mb-6 flex items-center gap-3">
      <div class="bg-secondary flex size-10 items-center justify-center rounded-lg">
        <LogIn class="text-foreground size-5" />
      </div>
      <div>
        <h1 class="text-2xl font-semibold tracking-tight">Sign in</h1>
        <p class="text-muted-foreground text-sm">Magic-link sign-in to MusicHoarder.</p>
      </div>
    </div>

    <form onsubmit={handleSubmit} class="space-y-4">
      <div class="space-y-2">
        <Label for="email">Email</Label>
        <Input
          id="email"
          type="email"
          autocomplete="email"
          placeholder="you@example.com"
          bind:value={email}
          oninput={() => (result = null)}
          class="font-mono text-sm"
          required
        />
      </div>

      <Button type="submit" disabled={isSending || !email.trim()} class="w-full">
        {#if isSending}
          <Loader2 class="mr-2 size-4 animate-spin" />
        {:else}
          <Mail class="mr-2 size-4" />
        {/if}
        Send me a magic link
      </Button>
    </form>

    {#if result?.ok}
      <div
        class="border-primary/40 bg-primary/5 text-primary mt-4 flex items-start gap-2 rounded-lg border px-4 py-3 text-sm"
      >
        <CheckCircle2 class="mt-0.5 size-4 shrink-0" />
        <div class="min-w-0 flex-1">
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
                Click here to sign in <ExternalLink class="size-3" />
              </a>
            </div>
          {/if}
        </div>
      </div>
    {:else if result && !result.ok}
      <div
        class="border-destructive/50 bg-destructive/10 text-destructive mt-4 flex items-start gap-2 rounded-lg border px-4 py-3 text-sm"
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
