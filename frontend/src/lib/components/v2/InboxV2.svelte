<script lang="ts">
  import { page } from '$app/state';
  import { IsMobile } from '$lib/hooks/is-mobile.svelte';
  import { isInboxHub } from '$lib/nav';
  import GroupHubV2 from './GroupHubV2.svelte';
  import InboxTagReviewV2 from './inbox/InboxTagReviewV2.svelte';
  import InboxDuplicatesV2 from './inbox/InboxDuplicatesV2.svelte';
  import InboxArtistsV2 from './inbox/InboxArtistsV2.svelte';
  import InboxAlbumsV2 from './inbox/InboxAlbumsV2.svelte';
  import InboxAiFlaggedV2 from './inbox/InboxAiFlaggedV2.svelte';
  import { publishInboxQueueCount } from '$lib/stores/nav-badges.svelte';

  type TabId = 'review' | 'dupes' | 'artists' | 'albums' | 'ai';

  // The active queue is driven by ?tab= so the sidebar's Inbox items, the phone's Inbox hub and
  // browser back/forward all stay in sync. The queues are NAV_GROUPS items (with `inboxTab()`
  // matchers), so those surfaces render them as real links — this component only reads the
  // resulting URL. Falls back to Tag review.
  const tab = $derived.by<TabId>(() => {
    const t = page.url.searchParams.get('tab');
    return t === 'dupes' || t === 'artists' || t === 'albums' || t === 'ai' ? t : 'review';
  });

  // A phone opens the Inbox tab on the list of queues (the hub); every link into a queue carries
  // ?tab=, and a bare /inbox?song= deep link still means Tag review. A desktop keeps opening on
  // the first queue, with the sidebar listing the rest.
  const isMobile = new IsMobile();
  const showHub = $derived(isMobile.current && isInboxHub(page.url));
</script>

<!-- No page toolbar here: the hub and each queue draw their own nav bar. The counts on the tab
     bar, the sidebar and the hub come from nav-badges.svelte.ts; the mounted queue reports its
     own size there as soon as it loads and after every decision, so the badge follows a Keep or
     Merge at once instead of on the next refresh. Tag review has no callback: its count is the
     songs store's needs-review rows (nav-badges.svelte.ts), not a figure a queue can publish. -->
<!-- Body: the hub, or only the active queue (keyed so switching resets state). -->
{#if showHub}
  <GroupHubV2 group="inbox" />
{:else if tab === 'review'}
  <InboxTagReviewV2 />
{:else if tab === 'dupes'}
  <InboxDuplicatesV2 oncount={(n) => publishInboxQueueCount('dupes', n)} />
{:else if tab === 'artists'}
  <InboxArtistsV2 oncount={(n) => publishInboxQueueCount('artists', n)} />
{:else if tab === 'albums'}
  <InboxAlbumsV2 oncount={(n) => publishInboxQueueCount('albums', n)} />
{:else}
  <InboxAiFlaggedV2 oncount={(n) => publishInboxQueueCount('ai', n)} />
{/if}
