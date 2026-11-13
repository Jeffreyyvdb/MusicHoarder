<script lang="ts">
  import ProsePageView from '#lib/components/landing/ProsePageView.svelte';
  import JsonLd from '#lib/components/JsonLd.svelte';
  import { contactPage } from '#lib/content/prose-pages.ts';
  import { buildProsePageSchema } from '#lib/components/landing/structured-data.ts';
</script>

<JsonLd data={buildProsePageSchema(contactPage)} />
<ProsePageView page={contactPage} eyebrow="Contact · GitHub" />
