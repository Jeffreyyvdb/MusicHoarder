<script lang="ts">
  import { page } from '$app/state';
  import { Button } from '$lib/components/ui/button';
  import ThemeToggle from '$lib/components/ThemeToggle.svelte';
  import BrandMark from '$lib/components/BrandMark.svelte';
  import { createPrimaryCta } from '$lib/components/landing/cta.svelte';

  const version = $derived(page.data.appVersion as string | null | undefined);
  const signedIn = $derived(Boolean(page.data.sessionRole));

  const cta = createPrimaryCta({
    signedOutLabel: 'Try the live demo',
    shortSignedOutLabel: 'Live demo'
  });

  // Every tap target in the bar is 44pt on a touch screen (the section anchors only show from sm,
  // where a tablet still has a finger): the controls grow to 44px, the text links grow their hit
  // area with padding rather than their type — 44 tall, and 44 wide for a short word ("Inbox").
  const anchor =
    'text-muted-foreground hover:text-foreground hidden text-[13px] transition-colors sm:inline-flex sm:items-center sm:justify-center pointer-coarse:min-h-11 pointer-coarse:min-w-11 pointer-coarse:px-1';
</script>

<nav
  class="mx-auto flex max-w-[1280px] items-center justify-between px-4 pt-[calc(1.5rem_+_env(safe-area-inset-top))] pb-6 md:px-14"
>
  <a href="/" class="flex min-h-11 items-center gap-2.5 text-[15px] font-semibold tracking-tight">
    <BrandMark class="size-7" />
    <span>MusicHoarder</span>
    {#if version}
      <span
        class="bg-muted text-muted-foreground ml-1 hidden rounded-[4px] px-1.5 py-0.5 font-mono text-[11px] font-normal sm:inline"
      >
        v{version}
      </span>
    {/if}
  </a>

  <div class="flex items-center gap-2 md:gap-5">
    <a
      href="#pipeline"
      class={anchor}
    >
      Pipeline
    </a>
    <a
      href="#inbox"
      class={anchor}
    >
      Inbox
    </a>
    <a
      href="#library"
      class={anchor}
    >
      Library
    </a>
    <a
      href="#quickstart"
      class={anchor}
    >
      Quickstart
    </a>
    <a
      href="#features"
      class={anchor}
    >
      Features
    </a>
    <a
      href="https://github.com/Jeffreyyvdb/MusicHoarder"
      target="_blank"
      rel="noopener noreferrer"
      class={anchor}
    >
      GitHub
    </a>
    <ThemeToggle class="pointer-coarse:size-11" />
    {#if !signedIn}
      <Button
        variant="ghost"
        size="sm"
        href="/login"
        class="h-11 px-3 md:h-7 md:px-2.5"
      >
        Sign in
      </Button>
    {/if}
    <Button
      size="sm"
      onclick={cta.activate}
      disabled={cta.busy}
      class="h-11 rounded-full px-4 md:h-7 md:rounded-lg md:px-2.5"
    >
      <span class="sm:hidden">{cta.shortLabel}</span>
      <span class="hidden sm:inline">{cta.label}</span>
    </Button>
  </div>
</nav>
