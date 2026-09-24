<script lang="ts">
  import '../app.css';
  import type { Snippet } from 'svelte';
  import { ModeWatcher, mode } from 'mode-watcher';
  import { onMount } from 'svelte';
  import { afterNavigate, beforeNavigate } from '$app/navigation';
  import { page, updated } from '$app/state';
  import { toast } from 'svelte-sonner';
  import { Toaster } from '$lib/components/ui/sonner';
  import Analytics from '$lib/components/Analytics.svelte';
  import { clearStaleChunkRecovery } from '$lib/stale-chunk-recovery';
  import { installBottomInsetTracker } from '$lib/hooks/viewport-insets.svelte';
  import { installDynamicType } from '$lib/dynamic-type';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { surfaceFor } from '$lib/nav';
  import { themeSurface } from '$lib/stores/theme-surface.svelte';

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

  // Follow the iPhone's Text Size setting (--mh-dt, which the iOS text-style utilities scale by).
  // A no-op off iOS; app.html has already applied the value before first paint.
  $effect(() => installDynamicType());

  // Hex mirrors of `--background` (app.css) for the theme-color meta: Safari's tab bar, and the
  // status bar of the installed iOS app. app.html sets the initial value before hydration; this
  // follows the theme toggle. Done by hand rather than via ModeWatcher's `themeColors`, which would
  // render a second <meta name="theme-color"> on server-rendered pages next to the static one the
  // client-rendered (app) routes need.
  //
  // Inside the app shell the status bar follows the page's surface as well: a grouped page (a hub,
  // Settings — `surfaceFor` in $lib/nav) sits on #F2F2F7 in light mode, and Now Playing's media
  // appearance is black in either mode (the theme-surface store holds that one override). A cold
  // load of a grouped page still paints one #ffffff frame first — app.html cannot know the
  // surface — which is accepted, like the light launch screen. Landing, login and share pages
  // keep the plain background.
  const THEME_COLOR = { light: '#ffffff', dark: '#000000' } as const;
  const GROUPED_LIGHT = '#f2f2f7';
  const MEDIA = '#000000';
  const isCompact = new IsMobile();
  $effect(() => {
    if (!mode.current) return; // not resolved yet — leave app.html's pre-hydration value alone
    const dark = mode.current === 'dark';
    const inApp = page.route.id?.startsWith('/(app)') ?? false;
    let color: string = dark ? THEME_COLOR.dark : THEME_COLOR.light;
    if (inApp && themeSurface.media) color = MEDIA;
    else if (inApp && !dark && surfaceFor(page.url, isCompact.current) === 'grouped') {
      color = GROUPED_LIGHT;
    }
    for (const meta of document.querySelectorAll('meta[name="theme-color"]')) {
      meta.setAttribute('content', color);
    }
  });

  // Bottom on a phone inside the app shell; top on md+ and outside the shell (landing, login, a
  // share page), which have no bottom chrome to clear. Now Playing (the media claim) keeps them at
  // the top too: its transport and action row fill the bottom of the screen, where the shell's
  // clearance would put a toast right over them.
  const toastPosition = $derived(
    isCompact.current && (page.route.id?.startsWith('/(app)') ?? false) && !themeSurface.media
      ? 'bottom-center'
      : 'top-center'
  );
</script>

<ModeWatcher defaultMode="system" />
<Analytics />
<!-- Top offsets are the library defaults plus the status-bar inset an installed (home-screen) app
     draws under; in a browser tab the inset is 0. On a phone inside the app, toasts rise from the
     bottom instead, just above the tab bar / decision toolbar and the MiniPlayer
     (`--mh-toast-bottom`, app.css): a top toast covered the nav bar's Back, chevrons and More for
     seconds after every Accept or merge. Sonner switches to its own mobile offsets below 600px,
     so the bottom offset is given at both widths. -->
<Toaster
  position={toastPosition}
  richColors
  closeButton
  offset={{ top: 'calc(24px + env(safe-area-inset-top))', bottom: 'var(--mh-toast-bottom)' }}
  mobileOffset={{ top: 'calc(16px + env(safe-area-inset-top))', bottom: 'var(--mh-toast-bottom)' }}
/>

{@render children()}
