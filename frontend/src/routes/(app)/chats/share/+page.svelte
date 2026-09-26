<script lang="ts">
  import { page } from '$app/state';
  import ChatShareTargetV2 from '$lib/components/chat/ChatShareTargetV2.svelte';
</script>

<!-- Keyed: a second share arriving while this page is open must start over with it. -->
{#key page.url.search}
  <ChatShareTargetV2 />
{/key}
