<script lang="ts">
  import type { Snippet } from 'svelte';

  type Props = {
    title: string;
    caption?: string | null;
    kind: string;
    children: Snippet;
    table?: Snippet;
  };
  let { title, caption, kind, children, table }: Props = $props();

  const headingId = $props.id();
</script>

<section
  aria-labelledby={headingId}
  data-slot="background-panel"
  data-kind={kind}
  class="bg-card mx-4 rounded-xl p-4"
>
  <h3 id={headingId} class="text-subheadline font-semibold md:text-sm">{title}</h3>
  {#if caption}
    <p class="text-footnote text-muted-foreground mt-0.5 md:text-xs">{caption}</p>
  {/if}
  <div class="mt-3">{@render children()}</div>
  {#if table}
    <div class="sr-only">{@render table()}</div>
  {/if}
</section>
