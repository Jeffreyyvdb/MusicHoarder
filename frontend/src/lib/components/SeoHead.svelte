<script lang="ts">
  import { env } from '$env/dynamic/public';

  type Props = {
    title: string;
    description: string;
    path: string;
    ogImage?: string;
    ogImageAlt?: string;
    ogType?: 'website' | 'article';
    noindex?: boolean;
  };

  const {
    title,
    description,
    path,
    ogImage = '/og-image.png',
    ogImageAlt,
    ogType = 'website',
    noindex = false
  }: Props = $props();

  const SITE = env.PUBLIC_SITE_URL ?? 'https://musichoarder.app';
  const canonical = $derived(`${SITE}${path}`);
  const ogUrl = $derived(`${SITE}${ogImage}`);
</script>

<svelte:head>
  <title>{title}</title>
  <meta name="description" content={description} />
  <link rel="canonical" href={canonical} />

  <meta property="og:type" content={ogType} />
  <meta property="og:url" content={canonical} />
  <meta property="og:title" content={title} />
  <meta property="og:description" content={description} />
  <meta property="og:image" content={ogUrl} />
  {#if ogImageAlt}<meta property="og:image:alt" content={ogImageAlt} />{/if}
  <meta property="og:site_name" content="MusicHoarder" />
  <meta property="og:locale" content="en_US" />

  <meta name="twitter:card" content="summary_large_image" />
  <meta name="twitter:title" content={title} />
  <meta name="twitter:description" content={description} />
  <meta name="twitter:image" content={ogUrl} />
  {#if ogImageAlt}<meta name="twitter:image:alt" content={ogImageAlt} />{/if}

  {#if noindex}
    <meta name="robots" content="noindex,nofollow" />
  {:else}
    <meta name="robots" content="index,follow,max-image-preview:large,max-snippet:-1,max-video-preview:-1" />
  {/if}
</svelte:head>
