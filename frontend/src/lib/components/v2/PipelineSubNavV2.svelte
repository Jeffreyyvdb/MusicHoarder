<script lang="ts">
  import type { Component } from 'svelte';
  import { cn } from '$lib/utils';

  type Tab = {
    id: string;
    label: string;
    href: string;
    icon?: Component;
    /** Show a pulse dot (the live conveyor). */
    live?: boolean;
    /** Numeric/string count on the right; null/undefined hides it. */
    count?: number | string | null;
  };

  type Props = {
    tabs: Tab[];
    /** id of the active tab. */
    active: string;
    /** Whether the pipeline is currently running (drives the live pulse). */
    running?: boolean;
    /** Optional right-aligned meta text. */
    meta?: string;
  };

  const { tabs, active, running = false, meta }: Props = $props();
</script>

<nav
  class="border-border flex shrink-0 items-center gap-1 overflow-x-auto border-b px-4 sm:px-7"
  aria-label="Pipeline views"
>
  {#each tabs as tab (tab.id)}
    {@const isActive = tab.id === active}
    {@const Icon = tab.icon}
    <a
      href={tab.href}
      data-active={isActive || undefined}
      class={cn(
        'relative flex shrink-0 items-center gap-1.5 px-2.5 py-2.5 text-[12.5px] whitespace-nowrap transition-colors',
        'after:absolute after:inset-x-2.5 after:bottom-0 after:h-[2px] after:rounded-full after:bg-transparent',
        isActive
          ? 'text-foreground font-medium after:bg-primary'
          : 'text-muted-foreground hover:text-foreground'
      )}
    >
      {#if tab.live && running}
        <span class="bg-primary mh-v2-subdot size-1.5 shrink-0 rounded-full"></span>
      {/if}
      {#if Icon}
        <Icon class="size-3.5" />
      {/if}
      <span>{tab.label}</span>
      {#if tab.count != null}
        <span
          class={cn(
            'rounded-full px-1.5 py-px font-mono text-[10px] tabular-nums',
            isActive ? 'bg-primary/15 text-primary' : 'bg-muted text-muted-foreground'
          )}
        >{typeof tab.count === 'number' ? tab.count.toLocaleString() : tab.count}</span>
      {/if}
    </a>
  {/each}
  <span class="flex-1"></span>
  {#if meta}
    <span class="text-muted-foreground/80 hidden font-mono text-[11px] whitespace-nowrap sm:block">{meta}</span>
  {/if}
</nav>

<style>
  :global(.mh-v2-subdot) {
    box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0.5);
    animation: mh-v2-subdot 2s infinite;
  }
  @keyframes mh-v2-subdot {
    0% {
      box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0.5);
    }
    70% {
      box-shadow: 0 0 0 5px oklch(0.5 0.17 145 / 0);
    }
    100% {
      box-shadow: 0 0 0 0 oklch(0.5 0.17 145 / 0);
    }
  }
</style>
