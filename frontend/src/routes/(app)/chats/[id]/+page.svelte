<script lang="ts">
  import { page } from '$app/state';
  import ChatsV2 from '$lib/components/chat/ChatsV2.svelte';
</script>

<ChatsV2 conversationId={page.params.id} />
