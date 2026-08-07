<script lang="ts">
  import { page } from '$app/state';
  import InboxTagReviewV2 from './inbox/InboxTagReviewV2.svelte';
  import InboxDuplicatesV2 from './inbox/InboxDuplicatesV2.svelte';
  import InboxArtistsV2 from './inbox/InboxArtistsV2.svelte';
  import InboxAlbumsV2 from './inbox/InboxAlbumsV2.svelte';
  import InboxAiFlaggedV2 from './inbox/InboxAiFlaggedV2.svelte';

  type TabId = 'review' | 'dupes' | 'artists' | 'albums' | 'ai';

  // The active queue is driven by ?tab= so the sidebar subitems, the top-bar
  // strip, and browser back/forward all stay in sync. The queue tabs are
  // NAV_GROUPS items (with `inboxTab()` matchers), so the shell's section strip
  // renders them as real links — this component only reads the resulting URL.
  // Falls back to Tag review.
  const tab = $derived.by<TabId>(() => {
    const t = page.url.searchParams.get('tab');
    return t === 'dupes' || t === 'artists' || t === 'albums' || t === 'ai' ? t : 'review';
  });
</script>

<!-- No page toolbar here: the top-bar strip already names both the section and
     the active queue, and each queue draws its own toolbar below. Per-queue
     counts went with the old strip — only the mounted queue ever reported one,
     so four of the five were always blank; the sidebar carries the real
     attention badge. -->
<!-- Body: only the active queue is mounted (keyed so switching resets state). -->
{#if tab === 'review'}
  <InboxTagReviewV2 />
{:else if tab === 'dupes'}
  <InboxDuplicatesV2 />
{:else if tab === 'artists'}
  <InboxArtistsV2 />
{:else if tab === 'albums'}
  <InboxAlbumsV2 />
{:else}
  <InboxAiFlaggedV2 />
{/if}
