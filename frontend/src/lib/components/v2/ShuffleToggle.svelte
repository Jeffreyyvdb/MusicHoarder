<script lang="ts">
  import { Shuffle } from '@lucide/svelte';
  import { Button } from '#lib/components/ui/button/index.ts';
  import type { ShuffleScope } from '#lib/shuffle-prefs.ts';
  import { playerStore } from '#lib/stores/player.svelte.ts';
  import { cn } from '#lib/utils.ts';

  interface Props {
    scope: ShuffleScope;
    /** `pill`: a phone's Play / Shuffle capsules. `sm`: the md+ row. `icon`: an artist's row. */
    appearance: 'pill' | 'sm' | 'icon';
    class?: string;
  }
  const { scope, appearance, class: className }: Props = $props();

  const on = $derived(playerStore.shuffleFor(scope));
</script>

<!-- Spotify's Shuffle: a setting the list remembers, not a way to start it. On reads as a tint and
     a dot under the glyph, so the state is not colour alone. -->
{#snippet glyph(size: string)}
  <span class="relative inline-flex">
    <Shuffle class={size} />
    {#if on}
      <span
        aria-hidden="true"
        class="absolute -bottom-1.5 left-1/2 size-1 -translate-x-1/2 rounded-full bg-current"
      ></span>
    {/if}
  </span>
{/snippet}

{#if appearance === 'icon'}
  <Button
    variant="ghost"
    size="icon-lg"
    class={cn('size-11 rounded-full', on && 'text-primary hover:text-primary', className)}
    aria-label="Shuffle"
    aria-pressed={on}
    title={on ? 'Shuffle on' : 'Shuffle off'}
    onclick={() => playerStore.setShuffleFor(scope, !on)}
  >
    {@render glyph('size-6')}
  </Button>
{:else}
  <Button
    variant={on ? 'tinted' : 'gray'}
    size={appearance === 'pill' ? 'pill' : 'sm'}
    class={cn(appearance === 'sm' && 'h-8 gap-1.5 rounded-full px-3', className)}
    aria-pressed={on}
    title={on ? 'Shuffle on' : 'Shuffle off'}
    onclick={() => playerStore.setShuffleFor(scope, !on)}
  >
    {@render glyph(appearance === 'pill' ? 'size-5' : 'size-4')} Shuffle
  </Button>
{/if}
