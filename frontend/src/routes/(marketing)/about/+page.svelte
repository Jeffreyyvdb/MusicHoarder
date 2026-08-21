<script lang="ts">
  import ProsePageView from '$lib/components/landing/ProsePageView.svelte';
  import JsonLd from '$lib/components/JsonLd.svelte';
  import { aboutPage } from '$lib/content/prose-pages';
  import { buildProsePageSchema } from '$lib/components/landing/structured-data';
</script>

<JsonLd data={buildProsePageSchema(aboutPage)} />
<ProsePageView page={aboutPage} eyebrow="ABOUT · OPEN SOURCE" />
