<script lang="ts">
  import { CheckCircle2, Clock, AlertCircle, Loader2, Eye } from '@lucide/svelte';
  type Props = { status?: string };
  const { status }: Props = $props();
</script>

{#if status === 'complete'}
  <CheckCircle2 class="text-primary size-4" />
{:else if status === 'processing'}
  <Loader2 class="text-chart-2 size-4 animate-spin" />
{:else if status === 'needsreview'}
  <Eye class="size-4 text-amber-600 dark:text-amber-500" />
{:else if status === 'pending'}
  <Clock class="text-muted-foreground size-4" />
{:else if status === 'failed'}
  <AlertCircle class="text-destructive size-4" />
{/if}
