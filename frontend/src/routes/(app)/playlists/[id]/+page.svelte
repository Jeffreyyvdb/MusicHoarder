<script lang="ts">
  import { page } from '$app/state';
  import PlaylistDetailV2 from '$lib/components/v2/PlaylistDetailV2.svelte';

  const id = $derived(Number(page.params.id));
</script>

{#key id}
  <PlaylistDetailV2 {id} />
{/key}
