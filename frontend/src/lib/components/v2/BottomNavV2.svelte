<script lang="ts">
  import { page } from '$app/state';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { navGroupsFor, resolveNav } from '$lib/nav';
  import { cn } from '$lib/utils';

  // Mobile-only floating bottom bar: one tap per group. Items stay reachable via
  // the section tab strip in the top bar (SectionTabsV2) and the off-canvas
  // sidebar, so this carries the group headers only — the same role-filtered
  // groups the sidebar renders, so the two can't disagree about which group a
  // route belongs to.
  const navGroups = $derived(navGroupsFor(page.data.user?.role));
  const active = $derived(resolveNav(page.url)?.group.id ?? null);
  const running = $derived(pipelineOverlay.isAnyRunning);
</script>

<nav
  aria-label="Primary"
  class="border-border bg-background/70 fixed inset-x-3 bottom-[calc(0.75rem_+_max(env(safe-area-inset-bottom),var(--mh-vv-bottom,0px)))] z-40 flex items-stretch gap-1 rounded-2xl border p-1.5 shadow-[0_-4px_24px_oklch(0%_0_0/0.08)] backdrop-blur-xl backdrop-saturate-150 md:hidden dark:shadow-[0_-4px_20px_rgba(0,0,0,0.35)]"
>
  {#each navGroups as group (group.id)}
    {@const isActive = group.id === active}
    <a
      href={group.href}
      data-active={isActive || undefined}
      aria-current={isActive ? 'page' : undefined}
      class={cn(
        'relative flex flex-1 flex-col items-center justify-center gap-1 rounded-xl py-2 transition-colors',
        'text-muted-foreground hover:text-foreground',
        'data-[active=true]:bg-muted data-[active=true]:text-foreground',
        'focus-visible:ring-ring/60 outline-none focus-visible:ring-2'
      )}
    >
      {#if group.live && running}
        <span class="bg-primary mh-v2-pulse absolute top-1.5 right-1/2 size-1.5 translate-x-3 rounded-full"
        ></span>
      {/if}
      <group.icon class="size-5 shrink-0" />
      <span class="text-nav-count leading-none font-medium tracking-[-0.005em]">{group.label}</span>
    </a>
  {/each}
</nav>
