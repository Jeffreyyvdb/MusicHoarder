<script lang="ts">
  import SeoHead from '#lib/components/SeoHead.svelte';
  import { mode } from 'mode-watcher';
  import { renderMermaidDiagrams } from '#lib/docs/mermaid.ts';
  import type { TocEntry } from '#lib/docs/slugs.ts';

  type PagerEntry = { slug: string; title: string } | null;

  type Props = {
    slug: string;
    docPath: string;
    title: string;
    description: string;
    html: string;
    toc: TocEntry[];
    mermaid: boolean;
    prev: PagerEntry;
    next: PagerEntry;
  };

  const { slug, docPath, title, description, html, toc, mermaid, prev, next }: Props = $props();

  let article = $state<HTMLElement | null>(null);
  let activeHeading = $state<string>('');

  // Diagrams hydrate after mount and after navigation (SvelteKit reuses this component across
  // slug pages, so the effect must track the new HTML, not just mount once), and re-render when
  // the reader flips light/dark.
  $effect(() => {
    if (!mermaid || !article) return;
    void html;
    void renderMermaidDiagrams(article, mode.current === 'dark');
  });

  // Scroll-spy: the topmost heading in view drives the TOC's current entry.
  $effect(() => {
    if (!article || toc.length === 0) return;
    const headings = [...article.querySelectorAll<HTMLElement>('h2[id], h3[id]')];
    if (headings.length === 0) return;
    const visibility = new Map<HTMLElement, boolean>();
    const observer = new IntersectionObserver(
      (entries) => {
        for (const entry of entries)
          visibility.set(entry.target as HTMLElement, entry.isIntersecting);
        const firstVisible = headings.find((heading) => visibility.get(heading));
        activeHeading = firstVisible?.id ?? activeHeading;
      },
      // A heading is "reached" once it crosses the top quarter of the viewport.
      { rootMargin: '-72px 0px -70% 0px', threshold: 0 }
    );
    for (const heading of headings) observer.observe(heading);
    return () => observer.disconnect();
  });

  const githubUrl = $derived(
    `https://github.com/Jeffreyyvdb/MusicHoarder/blob/main/docs/${docPath}`
  );
  const canonicalPath = $derived(slug === '' ? '/docs' : `/docs/${slug}`);
</script>

<SeoHead
  title={`${title} · MusicHoarder Docs`}
  {description}
  path={canonicalPath}
  ogType="article"
/>

<div class="grid gap-12 xl:grid-cols-[minmax(0,1fr)_200px]">
  <article class="min-w-0">
    <div class="docs-prose" bind:this={article}>
      {@html html}
    </div>

    <p class="text-muted-foreground mt-12 text-[13px]">
      Found something wrong?
      <a href={githubUrl} target="_blank" rel="noopener noreferrer">Edit this page on GitHub</a>
      — the docs live in the repository's <code class="bg-muted rounded px-1">docs/</code> folder.
    </p>

    {#if prev || next}
      <nav aria-label="Documentation pager" class="mt-6 grid gap-3 sm:grid-cols-2">
        {#if prev}
          <a
            href={prev.slug === '' ? '/docs' : `/docs/${prev.slug}`}
            class="bg-background-grouped dark:bg-card hover:bg-muted rounded-[18px] px-4 py-3 transition-colors"
          >
            <span
              class="text-muted-foreground block text-[11px] font-semibold tracking-wide uppercase"
              >Previous</span
            >
            <span class="text-foreground mt-0.5 block text-[14px] font-medium">{prev.title}</span>
          </a>
        {/if}
        {#if next}
          <a
            href={next.slug === '' ? '/docs' : `/docs/${next.slug}`}
            class="bg-background-grouped dark:bg-card hover:bg-muted rounded-[18px] px-4 py-3 text-right transition-colors sm:col-start-2"
          >
            <span
              class="text-muted-foreground block text-[11px] font-semibold tracking-wide uppercase"
              >Next</span
            >
            <span class="text-foreground mt-0.5 block text-[14px] font-medium">{next.title}</span>
          </a>
        {/if}
      </nav>
    {/if}
  </article>

  {#if toc.length > 0}
    <aside class="hidden xl:block">
      <nav aria-label="On this page" class="sticky top-20">
        <p class="text-muted-foreground-dim pb-2 text-[11px] font-semibold tracking-wide uppercase">
          On this page
        </p>
        <div class="flex flex-col">
          {#each toc as entry (entry.id)}
            <a
              href="#{entry.id}"
              class="text-muted-foreground hover:text-foreground border-separator border-l py-1 pl-3 text-[13px] leading-[18px] transition-colors {activeHeading ===
              entry.id
                ? 'text-primary border-l-2 font-medium'
                : ''} {entry.depth === 3 ? 'pl-6' : ''}">{entry.text}</a
            >
          {/each}
        </div>
      </nav>
    </aside>
  {/if}
</div>
