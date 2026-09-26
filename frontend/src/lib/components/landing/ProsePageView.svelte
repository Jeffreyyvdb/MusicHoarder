<script lang="ts">
  import SeoHead from '$lib/components/SeoHead.svelte';
  import LandingNav from '$lib/components/landing/LandingNav.svelte';
  import Footer from '$lib/components/landing/Footer.svelte';
  import type { ProsePage } from '$lib/content/types';

  type Props = { page: ProsePage; eyebrow: string };
  const { page, eyebrow }: Props = $props();

  /** Markdown sibling of this page, for readers who would rather have the plain-text version. */
  const markdownPath = $derived(`${page.path}.md`);
</script>

<SeoHead title={`${page.title} · MusicHoarder`} description={page.description} path={page.path} />

<!-- The same language as the landing page: a plain band for the article, a grouped band for the
     footer, the tint only on links. `overflow-x-clip` keeps the nav bar sticky. -->
<main class="bg-background text-foreground min-h-dvh overflow-x-clip">
  <LandingNav />

  <article class="mx-auto max-w-[760px] px-4 pt-14 pb-20 md:px-10 md:pt-20 md:pb-28">
    <p class="text-muted-foreground text-[17px] leading-[22px] font-semibold">{eyebrow}</p>
    <h1
      class="mt-2 text-[clamp(34px,5vw,48px)] leading-[1.08] font-bold tracking-[-0.03em] text-balance"
    >
      {page.title}
    </h1>
    <p class="text-muted-foreground mt-5 text-[19px] leading-[1.5] text-pretty md:text-[21px]">
      {page.description}
    </p>

    {#each page.sections as section (section.heading)}
      <section class="mt-12">
        <h2 class="text-[24px] leading-[30px] font-bold tracking-[-0.02em]">{section.heading}</h2>

        {#each section.blocks as block, i (i)}
          {#if block.kind === 'paragraph'}
            <p class="text-muted-foreground mt-4 text-[17px] leading-[1.6] text-pretty">
              {block.text}
            </p>
          {:else if block.kind === 'list'}
            <ul class="mt-5 flex flex-col gap-3">
              {#each block.items as item (item)}
                <li class="text-muted-foreground flex gap-3 text-[17px] leading-[1.55]">
                  <span
                    class="bg-muted-foreground-dim mt-[10px] size-1.5 shrink-0 rounded-full"
                    aria-hidden="true"
                  ></span>
                  <span class="min-w-0">{item}</span>
                </li>
              {/each}
            </ul>
          {:else}
            <ul class="bg-background-grouped mt-5 flex flex-col rounded-[18px] px-5 py-2 dark:bg-card">
              {#each block.items as link (link.href)}
                <li
                  class="border-separator text-muted-foreground border-b py-3 text-[17px] leading-[1.5] last:border-b-0"
                >
                  <a
                    href={link.href}
                    target={link.href.startsWith('/') ? null : '_blank'}
                    rel={link.href.startsWith('/') ? null : 'noopener noreferrer'}
                    class="text-primary font-medium hover:underline">{link.label}</a
                  >{#if link.note}<span>&nbsp;— {link.note}</span>{/if}
                </li>
              {/each}
            </ul>
          {/if}
        {/each}
      </section>
    {/each}

    <p class="text-muted-foreground mt-14 text-[13px] leading-[18px]">
      Last updated {page.updated} ·
      <a
        href={markdownPath}
        data-sveltekit-reload
        class="hover:text-foreground inline-block underline underline-offset-2 pointer-coarse:py-3"
        >Read as Markdown</a
      >
    </p>
  </article>

  <Footer />
</main>
