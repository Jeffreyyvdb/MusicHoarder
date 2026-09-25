<script lang="ts">
  import { HousePlus, Share, X } from '@lucide/svelte';
  import { Button } from '$lib/components/ui/button';
  import * as GroupedList from '$lib/components/ui/grouped-list';
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
  <!-- The one thing iOS will not tell them, in the footer: a Home Screen web app has its own
       cookie jar. -->
  <GroupedList.Section
    headingLevel={2}
    header="Home Screen app"
    footer="The Home Screen app keeps its own sign-in, separate from Safari’s, so you sign in once more inside it. Email sign-in links always open in Safari, so a passkey is the quickest way in: add one under Passkeys above, then choose Sign in with a passkey in the app."
  >
    <!-- A tip cell with its own close button (TipKit's shape): the glyph stays 20px, the button
         is a 44pt target in the corner. -->
    <div class="relative px-4 py-3 pr-12">
      <p class="text-headline flex items-center gap-2 md:text-sm md:font-semibold">
        <HousePlus class="size-5 shrink-0" aria-hidden="true" /> Add MusicHoarder to your Home Screen
      </p>
      <p class="text-subheadline text-muted-foreground mt-0.5 md:text-xs">
        It opens full screen with its own icon, like an app — no Safari toolbar.
      </p>
      <ol class="text-subheadline mt-3 list-decimal space-y-1.5 pl-5 md:text-sm">
        <li>
          In Safari, tap
          <Share class="inline size-4 align-text-bottom" aria-hidden="true" />
          <span class="font-semibold">Share</span> — in the toolbar, or under ••• when the toolbar is
          compact.
        </li>
        <li>
          Choose <span class="font-semibold">Add to Home Screen</span>, then tap
          <span class="font-semibold">Add</span>.
        </li>
      </ol>
      <Button
        variant="ghost"
        size="icon"
        class="text-muted-foreground absolute top-0.5 right-0.5 size-11 rounded-full"
        aria-label="Dismiss the Home Screen tip"
        onclick={dismiss}
      >
        <X class="size-5" />
      </Button>
    </div>
  </GroupedList.Section>
{/if}
