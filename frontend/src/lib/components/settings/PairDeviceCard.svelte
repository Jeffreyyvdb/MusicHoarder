<script lang="ts">
  import QRCode from 'qrcode';
  import { toast } from 'svelte-sonner';
  import { Button } from '$lib/components/ui/button';
  import * as GroupedList from '$lib/components/ui/grouped-list';
  import FieldRow from '$lib/components/settings/FieldRow.svelte';
  import StatusLine from '$lib/components/settings/StatusLine.svelte';
  import { COPY_FAILED_MESSAGE, copyText } from '$lib/components/settings/copy-text';
  import { createDeviceToken } from '$lib/api-client';
  import { Check, ChevronDown, Copy, Loader2, Smartphone, TriangleAlert } from '@lucide/svelte';

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
    if (!(await copyText(token))) {
      toast.error(COPY_FAILED_MESSAGE);
      return;
    }
    copied = true;
    setTimeout(() => (copied = false), 2000);
  }
</script>

{#snippet footer()}
  <div class="flex flex-col gap-2">
    {#if error}
      <StatusLine tone="error">{error}</StatusLine>
    {/if}
    <p>
      Pair the MusicHoarder Android app with this server. Scanning the code signs the phone in on
      its own session — signing this browser out leaves it paired; Sign out everywhere revokes it.
    </p>
  </div>
{/snippet}

<GroupedList.Section headingLevel={2} header="Mobile app" {footer}>
  {#if !qrSvg}
    <GroupedList.Row icon={Smartphone} onclick={pair} disabled={isMinting}>
      <span class="text-body text-primary md:text-sm">Show pairing code</span>
      {#snippet trailing()}
        {#if isMinting}
          <Loader2 class="text-muted-foreground size-4 animate-spin" aria-hidden="true" />
        {/if}
      {/snippet}
    </GroupedList.Row>
  {:else}
    <div
      data-slot="grouped-list-row"
      class="after:bg-separator relative flex flex-col items-center gap-3 px-4 py-4 after:absolute after:right-0 after:bottom-0 after:left-4 after:h-(--hairline)"
    >
      <!-- White plate regardless of theme: scanners need the quiet zone light. -->
      <div class="rounded-xl bg-white p-3 shadow-sm">
        <div class="size-48 [&>svg]:size-full" role="img" aria-label="Pairing QR code">
          <!-- eslint-disable-next-line svelte/no-at-html-tags -->
          {@html qrSvg}
        </div>
      </div>
      <p class="text-subheadline text-muted-foreground flex max-w-md items-start gap-2 md:text-xs">
        <TriangleAlert class="text-warning-text mt-0.5 size-4 shrink-0" aria-hidden="true" />
        <span>
          This code grants full access to your library — treat it like a password, and hide it once
          the phone is paired.
          {#if expiresAtUtc}
            The device session expires {new Date(expiresAtUtc).toLocaleDateString()}.
          {/if}
        </span>
      </p>
    </div>
    <GroupedList.Row
      onclick={() => (showFallback = !showFallback)}
      aria-expanded={showFallback}
      label="Can’t scan?"
      sublabel="Enter the server and token by hand"
    >
      {#snippet trailing()}
        <ChevronDown
          class="text-muted-foreground-dim size-4 transition-transform duration-200 {showFallback
            ? 'rotate-180'
            : ''}"
          strokeWidth={2.5}
          aria-hidden="true"
        />
      {/snippet}
    </GroupedList.Row>
    {#if showFallback}
      <FieldRow label="Server URL">
        <p class="text-body font-mono break-all select-all md:text-xs">{baseUrl}</p>
      </FieldRow>
      <FieldRow label="Access token">
        <div class="flex items-center gap-1">
          <p class="text-body min-w-0 flex-1 truncate font-mono select-all md:text-xs">{token}</p>
          <Button
            variant="ghost"
            size="icon"
            class="shrink-0 pointer-coarse:size-11"
            onclick={copyToken}
            aria-label={copied ? 'Copied' : 'Copy access token'}
          >
            {#if copied}
              <Check class="text-primary size-4" />
            {:else}
              <Copy class="size-4" />
            {/if}
          </Button>
        </div>
      </FieldRow>
    {/if}
    <GroupedList.Row onclick={hide}>
      <span class="text-body text-primary md:text-sm">Hide code</span>
    </GroupedList.Row>
  {/if}
</GroupedList.Section>
