<script lang="ts">
  import type { Snippet } from 'svelte';
  import Cover from '$lib/components/file-browser/Cover.svelte';
  import { cn } from '$lib/utils';

  // One row of an Inbox queue list (Tag review, Duplicates, AI flagged).
  //
  // Phone: a Mail-style plain row — 44pt art, a 17pt title over a 15pt status line, an inset
  // hairline from the text to the edge — that pushes the detail (it is a real link). Desktop: the
  // dense row of the 320px list pane, with the selected row held by a tint rule on its leading
  // edge, as before. The same element serves both: the link's click is turned into a URL replace
  // on desktop by the queue's selection (QueueSelection.onRowClick).
  type Props = {
    href: string;
    onclick: (event: MouseEvent) => void;
    /** The desktop pane's current row. */
    selected: boolean;
    compact: boolean;
    /**
     * The row's art: the track's cover when there is one (`url`), else the initials tile drawn
     * from artist and title — so a song looks here as it does in Tracks.
     */
    cover: { artist: string; title: string; url?: string | null };
    title: string;
    /** A title derived from the file name reads as one (monospace). */
    titleMono?: boolean;
    /** Decided rows (Tag review's skipped ones) drop to secondary text — never an opacity. */
    muted?: boolean;
    /** The second line: status glyph + word, then context. */
    detail: Snippet;
    trailing?: Snippet;
  };

  const {
    href,
    onclick,
    selected,
    compact,
    cover,
    title,
    titleMono = false,
    muted = false,
    detail,
    trailing
  }: Props = $props();
</script>

<a
  {href}
  {onclick}
  aria-current={selected && !compact ? 'true' : undefined}
  class={cn(
    'group/row relative flex w-full items-center gap-3 pl-4 text-left outline-none',
    'focus-visible:ring-ring/50 active:bg-accent min-h-[60px] transition-colors duration-100 focus-visible:ring-3 focus-visible:ring-inset',
    'md:hover:bg-accent md:mb-0.5 md:min-h-0 md:gap-2.5 md:rounded-md md:border-l-2 md:border-transparent md:py-2 md:pr-2.5 md:pl-2',
    selected && 'md:border-l-primary md:bg-sidebar-accent md:hover:bg-sidebar-accent'
  )}
>
  <Cover
    artist={cover.artist}
    title={cover.title}
    coverUrl={cover.url ?? null}
    size={compact ? 44 : 40}
    corner={6}
    caption={false}
    dprCap={3}
  />
  <!-- The hairline belongs to the text column, so it starts where the text does (UITableView's
       inset separator) and is dropped under the last row of a group. -->
  <div
    class="after:bg-separator relative flex min-w-0 flex-1 items-center gap-2 self-stretch py-2 pr-4 after:absolute after:inset-x-0 after:bottom-0 after:h-(--hairline) group-last/row:after:hidden md:py-0 md:pr-0 md:after:hidden"
  >
    <div class="min-w-0 flex-1">
      <div
        class={cn(
          'text-body truncate md:text-[13px] md:leading-5 md:font-medium',
          titleMono && 'font-mono md:text-[12px]',
          muted && 'text-muted-foreground'
        )}
      >
        {title}
      </div>
      <div
        class="text-subheadline text-muted-foreground flex min-w-0 items-center gap-1.5 md:text-[11.5px] md:leading-4"
      >
        {@render detail()}
      </div>
    </div>
    {@render trailing?.()}
  </div>
</a>
