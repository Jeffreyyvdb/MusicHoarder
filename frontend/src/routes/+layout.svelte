<script lang="ts">
  import '../app.css';
  import type { Snippet } from 'svelte';
  import { ModeWatcher, mode } from 'mode-watcher';
  import { onMount } from 'svelte';
  import { page, updated } from '$app/state';
  import { toast } from 'svelte-sonner';
  import { Toaster } from '#lib/components/ui/sonner/index.ts';
  import Analytics from '#lib/components/Analytics.svelte';
  import { installBottomInsetTracker } from '#lib/hooks/viewport-insets.svelte.ts';
  import { installDynamicType } from '#lib/dynamic-type.ts';
  import { IsMobile } from '#lib/hooks/is-mobile.svelte.ts';
  import { surfaceFor } from '#lib/nav.ts';
  import { themeSurface } from '#lib/stores/theme-surface.svelte.ts';
  import { installLooping } from '#lib/looping.svelte.ts';
  import {
    createKonamiMatcher,
    hardwareMode,
    HARDWARE_THEME_COLOR
  } from '#lib/hardware/hardware-mode.svelte.ts';

  type Props = { children: Snippet };
  const { children }: Props = $props();

  let announcedUpdate = false;
  $effect(() => {
    if (!updated.current || announcedUpdate) return;
    announcedUpdate = true;
    toast('A new version of MusicHoarder is ready', {
      duration: Infinity,
      action: { label: 'Refresh', onClick: () => location.reload() }
    });
  });

  // Track the browser's bottom chrome (e.g. Chrome Android's bottom address bar)
  // so the floating bottom nav / mini-player never hide behind it.
  onMount(() => installBottomInsetTracker());

  // Looping, the problem reporter: picks up the widget app.html loaded, on a device with a key.
  onMount(() => installLooping());

  // The hardware skin, an easter egg: restore it, and listen for the Konami code that finds it
  // (search finds it on a phone — CommandPalette). Arrow keys typed into a field don't count.
  onMount(() => {
    hardwareMode.init();
    const konami = createKonamiMatcher();
    const onKey = (event: KeyboardEvent) => {
      if (event.metaKey || event.ctrlKey || event.altKey) return;
      const target = event.target as HTMLElement | null;
      if (target?.closest?.('input, textarea, [contenteditable="true"]')) return;
      if (!konami(event.key)) return;
      const on = hardwareMode.unlocked ? hardwareMode.toggle() : hardwareMode.unlock();
      toast(on ? 'Hardware mode' : 'Hardware mode off', {
        description: on
          ? 'SPEC-01 by Shu Ding. Switch it off under Appearance in the account menu.'
          : 'Enter the code again to switch it back on.'
      });
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  });

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
  // Settings — `surfaceFor` in #lib/nav) sits on #F2F2F7 in light mode, and Now Playing's media
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
    if (hardwareMode.active) {
      color = dark || themeSurface.tv ? HARDWARE_THEME_COLOR.dark : HARDWARE_THEME_COLOR.light;
    } else if (themeSurface.media) color = MEDIA;
    else if (inApp && !dark && surfaceFor(page.url, isCompact.current) === 'grouped') {
      color = GROUPED_LIGHT;
    }
    // Safari 26 samples the document canvas instead of theme-color. Keep both in sync,
    // including grouped pages and portaled Now Playing, so chrome and overscroll match.
    document.documentElement.style.setProperty('--mh-page-surface', color);
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
  // Over Now Playing on a phone a top toast would sit on its top bar (grabber, ⌄, the song and ⋯:
  // 12px + 56px, TrackPanel's topBar), and the ⋯ menu was out of reach until it timed out. There it
  // starts below the bar instead, over the art or lyrics.
  const toastMobileTop = $derived(
    themeSurface.media
      ? 'calc(76px + env(safe-area-inset-top))'
      : 'calc(16px + env(safe-area-inset-top))'
  );
</script>

<ModeWatcher defaultMode="system" disableHeadScriptInjection />
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
  mobileOffset={{ top: toastMobileTop, bottom: 'var(--mh-toast-bottom)' }}
/>

{@render children()}
