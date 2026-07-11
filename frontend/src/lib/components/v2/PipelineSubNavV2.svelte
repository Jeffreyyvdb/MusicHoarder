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
  class="no-scrollbar border-border flex shrink-0 items-center overflow-x-auto border-b px-4 py-2 sm:px-7"
  aria-label="Pipeline views"
>
  <!-- Apple-style segmented control (same idiom as the song-panel tabs): a soft
       capsule track with the active segment as a raised pill. The bar stays
       count-less and dimension-stable (constraint) — switching tabs only moves
       the pill, never resizes the bar. -->
  <div class="bg-foreground/5 flex shrink-0 items-center gap-1 rounded-full p-1">
    {#each tabs as tab (tab.id)}
      {@const isActive = tab.id === active}
      <a
        href={tab.href}
        data-active={isActive || undefined}
        aria-current={isActive ? 'page' : undefined}
        class={cn(
          'flex shrink-0 items-center gap-1.5 rounded-full px-3 py-1.5 text-xs font-medium whitespace-nowrap transition-colors sm:px-4 sm:text-[13px]',
          'focus-visible:ring-ring/60 outline-none focus-visible:ring-2',
          isActive
            ? 'bg-background text-foreground shadow-sm'
            : 'text-muted-foreground hover:text-foreground'
        )}
      >
        {#if tab.live && running}
          <span class="bg-primary mh-v2-pulse size-1.5 shrink-0 rounded-full"></span>
        {/if}
        <span>{tab.label}</span>
        {#if tab.count != null}
          <span
            class={cn(
              'rounded-full px-1.5 py-px text-[10.5px] tabular-nums',
              isActive ? 'bg-primary/15 text-primary' : 'bg-muted text-muted-foreground'
            )}
          >{typeof tab.count === 'number' ? tab.count.toLocaleString() : tab.count}</span>
        {/if}
      </a>
    {/each}
  </div>
  <span class="flex-1"></span>
  {#if meta}
    <span class="text-muted-foreground/80 hidden text-[11px] whitespace-nowrap tabular-nums sm:block">{meta}</span>
  {/if}
</nav>
