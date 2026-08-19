<script lang="ts">
  import QRCode from 'qrcode';
  import { Button } from '$lib/components/ui/button';
  import { createDeviceToken } from '$lib/api-client';
  import { AlertCircle, Check, Copy, Loader2, Smartphone } from '@lucide/svelte';

  /**
   * Pairs the native (Android) client with this deployment. The QR encodes the origin the phone
   * should talk to plus a freshly minted bearer token; the app then speaks to the very same
   * `/api/mh` proxy the browser uses, so there is nothing extra to expose publicly.
   *
   * The token rides its own server-side session row, so signing this browser out leaves the phone
   * paired — "Sign out everywhere" is what revokes it.
   */
  const PAIR_URI_VERSION = 1;

  let qrSvg = $state<string | null>(null);
  let baseUrl = $state<string | null>(null);
  let token = $state<string | null>(null);
  let expiresAtUtc = $state<string | null>(null);
  let isMinting = $state(false);
  let error = $state<string | null>(null);
  let showFallback = $state(false);
  let copied = $state(false);

  async function pair() {
    isMinting = true;
    error = null;
    try {
      const result = await createDeviceToken();
      // The phone talks to this frontend's origin, not the API's — the same-origin `/api/mh`
      // proxy forwards the Authorization header through to the API.
      const origin = window.location.origin;
      const uri =
        `musichoarder://pair?v=${PAIR_URI_VERSION}` +
        `&url=${encodeURIComponent(origin)}` +
        `&token=${encodeURIComponent(result.accessToken)}`;

      qrSvg = await QRCode.toString(uri, {
        type: 'svg',
        errorCorrectionLevel: 'L',
        margin: 1,
        color: { dark: '#000000ff', light: '#ffffffff' }
      });
      baseUrl = origin;
      token = result.accessToken;
      expiresAtUtc = result.expiresAtUtc;
    } catch (err) {
      error = err instanceof Error ? err.message : 'Could not create a pairing code.';
    } finally {
      isMinting = false;
    }
  }

  function hide() {
    qrSvg = null;
    baseUrl = null;
    token = null;
    expiresAtUtc = null;
    showFallback = false;
    copied = false;
  }

  async function copyToken() {
    if (!token) return;
    await navigator.clipboard.writeText(token);
    copied = true;
    setTimeout(() => (copied = false), 2000);
  }
</script>

<section class="border-border bg-card rounded-lg border">
  <header class="border-border border-b px-5 py-3.5">
    <h2 class="flex items-center gap-2 text-sm font-semibold">
      <Smartphone class="size-4" /> Mobile app
    </h2>
    <p class="text-muted-foreground text-xs">
      Pair the MusicHoarder Android app with this server. Scanning the code signs the phone in on
      its own session — signing this browser out leaves it paired, "Sign out everywhere" revokes it.
    </p>
  </header>

  <div class="space-y-4 p-5">
    {#if error}
      <div
        class="border-destructive/50 bg-destructive/10 text-destructive flex items-start gap-2 rounded-lg border px-4 py-3 text-sm"
      >
        <AlertCircle class="mt-0.5 size-4 shrink-0" />
        <span>{error}</span>
      </div>
    {/if}

    {#if !qrSvg}
      <Button onclick={pair} disabled={isMinting}>
        {#if isMinting}
          <Loader2 class="mr-2 size-4 animate-spin" />
        {:else}
          <Smartphone class="mr-2 size-4" />
        {/if}
        Show pairing code
      </Button>
    {:else}
      <div class="flex flex-col items-start gap-4 sm:flex-row">
        <!-- White plate regardless of theme: scanners need the quiet zone light. -->
        <div class="shrink-0 rounded-lg bg-white p-3 shadow-sm">
          <div class="size-44 [&>svg]:size-full">
            <!-- eslint-disable-next-line svelte/no-at-html-tags -->
            {@html qrSvg}
          </div>
        </div>

        <div class="min-w-0 flex-1 space-y-3">
          <div
            class="border-border bg-secondary/40 text-foreground/80 rounded-lg border px-4 py-3 text-xs leading-relaxed"
          >
            This code grants full access to your library — treat it like a password, and hide it
            once the phone is paired.
            {#if expiresAtUtc}
              The device session expires {new Date(expiresAtUtc).toLocaleDateString()}.
            {/if}
          </div>

          <div class="flex flex-wrap items-center gap-2">
            <Button variant="outline" size="sm" onclick={hide}>Hide code</Button>
            <Button variant="ghost" size="sm" onclick={() => (showFallback = !showFallback)}>
              {showFallback ? 'Hide manual entry' : "Can't scan?"}
            </Button>
          </div>

          {#if showFallback}
            <div class="space-y-2">
              <div>
                <div class="text-muted-foreground text-[11px]">Server URL</div>
                <div class="border-border bg-secondary/30 truncate rounded-md border px-3 py-2 font-mono text-xs">
                  {baseUrl}
                </div>
              </div>
              <div>
                <div class="text-muted-foreground text-[11px]">Access token</div>
                <div class="flex items-center gap-2">
                  <div
                    class="border-border bg-secondary/30 min-w-0 flex-1 truncate rounded-md border px-3 py-2 font-mono text-xs"
                  >
                    {token}
                  </div>
                  <Button
                    variant="ghost"
                    size="icon"
                    class="size-8 shrink-0"
                    onclick={copyToken}
                    aria-label="Copy access token"
                  >
                    {#if copied}
                      <Check class="size-4" />
                    {:else}
                      <Copy class="size-4" />
                    {/if}
                  </Button>
                </div>
              </div>
            </div>
          {/if}
        </div>
      </div>
    {/if}
  </div>
</section>
