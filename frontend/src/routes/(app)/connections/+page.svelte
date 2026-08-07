<script lang="ts">
  import ConnectionsV2 from '$lib/components/v2/ConnectionsV2.svelte';
  import type { PageData } from './$types';

  let { data }: { data: PageData } = $props();
</script>

<ConnectionsV2 isDemo={data.user.role === 'Demo'} />
