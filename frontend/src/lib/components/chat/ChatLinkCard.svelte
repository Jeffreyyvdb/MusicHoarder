<script lang="ts">
  import { Download, Link2, Play } from '@lucide/svelte';
  import type { ChatLink } from '$lib/api-client';
  import { linkCaption } from '$lib/chat/chat-text';
  import { playLibrarySong } from '$lib/chat/play-shared';
  import { cn } from '$lib/utils';

  // A link from outside — a Spotify track or album, a YouTube video — as a card: its artwork, name and
  // artist, opening in the app it came from. When the viewer already has that Spotify track, Play
  // plays their own copy; an administrator can hand a link to "Add from link" to download it.
  type Props = {
    link: ChatLink;
    /** Offered for links the add-from-link sheet understands (admins only). */
    onadd?: (url: string) => void;
    class?: string;
  };
  const { link, onadd, class: className }: Props = $props();

  const caption = $derived(linkCaption(link));
  const wide = $derived(link.provider === 'youtube');
  const canAdd = $derived(
    onadd != null &&
      link.librarySongId == null &&
      ((link.provider === 'spotify' && (link.kind === 'track' || link.kind === 'playlist')) ||
        (link.provider === 'youtube' && (link.kind === 'video' || link.kind === 'playlist')))
  );
  let imageFailed = $state(false);
</script>

<div class={cn('bg-card flex w-full max-w-sm flex-col overflow-hidden rounded-2xl border', className)}>
  <a
    href={link.url}
    target="_blank"
    rel="noopener noreferrer"
    class="focus-visible:ring-ring flex items-center gap-3 p-2.5 outline-none focus-visible:ring-2 focus-visible:ring-inset"
  >
    {#if link.imageUrl && !imageFailed}
      <img
        src={link.imageUrl}
        alt=""
        loading="lazy"
        referrerpolicy="no-referrer"
        class={cn('bg-muted shrink-0 rounded-lg object-cover', wide ? 'h-14 w-24' : 'size-14')}
        onerror={() => (imageFailed = true)}
      />
    {:else}
      <span class="bg-muted text-muted-foreground grid size-14 shrink-0 place-items-center rounded-lg">
        <Link2 class="size-6" aria-hidden="true" />
      </span>
    {/if}
    <span class="min-w-0 flex-1">
      <span class="text-subheadline text-foreground line-clamp-2 font-semibold md:text-sm">
        {link.title ?? link.url}
      </span>
      {#if link.subtitle}
        <span class="text-footnote text-muted-foreground block truncate md:text-xs">{link.subtitle}</span>
      {/if}
      <span class="text-footnote text-muted-foreground block truncate md:text-xs">{caption}</span>
    </span>
  </a>

  {#if link.librarySongId != null || canAdd}
    <div class="flex gap-2 border-t px-2.5 py-2">
      {#if link.librarySongId != null}
        {@const songId = link.librarySongId}
        <button
          type="button"
          class="bg-secondary text-primary hover:bg-secondary-hover focus-visible:ring-ring inline-flex h-11 items-center gap-1.5 rounded-full px-4 text-[15px] font-medium outline-none focus-visible:ring-2 md:h-8 md:px-3 md:text-sm"
          onclick={() => void playLibrarySong(songId)}
        >
          <Play class="size-4 fill-current" aria-hidden="true" /> Play from your library
        </button>
      {:else if canAdd}
        <button
          type="button"
          class="bg-secondary text-primary hover:bg-secondary-hover focus-visible:ring-ring inline-flex h-11 items-center gap-1.5 rounded-full px-4 text-[15px] font-medium outline-none focus-visible:ring-2 md:h-8 md:px-3 md:text-sm"
          onclick={() => onadd?.(link.url)}
        >
          <Download class="size-4" aria-hidden="true" /> Add to library
        </button>
      {/if}
    </div>
  {/if}
</div>
