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

<main class="bg-background text-foreground min-h-screen overflow-x-hidden pb-4">
  <LandingNav />

  <article class="mx-auto max-w-[760px] px-6 py-10 md:px-14 md:py-14">
    <div
      class="text-muted-foreground font-mono text-[11px] font-semibold tracking-[0.12em] uppercase"
    >
      {eyebrow}
    </div>
    <h1 class="mt-2 mb-3 text-[clamp(28px,3.4vw,38px)] font-bold tracking-[-0.025em] text-balance">
      {page.title}
    </h1>
    <p class="text-muted-foreground text-[15px] leading-[1.65] text-pretty">
      {page.description}
    </p>

    {#each page.sections as section (section.heading)}
      <section class="mt-11">
        <h2 class="mb-3 text-[19px] font-semibold tracking-[-0.015em]">{section.heading}</h2>

        {#each section.blocks as block, i (i)}
          {#if block.kind === 'paragraph'}
            <p class="text-muted-foreground mt-3 text-[14.5px] leading-[1.7] text-pretty">
              {block.text}
            </p>
          {:else if block.kind === 'list'}
            <ul class="mt-4 flex flex-col gap-2.5">
              {#each block.items as item (item)}
                <li class="text-muted-foreground flex gap-3 text-[14.5px] leading-[1.65]">
                  <span class="bg-primary/40 mt-[9px] size-1.5 shrink-0 rounded-full"></span>
                  <span class="min-w-0">{item}</span>
                </li>
              {/each}
            </ul>
          {:else}
            <ul class="mt-4 flex flex-col gap-2.5">
              {#each block.items as link (link.href)}
                <li class="text-muted-foreground text-[14.5px] leading-[1.65]">
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

    <p class="text-muted-foreground mt-12 font-mono text-[11px]">
      Last updated {page.updated} ·
      <a
        href={markdownPath}
        data-sveltekit-reload
        class="hover:text-foreground underline underline-offset-2">read as markdown</a
      >
    </p>
  </article>

  <Footer />
</main>
