<script lang="ts">
  import { cn } from '$lib/utils';
  import type { Feature } from '$lib/components/landing/landing-content';

  /**
   * Feature tiles from `sm` up; on a phone, one inset grouped list (the app's own phone idiom), so a
   * section's features read as rows instead of a tall stack of cards. The icon sits in the neutral
   * rounded square the app's grouped-list rows use: these are not tappable, so they take no tint.
   * `surface` is the band the grid sits on — a tile (or the list) needs a fill that differs from it:
   * white on grouped; grouped on white, and the dark card on black in dark mode, where both bands
   * are black.
   */
  type Props = {
    items: readonly Feature[];
    surface?: 'plain' | 'grouped';
    columns?: 2 | 3;
    class?: string;
  };
  const { items, surface = 'grouped', columns = 3, class: className = '' }: Props = $props();

  // Spelled out in full: Tailwind only generates classes it finds literally in the source.
  const listFill = $derived(
    surface === 'grouped' ? 'max-sm:bg-card' : 'max-sm:bg-background-grouped max-sm:dark:bg-card'
  );
  const tileFill = $derived(
    surface === 'grouped' ? 'sm:bg-card' : 'sm:bg-background-grouped sm:dark:bg-card'
  );
</script>

<ul
  class={cn(
    'grid grid-cols-1 sm:grid-cols-2 sm:gap-4',
    'max-sm:overflow-hidden max-sm:rounded-[18px]',
    columns === 3 && 'lg:grid-cols-3',
    listFill,
    className
  )}
>
  {#each items as feature (feature.title)}
    {@const Icon = feature.icon}
    <li
      class={cn(
        'relative flex gap-4 px-4 py-4 sm:block sm:rounded-[22px] sm:p-6 md:p-7',
        // The row separator, inset to the text like a grouped list's (16px padding + 44px icon +
        // 16px gap).
        'max-sm:not-first:before:bg-separator max-sm:not-first:before:absolute max-sm:not-first:before:top-0 max-sm:not-first:before:right-0 max-sm:not-first:before:left-[76px] max-sm:not-first:before:h-px',
        tileFill
      )}
    >
      <span
        class="bg-muted text-foreground grid size-11 shrink-0 place-items-center rounded-[12px]"
        aria-hidden="true"
      >
        <Icon class="size-6" />
      </span>
      <div class="min-w-0">
        <h3 class="text-[17px] leading-[22px] font-semibold tracking-[-0.01em] sm:mt-4 sm:text-[19px] sm:leading-[24px]">
          {feature.title}
        </h3>
        <p class="text-muted-foreground mt-1 text-[15px] leading-[20px] text-pretty sm:mt-2 sm:leading-[21px]">
          {feature.body}
        </p>
      </div>
    </li>
  {/each}
</ul>
