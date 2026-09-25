<script lang="ts">
  import { page } from '$app/state';
  import { Button } from '$lib/components/ui/button';
  import ThemeToggle from '$lib/components/ThemeToggle.svelte';
  import BrandMark from '$lib/components/BrandMark.svelte';
  import { createPrimaryCta } from '$lib/components/landing/cta.svelte';

  const signedIn = $derived(Boolean(page.data.sessionRole));
  // The section links only exist on the landing page itself; About, Contact and Privacy share this
  // bar, so their links point back at the landing page's sections.
  const onLanding = $derived(page.url.pathname === '/');
  const sectionHref = (id: string) => (onLanding ? `#${id}` : `/#${id}`);

  const cta = createPrimaryCta({
    signedOutLabel: 'Try the live demo',
    shortSignedOutLabel: 'Live demo'
  });

  const sections: ReadonlyArray<{ id: string; label: string }> = [
    { id: 'listen', label: 'Listen' },
    { id: 'organize', label: 'Organize' },
    { id: 'review', label: 'Review' },
    { id: 'grow', label: 'Grow' },
    { id: 'quickstart', label: 'Self-host' }
  ];

  // Every tap target in the bar is 44pt on a touch screen (the section links only show from md,
  // where a tablet still has a finger): the controls grow to 44px, the text links grow their hit
  // area with padding rather than their type.
  const link =
    'text-muted-foreground hover:text-foreground hidden text-[13px] transition-colors md:inline-flex md:items-center md:justify-center pointer-coarse:min-h-11 pointer-coarse:min-w-11 pointer-coarse:px-1';
</script>

<!-- The page's own navigation bar, floating over the content as it scrolls: the one piece of glass
     on the page. `.mh-glass` is the app's accessibility hook — it goes solid under Reduce
     Transparency and Increase Contrast. -->
<header
  class="mh-glass bg-background/80 border-separator sticky top-0 z-40 border-b pt-[env(safe-area-inset-top)] backdrop-blur-xl backdrop-saturate-150"
>
  <nav
    aria-label="Main"
    class="mx-auto flex h-14 max-w-[1200px] items-center justify-between gap-3 px-4 md:h-12 md:px-10"
  >
    <a href="/" class="flex min-h-11 items-center gap-2 text-[17px] font-semibold tracking-tight md:text-[15px]">
      <BrandMark class="size-7 md:size-6" />
      <span>MusicHoarder</span>
    </a>

    <div class="flex items-center gap-1 md:gap-6">
      {#each sections as section (section.id)}
        <a href={sectionHref(section.id)} class={link}>{section.label}</a>
      {/each}
      <a
        href="https://github.com/Jeffreyyvdb/MusicHoarder"
        target="_blank"
        rel="noopener noreferrer"
        class={link}
      >
        GitHub
      </a>
      <ThemeToggle class="pointer-coarse:size-11" />
      {#if !signedIn}
        <Button variant="ghost" size="sm" href="/login" class="h-11 rounded-full px-3 md:h-7">
          Sign in
        </Button>
      {/if}
      <Button
        size="sm"
        onclick={cta.activate}
        disabled={cta.busy}
        class="h-11 rounded-full px-4 md:h-7 md:px-3"
      >
        <span class="sm:hidden">{cta.shortLabel}</span>
        <span class="hidden sm:inline">{cta.label}</span>
      </Button>
    </div>
  </nav>
</header>
