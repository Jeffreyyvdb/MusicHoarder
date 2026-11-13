<script lang="ts">
  import { cn } from '#lib/utils.ts';

  /**
   * A screenshot from `static/screenshots/` (the same files the README embeds). Every image there
   * has a `-1200` sibling, so the browser picks the smaller one on a phone or a 1x screen. `dark`
   * names a variant shown instead in dark mode: both are lazy, and an image that is not displayed
   * is not fetched, so only the one for the current theme loads.
   *
   * `priority` is for the one screenshot that is the page's largest paint (the hero): it loads at
   * once, at high priority. Lazy loading cannot pick the theme's file there, so a `<picture>` picks
   * it from the system scheme, which is what the page follows unless an app theme was set against
   * it. Only then is the other file shown — lazily, so the common case fetches one image.
   */
  type Props = {
    name: string;
    alt: string;
    /** Intrinsic size of the full-size file, which also fixes the aspect ratio before it loads. */
    width: number;
    height: number;
    dark?: string;
    sizes?: string;
    priority?: boolean;
    class?: string;
  };
  const {
    name,
    alt,
    width,
    height,
    dark,
    sizes = '(min-width: 1200px) 1200px, 100vw',
    priority = false,
    class: className = ''
  }: Props = $props();

  const src = (file: string) => `/screenshots/${file}.webp`;
  const srcset = (file: string) =>
    `/screenshots/${file}-1200.webp 1200w, /screenshots/${file}.webp ${width}w`;
</script>

{#if priority && dark}
  <picture class="mh-shot-system block">
    <source media="(prefers-color-scheme: dark)" srcset={srcset(dark)} {sizes} />
    <img
      src={src(name)}
      srcset={srcset(name)}
      {sizes}
      {alt}
      {width}
      {height}
      fetchpriority="high"
      class={cn('h-auto w-full', className)}
    />
  </picture>
  <!-- An app theme set against the system scheme: the other file, fetched only when shown. -->
  <img
    src={src(name)}
    srcset={srcset(name)}
    {sizes}
    {alt}
    {width}
    {height}
    loading="lazy"
    decoding="async"
    class={cn('mh-shot-override-light hidden h-auto w-full', className)}
  />
  <img
    src={src(dark)}
    srcset={srcset(dark)}
    {sizes}
    {alt}
    {width}
    {height}
    loading="lazy"
    decoding="async"
    class={cn('mh-shot-override-dark hidden h-auto w-full', className)}
  />
{:else if priority}
  <img
    src={src(name)}
    srcset={srcset(name)}
    {sizes}
    {alt}
    {width}
    {height}
    fetchpriority="high"
    class={cn('h-auto w-full', className)}
  />
{:else}
  <img
    src={src(name)}
    srcset={srcset(name)}
    {sizes}
    {alt}
    {width}
    {height}
    loading="lazy"
    decoding="async"
    class={cn('h-auto w-full', dark && 'dark:hidden', className)}
  />
  {#if dark}
    <img
      src={src(dark)}
      srcset={srcset(dark)}
      {sizes}
      {alt}
      {width}
      {height}
      loading="lazy"
      decoding="async"
      class={cn('hidden h-auto w-full dark:block', className)}
    />
  {/if}
{/if}

<style>
  /* mode-watcher puts `.dark` on <html> for a dark app theme, whether from the system or chosen. */
  @media (prefers-color-scheme: light) {
    :global(.dark) .mh-shot-system {
      display: none;
    }
    :global(.dark) .mh-shot-override-dark {
      display: block;
    }
  }
  @media (prefers-color-scheme: dark) {
    :global(html:not(.dark)) .mh-shot-system {
      display: none;
    }
    :global(html:not(.dark)) .mh-shot-override-light {
      display: block;
    }
  }
</style>
