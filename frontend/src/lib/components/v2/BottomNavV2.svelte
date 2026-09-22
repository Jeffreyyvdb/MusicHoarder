<script lang="ts">
  import { page } from '$app/state';
  import { pipelineOverlay } from '$lib/stores/pipeline-overlay.svelte';
  import { navGroupsFor, resolveNav } from '$lib/nav';
  import { inboxBadgeCount } from '$lib/stores/nav-badges.svelte';
  import { cn } from '$lib/utils';

  // Mobile-only floating bottom bar: one tap per group. Items stay reachable via
  // the section tab strip in the top bar (SectionTabsV2) and the off-canvas
  // sidebar, so this carries the group headers only — the same role-filtered
  // groups the sidebar renders, so the two can't disagree about which group a
  // route belongs to.
  const navGroups = $derived(navGroupsFor(page.data.user));
  const active = $derived(resolveNav(page.url)?.group.id ?? null);
  const running = $derived(pipelineOverlay.isAnyRunning);
  // Same predicate AppSidebarV2's Inbox group badge uses — see nav-badges.svelte.ts.
  const inboxBadge = $derived(inboxBadgeCount());
</script>

{#if navGroups.length > 1}
  <!-- A member's audience is narrowed to the Listen group alone (navGroupsFor), and a floating
       tab bar with exactly one destination is chrome for chrome's sake — SectionTabsV2 already
       covers that single group on phone widths, so the bar only renders once there's an actual
       choice to make. -->
  <nav
    aria-label="Primary"
    class="mh-glass border-border bg-background/70 fixed bottom-[calc(0.75rem_+_max(env(safe-area-inset-bottom),var(--mh-vv-bottom,0px)))] left-[max(0.75rem,env(safe-area-inset-left))] right-[max(0.75rem,env(safe-area-inset-right))] z-40 flex items-stretch gap-1 rounded-2xl border p-1.5 shadow-[0_-4px_24px_oklch(0%_0_0/0.08)] backdrop-blur-xl backdrop-saturate-150 md:hidden dark:shadow-[0_-4px_20px_rgba(0,0,0,0.35)]"
  >
    {#each navGroups as group (group.id)}
      {@const isActive = group.id === active}
      {@const live = Boolean(group.live && running)}
      {@const badge = group.id === 'inbox' && inboxBadge != null && inboxBadge > 0 ? inboxBadge : null}
      {@const extras = [
        live ? 'pipeline running' : null,
        badge != null ? `${badge > 99 ? 'more than 99' : badge} items need review` : null
      ].filter((s) => s != null)}
      <a
        href={group.href}
        data-active={isActive || undefined}
        aria-current={isActive ? 'page' : undefined}
        aria-label={extras.length ? `${group.label}, ${extras.join(', ')}` : undefined}
        class={cn(
          'relative flex flex-1 flex-col items-center justify-center gap-1 rounded-xl py-2 transition-colors',
          'text-muted-foreground hover:text-foreground',
          'data-[active=true]:bg-muted data-[active=true]:text-foreground',
          'focus-visible:ring-ring/60 outline-none focus-visible:ring-2'
        )}
      >
        {#if live}
          <span
            class="bg-primary mh-v2-pulse absolute top-1.5 right-1/2 size-1.5 translate-x-3 rounded-full"
            aria-hidden="true"
          ></span>
        {/if}
        {#if badge != null}
          <span
            class="bg-primary text-primary-foreground text-nav-badge absolute top-0.5 right-1/2 grid h-[15px] min-w-[15px] translate-x-[9px] place-items-center rounded-full px-1 leading-none font-semibold tabular-nums"
            aria-hidden="true"
          >{badge > 99 ? '99+' : badge}</span>
        {/if}
        <group.icon class="size-5 shrink-0" aria-hidden="true" />
        <span class="text-nav-count leading-none font-medium tracking-[-0.005em]">{group.label}</span>
      </a>
    {/each}
  </nav>
{/if}
