<script lang="ts">
  import '#lib/docs/docs.css';
  import { page } from '$app/state';
  import type { Snippet } from 'svelte';
  import BrandMark from '#lib/components/BrandMark.svelte';
  import ThemeToggle from '#lib/components/ThemeToggle.svelte';
  import { sidebar } from '#lib/docs/content.ts';

  type Props = { children: Snippet };
  const { children }: Props = $props();

  const groups = sidebar();

  // The index page renders the docs README and lives at /docs — its slug is the empty one.
  const activeSlug = $derived(
    page.url.pathname === '/docs' || page.url.pathname === '/docs/'
      ? ''
      : page.url.pathname.replace(/^\/docs\//, '').replace(/\/$/, '')
  );

  const nav = 'block rounded-lg px-3 py-1.5 text-[13px] leading-[18px] transition-colors';
  const navIdle = 'text-muted-foreground hover:text-foreground hover:bg-muted';
  const navActive = 'text-foreground bg-muted font-medium';
</script>

<!--
  Standalone docs shell — deliberately not inside the (marketing) group: those pages prerender a
  session role into LandingNav's call-to-action, and its signed-out action swaps a real owner
  session for a demo one. The docs header has no session actions at all, so a prerendered page is
  always safe to serve.
-->
<div class="bg-background text-foreground min-h-dvh">
  <header
    class="bg-background/80 border-separator sticky top-0 z-40 border-b pt-[env(safe-area-inset-top)] backdrop-blur-xl backdrop-saturate-150"
  >
    <nav
      aria-label="Documentation"
      class="mx-auto flex h-14 max-w-[1200px] items-center justify-between gap-3 px-4 md:h-12 md:px-10"
    >
      <a
        href="/docs"
        class="flex min-h-11 items-center gap-2 text-[15px] font-semibold tracking-tight"
      >
        <BrandMark class="size-6" />
        <span>MusicHoarder</span>
        <span class="text-muted-foreground font-normal">· Docs</span>
      </a>
      <div class="flex items-center gap-1">
        <a
          href="/"
          class="text-muted-foreground hover:text-foreground inline-flex min-h-11 items-center px-1 text-[13px] transition-colors md:min-h-0"
          >Home</a
        >
        <a
          href="/overview"
          class="text-muted-foreground hover:text-foreground inline-flex min-h-11 items-center px-1 text-[13px] transition-colors md:min-h-0"
          >Open app</a
        >
        <ThemeToggle />
      </div>
    </nav>
  </header>

  <div class="mx-auto flex max-w-[1200px] flex-col gap-10 px-4 pt-6 pb-24 md:px-10 lg:flex-row">
    <!-- Mobile: the same nav as a collapsible block. Desktop: sticky sidebar. -->
    <div class="lg:hidden">
      <details class="bg-background-grouped rounded-[18px]">
        <summary
          class="text-muted-foreground flex min-h-11 cursor-pointer items-center px-4 text-[13px] font-semibold select-none"
        >
          Documentation
        </summary>
        <nav aria-label="Documentation pages" class="flex flex-col gap-4 px-2 pb-3">
          {#each groups as group (group.label)}
            <div>
              <p
                class="text-muted-foreground px-3 pb-1 text-[11px] font-semibold tracking-wide uppercase"
              >
                {group.label}
              </p>
              {#each group.pages as entry (entry.slug)}
                <a
                  href={entry.slug === '' ? '/docs' : `/docs/${entry.slug}`}
                  aria-current={activeSlug === entry.slug ? 'page' : undefined}
                  class="{nav} {activeSlug === entry.slug ? navActive : navIdle}"
                >
                  {entry.label}
                </a>
              {/each}
            </div>
          {/each}
        </nav>
      </details>
    </div>

    <aside class="hidden w-[220px] shrink-0 lg:block">
      <nav aria-label="Documentation pages" class="sticky top-20 flex flex-col gap-5">
        {#each groups as group (group.label)}
          <div>
            <p
              class="text-muted-foreground-dim px-3 pb-1 text-[11px] font-semibold tracking-wide uppercase"
            >
              {group.label}
            </p>
            <div class="flex flex-col">
              {#each group.pages as entry (entry.slug)}
                <a
                  href={entry.slug === '' ? '/docs' : `/docs/${entry.slug}`}
                  aria-current={activeSlug === entry.slug ? 'page' : undefined}
                  class="{nav} {activeSlug === entry.slug ? navActive : navIdle}"
                >
                  {entry.label}
                </a>
              {/each}
            </div>
          </div>
        {/each}
      </nav>
    </aside>

    <main class="min-w-0 flex-1">
      {@render children()}
    </main>
  </div>
</div>
