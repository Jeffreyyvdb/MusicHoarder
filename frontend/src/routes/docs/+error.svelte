<script lang="ts">
  import { page } from '$app/state';
  import SeoHead from '#lib/components/SeoHead.svelte';
</script>

<SeoHead
  title="Page not found · MusicHoarder Docs"
  description="That documentation page does not exist."
  path="/docs"
  noindex
/>

<div
  class="bg-background text-foreground flex min-h-[60dvh] flex-col items-center justify-center gap-3 px-4 text-center"
>
  <p class="text-muted-foreground text-[13px] font-semibold tracking-wide uppercase">
    {page.status}
  </p>
  <h1 class="text-[24px] font-bold tracking-[-0.02em]">No documentation page here</h1>
  <p class="text-muted-foreground max-w-[420px] text-[15px] leading-[1.5]">
    {page.error?.message ?? 'That documentation page does not exist.'} Pick one from the navigation, or
    start at the index.
  </p>
  <a href="/docs" class="text-primary mt-1 text-[15px] font-medium hover:underline"
    >Documentation index</a
  >
</div>
