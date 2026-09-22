<script lang="ts">
  import { HousePlus, Share, X } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import { isInstalledApp } from '$lib/hooks/viewport-insets.svelte';
  import { isIosSafari } from '$lib/ios-safari';

  /**
   * The iPhone/iPad half of "use it like an app" — PairDeviceCard is the Android half. Safari
   * never offers to install a web app by itself, so without this the Home Screen app (manifest,
   * icons, offline page and all) is invisible to the people it was built for.
   *
   * Shown only in Safari on iOS/iPadOS, and not inside the installed app itself. Dismissal is a
   * per-browser convenience, so localStorage is enough — and it can throw or come back empty
   * (private mode, blocked storage), in which case the card simply shows again.
   */
  const DISMISS_KEY = 'mh-install-hint-dismissed';

  let visible = $state(false);

  $effect(() => {
    let dismissed = false;
    try {
      dismissed = localStorage.getItem(DISMISS_KEY) === '1';
    } catch {
      // storage unavailable — show the hint
    }
    visible = !dismissed && isIosSafari() && !isInstalledApp();
  });

  function dismiss() {
    visible = false;
    try {
      localStorage.setItem(DISMISS_KEY, '1');
    } catch {
      // hidden for this visit only
    }
  }
</script>

{#if visible}
  <section class="border-border bg-card rounded-lg border">
    <header class="border-border flex items-start gap-3 border-b px-5 py-3.5">
      <div class="min-w-0 flex-1">
        <h2 class="flex items-center gap-2 text-sm font-semibold">
          <HousePlus class="size-4 shrink-0" /> Add MusicHoarder to your Home Screen
        </h2>
        <p class="text-muted-foreground text-xs">
          It opens full screen with its own icon, like an app — no Safari toolbar.
        </p>
      </div>
      <!-- 28px visual, 44px target: the pseudo-element grows the hit area, not the glyph. -->
      <Button
        variant="ghost"
        size="icon-sm"
        class="relative -mr-1.5 shrink-0 after:absolute after:-inset-2"
        aria-label="Dismiss the Home Screen tip"
        onclick={dismiss}
      >
        <X />
      </Button>
    </header>

    <div class="space-y-4 p-5">
      <ol class="list-inside list-decimal space-y-1.5 text-sm">
        <li>
          In Safari, tap
          <Share class="inline size-4 align-text-bottom" aria-hidden="true" />
          <span class="font-medium">Share</span> — in the toolbar, or under ••• when the toolbar is compact.
        </li>
        <li>
          Choose <span class="font-medium">Add to Home Screen</span>, then tap
          <span class="font-medium">Add</span>.
        </li>
      </ol>

      <!-- The one thing iOS will not tell them: a Home Screen web app has its own cookie jar. -->
      <div
        class="border-border bg-secondary/40 rounded-lg border px-4 py-3 text-xs leading-relaxed"
      >
        The Home Screen app keeps its own sign-in, separate from Safari's, so you sign in once more
        inside it. Email sign-in links always open in Safari, so a passkey is the quickest way in:
        add one under Passkeys above, then choose
        <span class="font-medium">Sign in with a passkey</span> in the app.
      </div>
    </div>
  </section>
{/if}
