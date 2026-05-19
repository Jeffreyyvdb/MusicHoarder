<script lang="ts">
  import { ArrowRight } from '@lucide/svelte';

  type Props = {
    label: string;
    current?: string | null;
    original?: string | null;
  };
  const { label, current, original }: Props = $props();

  const isDifferent = $derived(current !== original && !!original);
</script>

<div class="border-border rounded-lg border p-3">
  <p class="text-muted-foreground mb-2 text-xs font-medium">{label}</p>
  <div class="flex items-center gap-3">
    <div class="min-w-0 flex-1">
      <p class="text-muted-foreground mb-0.5 text-xs">Enriched</p>
      <p class="truncate text-sm {isDifferent ? 'font-medium' : ''}">{current || '—'}</p>
    </div>
    {#if isDifferent}
      <ArrowRight class="text-muted-foreground size-4 shrink-0" />
      <div class="min-w-0 flex-1">
        <p class="text-muted-foreground mb-0.5 text-xs">Original</p>
        <p class="text-muted-foreground truncate text-sm">{original}</p>
      </div>
    {/if}
  </div>
</div>
