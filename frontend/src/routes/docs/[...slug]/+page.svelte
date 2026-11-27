<script lang="ts">
  import DocArticle from '#lib/docs/DocArticle.svelte';
  import type { PageProps } from './$types';

  const { data }: PageProps = $props();
</script>

<DocArticle
  slug={data.slug}
  docPath={data.docPath}
  title={data.title}
  description={data.description}
  html={data.html}
  toc={data.toc}
  mermaid={data.mermaid}
  prev={data.prev}
  next={data.next}
/>
