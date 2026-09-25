<script lang="ts">
  import { cn } from '$lib/utils';

  /**
   * A screenshot from `static/screenshots/` (the same files the README embeds). Every image there
   * has a `-1200` sibling, so the browser picks the smaller one on a phone or a 1x screen. `dark`
   * names a variant shown instead in dark mode: both are lazy, and an image that is not displayed
   * is not fetched, so only the one for the current theme loads.
   */
  type Props = {
    name: string;
    alt: string;
    /** Intrinsic size of the full-size file, which also fixes the aspect ratio before it loads. */
    width: number;
    height: number;
    dark?: string;
    sizes?: string;
    class?: string;
  };
  const {
    name,
    alt,
    width,
    height,
    dark,
    sizes = '(min-width: 1200px) 1200px, 100vw',
    class: className = ''
  }: Props = $props();

  const src = (file: string) => `/screenshots/${file}.webp`;
  const srcset = (file: string) =>
    `/screenshots/${file}-1200.webp 1200w, /screenshots/${file}.webp ${width}w`;
</script>

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
