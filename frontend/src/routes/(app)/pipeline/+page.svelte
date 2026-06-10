<script lang="ts">
  import PipelineHomeV2 from '$lib/components/v2/PipelineHomeV2.svelte';
  import type { PageData } from './$types';

  let { data }: { data: PageData } = $props();
</script>

<PipelineHomeV2 isDemo={data.user.role === 'Demo'} />
