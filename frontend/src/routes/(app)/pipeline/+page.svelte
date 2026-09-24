<script lang="ts">
  import PipelineHomeV2 from '$lib/components/v2/PipelineHomeV2.svelte';
  import { isDemo } from '$lib/auth/capabilities';
  import type { PageData } from './$types';

  let { data }: { data: PageData } = $props();
</script>

<!-- Gate on the capability helper, never on the raw role word (it carries the legacy vocabulary). -->
<PipelineHomeV2 isDemo={isDemo(data.user)} />
