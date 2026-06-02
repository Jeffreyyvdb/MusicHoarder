<script lang="ts">
  import { page } from '$app/state';
  import { Inbox, Library, Settings, Workflow, type Icon as IconType } from '@lucide/svelte';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { resolveActiveSection, type ActiveSection } from '$lib/section-nav';
  import { cn } from '$lib/utils';

  // Mobile-only floating bottom bar: one tap per top-level section. Sub-items
  // stay reachable via the top tab bar (SectionSubNav) and Inbox's own tabs, so
  // this carries the four sections only — mirroring the Pipeline/Inbox/Library/
  // Settings entries in AppSidebarV2's NAV array.
  type Item = {
    id: ActiveSection;
    label: string;
    href: string;
    icon: typeof IconType;
    /** Show a live pulse dot while a pipeline job is running. */
    live?: boolean;
  };

  const ITEMS: Item[] = [
    { id: 'pipeline', label: 'Pipeline', href: '/pipeline', icon: Workflow, live: true },
    { id: 'inbox', label: 'Inbox', href: '/inbox', icon: Inbox },
    { id: 'library', label: 'Library', href: '/library', icon: Library },
    { id: 'settings', label: 'Settings', href: '/settings', icon: Settings }
  ];

  const active = $derived(resolveActiveSection(page.url.pathname));
  const running = $derived(pipelineOverlay.isAnyRunning);
</script>

<nav
  aria-label="Primary"
  class="border-border bg-background/95 fixed inset-x-3 bottom-3 z-40 flex items-stretch gap-1 rounded-2xl border p-1.5 shadow-[0_-4px_24px_oklch(0%_0_0/0.08)] backdrop-blur md:hidden dark:shadow-[0_-4px_20px_rgba(0,0,0,0.35)]"
  style="margin-bottom: env(safe-area-inset-bottom, 0);"
>
  {#each ITEMS as item (item.id)}
    {@const isActive = item.id === active}
    <a
      href={item.href}
      data-active={isActive || undefined}
      aria-current={isActive ? 'page' : undefined}
      class={cn(
        'relative flex flex-1 flex-col items-center justify-center gap-1 rounded-xl py-2 transition-colors',
        'text-muted-foreground hover:text-foreground',
        'data-[active=true]:bg-muted data-[active=true]:text-foreground'
      )}
    >
      {#if item.live && running}
        <span class="bg-primary mh-v2-pulse absolute top-1.5 right-1/2 size-1.5 translate-x-3 rounded-full"
        ></span>
      {/if}
      <item.icon class="size-5 shrink-0" />
      <span class="text-[10.5px] leading-none font-medium tracking-[-0.005em]">{item.label}</span>
    </a>
  {/each}
</nav>
