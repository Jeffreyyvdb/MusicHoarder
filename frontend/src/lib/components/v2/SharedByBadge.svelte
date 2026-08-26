<script lang="ts">
  import { Users } from '@lucide/svelte';
  import type { ApiSong } from '$lib/api-client';
  import { songsStore } from '$lib/stores/songs.svelte';

  /**
   * "Shared by <name>" for a track that belongs to someone else's library.
   *
   * Renders nothing for a song the current account owns, so callers can drop it in unconditionally
   * instead of repeating the ownership check at every call site. The name is looked up per grantor
   * from the songs response — never hard-coded, and never the grantor's email address.
   */
  type Props = {
    song: Pick<ApiSong, 'sharedByUserId'>;
    /** `icon` shows just the glyph with the name in the tooltip — for dense grids. */
    variant?: 'full' | 'icon';
    class?: string;
  };

  const { song, variant = 'full', class: className = '' }: Props = $props();

  // Reads the STORE (a rune), not the api-client's plain module copy, so this re-renders
  // when the songs fetch resolves rather than only when the `song` prop happens to change.
  const grantor = $derived(songsStore.grantorOf(song));
  // A grantor who never set a display name reads as "someone", which is honest and, unlike
  // falling back to their email, does not publish an address to everyone they shared with.
  const name = $derived(grantor?.displayName?.trim() || 'someone');
  const label = $derived(`Shared by ${name}`);
</script>

{#if grantor}
  <span
    class="border-border/70 bg-secondary/50 text-muted-foreground inline-flex max-w-full items-center gap-1 rounded-full border px-2 py-0.5 text-[11px] leading-none {className}"
    title={label}
  >
    <Users class="size-3 shrink-0" aria-hidden="true" />
    {#if variant === 'full'}
      <span class="truncate">{label}</span>
    {:else}
      <span class="sr-only">{label}</span>
    {/if}
  </span>
{/if}
