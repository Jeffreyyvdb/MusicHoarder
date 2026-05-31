<script lang="ts">
  import { goto } from '$app/navigation';
  import { page } from '$app/state';
  import { uiVersion } from '$lib/stores/ui-version.svelte';
  import InboxV2 from '$lib/components/v2/InboxV2.svelte';

  // /inbox is a v2-only route. When the design flag is 'v1' it has no equivalent,
  // so redirect to the existing v1 review screen and never dead-end a deep link
  // (carrying any ?song= forward). v2 renders responsively on mobile too, so the
  // gate is just the design flag. The flag is client-only (localStorage) and the
  // (app) group is ssr=false, so guard on mount in an effect.
  const isV2 = $derived(uiVersion.isV2);
  const showInbox = $derived(isV2);

  $effect(() => {
    if (!showInbox) {
      const song = page.url.searchParams.get('song');
      void goto(song ? `/review?song=${song}` : '/review', { replaceState: true });
    }
  });
</script>

{#if showInbox}
  <InboxV2 />
{/if}
