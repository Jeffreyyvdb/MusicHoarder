<script lang="ts">
  import '../app.css';
  import type { Snippet } from 'svelte';
  import { ModeWatcher, mode } from 'mode-watcher';
  import { onMount } from 'svelte';
  import { afterNavigate, beforeNavigate } from '$app/navigation';
  import { updated } from '$app/state';
  import { toast } from 'svelte-sonner';
  import { Toaster } from '$lib/components/ui/sonner';
  import Analytics from '$lib/components/Analytics.svelte';
  import { clearStaleChunkRecovery } from '$lib/stale-chunk-recovery';
  import { installBottomInsetTracker } from '$lib/hooks/viewport-insets.svelte';

  type Props = { children: Snippet };
  const { children }: Props = $props();

  // A new version is live (detected via version polling). Do a full-page
  // navigation instead of client-side routing so we fetch the fresh build's
  // chunks rather than the deleted ones this tab still references. This still
  // fires silently on the next tap — it's the fallback that lands on the
  // destination the user actually chose and avoids a stale-chunk failure — but
  // the effect below announces the update as soon as it's known, so the reload
  // is chosen (tap Refresh) rather than suffered mid-tap (F28).
  beforeNavigate((nav) => {
    if (updated.current && nav.to?.url && !nav.willUnload) {
      nav.cancel();
      location.href = nav.to.url.href;
    }
  });

  // Distinct from VersionUpdateBanner (which watches the Docker image tag): this is SvelteKit's
  // own build-hash poll, and it's what the `beforeNavigate` guard above acts on. A toast rather
  // than a banner — it needs no shell real estate and stays visible across the eventual navigation
  // — persistent and shown once per session so it doesn't re-stack on every subsequent check.
  let announcedUpdate = false;
  $effect(() => {
    if (!updated.current || announcedUpdate) return;
    announcedUpdate = true;
    toast('A new version of MusicHoarder is ready', {
      duration: Infinity,
      action: { label: 'Refresh', onClick: () => location.reload() }
    });
  });

  // A navigation completed without a stale-chunk failure, so chunks are loading
  // fine — reset the stale-chunk reload budget for the next deploy.
  afterNavigate(() => clearStaleChunkRecovery());

  // Track the browser's bottom chrome (e.g. Chrome Android's bottom address bar)
  // so the floating bottom nav / mini-player never hide behind it.
  onMount(() => installBottomInsetTracker());

  // Hex mirrors of `--background` (app.css) for the theme-color meta: Safari's tab bar, and the
  // status bar of the installed iOS app. app.html sets the initial value before hydration; this
  // follows the theme toggle. Done by hand rather than via ModeWatcher's `themeColors`, which would
  // render a second <meta name="theme-color"> on server-rendered pages next to the static one the
  // client-rendered (app) routes need.
  const THEME_COLOR = { light: '#f8fafd', dark: '#060709' } as const;
  $effect(() => {
    if (!mode.current) return; // not resolved yet — leave app.html's pre-hydration value alone
    const color = mode.current === 'dark' ? THEME_COLOR.dark : THEME_COLOR.light;
    for (const meta of document.querySelectorAll('meta[name="theme-color"]')) {
      meta.setAttribute('content', color);
    }
  });
</script>

<ModeWatcher defaultMode="system" />
<Analytics />
<!-- Top offsets are the library defaults plus the status-bar inset an installed (home-screen) app
     draws under; in a browser tab the inset is 0. -->
<Toaster
  position="top-center"
  richColors
  closeButton
  offset={{ top: 'calc(24px + env(safe-area-inset-top))' }}
  mobileOffset={{ top: 'calc(16px + env(safe-area-inset-top))' }}
/>

{@render children()}
