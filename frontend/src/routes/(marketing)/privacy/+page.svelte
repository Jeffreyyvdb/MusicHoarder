<script lang="ts">
  import ProsePageView from '#lib/components/landing/ProsePageView.svelte';
  import JsonLd from '#lib/components/JsonLd.svelte';
  import { privacyPage } from '#lib/content/prose-pages.ts';
  import { buildProsePageSchema } from '#lib/components/landing/structured-data.ts';
</script>

<JsonLd data={buildProsePageSchema(privacyPage)} />
<ProsePageView page={privacyPage} eyebrow="Privacy · No tracking" />
